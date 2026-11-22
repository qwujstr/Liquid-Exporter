use serde::Serialize;
use std::{
    error::Error,
    ffi::OsStr,
    fs::{self, OpenOptions},
    io::{self, Write},
    path::Path,
    sync::atomic::{AtomicU64, Ordering},
};
use unity_asset_binary::{
    asset::{SerializedFile, header::SerializedFileHeader, types::SerializedType},
    file::{UnityFile, load_unity_file, load_unity_file_from_memory},
    object::ObjectHandle,
    reader::{BinaryReader, ByteOrder},
};

type Result<T> = std::result::Result<T, Box<dyn Error>>;

#[derive(Serialize)]
struct ObjectEntry {
    path_id: String,
    class_id: i32,
    size: u32,
    name: Option<String>,
}

#[derive(Serialize)]
struct Member<'a> {
    index: usize,
    name: &'a str,
    unity_version: String,
    has_type_tree: bool,
    objects: Vec<ObjectEntry>,
}

fn members<'a>(file: &'a UnityFile, name: &str) -> Result<Vec<(String, &'a SerializedFile)>> {
    match file {
        UnityFile::SerializedFile(file) => Ok(vec![(name.to_string(), file)]),
        UnityFile::AssetBundle(bundle) => Ok(bundle.assets.iter().enumerate()
            .map(|(index, asset)| (bundle.asset_names.get(index).cloned()
                .unwrap_or_else(|| format!("member-{index}")), asset))
            .collect()),
        UnityFile::WebFile(_) => Err("A Unity serialized file or bundle is required".into()),
    }
}

fn index(input: &Path) -> Result<()> {
    let file = load_unity_file(input)?;
    let name = input.file_name().unwrap_or_default().to_string_lossy();
    let loaded = members(&file, &name)?;
    let members: Vec<_> = loaded.iter().enumerate().map(|(index, (name, file))| {
        let stats = file.statistics();
        let objects = file.object_handles().map(|handle| ObjectEntry {
            path_id: handle.path_id().to_string(),
            class_id: handle.class_id(),
            size: handle.byte_size(),
            name: handle.peek_name().ok().flatten(),
        }).collect();
        Member { index, name, unity_version: stats.unity_version, has_type_tree: stats.has_type_tree, objects }
    }).collect();
    serde_json::to_writer(io::stdout().lock(), &members)?;
    Ok(())
}

fn object(file: &SerializedFile, id: i64) -> Result<ObjectHandle<'_>> {
    file.object_handles().find(|object| object.path_id() == id).ok_or_else(|| {
        format!("Path ID {id} was not found in the selected member").into()
    })
}

fn read_blob(data: &[u8], at: usize, order: ByteOrder) -> Result<(usize, &[u8])> {
    let start = at.checked_add(4).ok_or("TextAsset field offset overflows")?;
    let length: [u8; 4] = data.get(at..start).ok_or("Truncated TextAsset length")?.try_into()?;
    let length = match order {
        ByteOrder::Big => u32::from_be_bytes(length),
        ByteOrder::Little => u32::from_le_bytes(length),
    } as usize;
    let end = start.checked_add(length).ok_or("TextAsset field length overflows")?;
    Ok((start, data.get(start..end).ok_or("Truncated TextAsset payload")?))
}

fn text_payload(handle: ObjectHandle<'_>) -> Result<(usize, &[u8], &[u8])> {
    if handle.class_id() != 49 { return Err("The object is not a TextAsset".into()); }
    let data = handle.raw_data()?;
    let order = handle.file().header.byte_order();
    let (name_at, name) = read_blob(data, 0, order)?;
    std::str::from_utf8(name)?;
    let script_at = (name_at + name.len()).checked_next_multiple_of(4).ok_or("TextAsset name is too long")?;
    let (start, script) = read_blob(data, script_at, order)?;
    let end = start + script.len();
    let padded = end.checked_next_multiple_of(4).ok_or("TextAsset payload is too long")?;
    let suffix = if data.len() == end {
        &[][..]
    } else {
        if data.get(end..padded).is_none_or(|pad| pad.iter().any(|byte| *byte != 0)) {
            return Err("Invalid TextAsset alignment".into());
        }
        let suffix = &data[padded..];
        if !suffix.is_empty() {
            // Unity 5 TextAsset adds a serialized m_PathName after the aligned script.
            if !handle.file().unity_version.starts_with("5.") {
                return Err("Unsupported TextAsset trailing fields".into());
            }
            let (path_start, path) = read_blob(suffix, 0, order)?;
            std::str::from_utf8(path)?;
            let path_end = path_start + path.len();
            let tail_padding = suffix.len().saturating_sub(path_end);
            if tail_padding > 3 || suffix[path_end..].iter().any(|byte| *byte != 0) {
                return Err("Unexpected fields after TextAsset m_PathName".into());
            }
        }
        suffix
    };
    Ok((start, script, suffix))
}

fn extract(input: &Path, member: usize, id: i64, output: &Path, text: bool) -> Result<()> {
    let file = load_unity_file(input)?;
    let members = members(&file, "")?;
    let (_, file) = members.get(member).ok_or("Bundle member index is out of range")?;
    let handle = object(file, id)?;
    let bytes = if text { text_payload(handle)?.1 } else { handle.raw_data()? };
    write_new(output, bytes)
}

fn write_new(path: &Path, bytes: &[u8]) -> Result<()> {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let parent = path.parent().ok_or("Output path has no parent")?;
    if path.try_exists()? {
        return Err("Output file already exists".into());
    }
    let sequence = NEXT.fetch_add(1, Ordering::Relaxed);
    let temp = parent.join(format!(".liquid-{}-{sequence}.tmp", std::process::id()));
    let mut file = OpenOptions::new().create_new(true).write(true).open(&temp)?;
    let prepared = file.write_all(bytes).and_then(|_| file.sync_all());
    drop(file);
    // A same-directory hard link publishes the completed file without replacing
    // another process's output, even when it appeared after try_exists().
    let published = prepared.and_then(|_| fs::hard_link(&temp, path));
    let cleanup = fs::remove_file(&temp);
    published?;
    cleanup?;
    Ok(())
}

// Locate the address and length fields in a modern object table, checking each
// entry against the parser's independent inventory before touching any metadata.
fn object_slot(file: &SerializedFile, id: i64) -> Result<usize> {
    let mut reader = BinaryReader::new(file.data(), ByteOrder::Big);
    let header = SerializedFileHeader::from_reader(&mut reader)?;
    if !(17..=22).contains(&header.version) {
        return Err("Variable-length writing supports SerializedFile versions 17–22".into());
    }
    reader.set_byte_order(header.byte_order());
    reader.read_cstring()?;
    reader.read_i32()?;
    let has_tree = reader.read_bool()?;
    let type_count = usize::try_from(reader.read_i32()?)?;
    if type_count != file.types.len() { return Err("Serialized type count differs".into()); }
    for _ in 0..type_count {
        SerializedType::from_reader(&mut reader, header.version, has_tree, false)?;
    }
    let count = usize::try_from(reader.read_i32()?)?;
    if count != file.objects.len() { return Err("Serialized object count differs".into()); }
    let mut result = None;
    for handle in file.object_handles() {
        reader.align()?;
        let current = reader.read_i64()?;
        let offset = usize::try_from(reader.position())?;
        let relative = if header.version >= 22 { u64::try_from(reader.read_i64()?)? }
            else { u64::from(reader.read_u32()?) };
        let size = reader.read_u32()?;
        let type_index = reader.read_i32()?;
        if current != handle.path_id() || size != handle.byte_size()
            || type_index != handle.info().type_index
            || relative.checked_add(header.data_offset) != Some(handle.byte_start())
            || reader.position() >= header.data_offset
        {
            return Err("Serialized object table differs from the inventory".into());
        }
        if current == id && result.replace(offset).is_some() {
            return Err("Duplicate Path ID in object table".into());
        }
    }
    result.ok_or_else(|| "Object table slot missing".into())
}

fn put_u32(bytes: &mut [u8], at: usize, value: u32, order: ByteOrder) {
    bytes[at..at + 4].copy_from_slice(&match order {
        ByteOrder::Big => value.to_be_bytes(),
        ByteOrder::Little => value.to_le_bytes(),
    });
}

fn put_u64(bytes: &mut [u8], at: usize, value: u64, order: ByteOrder) {
    bytes[at..at + 8].copy_from_slice(&match order {
        ByteOrder::Big => value.to_be_bytes(),
        ByteOrder::Little => value.to_le_bytes(),
    });
}

fn patch_text(input: &Path, id: i64, replacement: &Path, output: &Path) -> Result<()> {
    let source = load_unity_file(input)?;
    let file = source.as_serialized().ok_or("Patch requires a standalone SerializedFile")?;
    let mut bytes = file.data().to_vec();
    if bytes.len() as u64 != file.header.file_size {
        return Err("Declared file length differs from actual input length".into());
    }
    let handle = object(file, id)?;
    let replacement = fs::read(replacement)?;
    let (within_object, old_text, suffix) = text_payload(handle)?;
    let offset = usize::try_from(handle.byte_start())?;
    let end = offset.checked_add(handle.byte_size() as usize).ok_or("Object range overflows")?;
    if offset < usize::try_from(file.header.data_offset)?
        || bytes.get(offset..end) != Some(handle.raw_data()?)
    {
        return Err("Object offset could not be verified against the original file".into());
    }
    for other in file.object_handles().filter(|object| object.path_id() != id && object.byte_size() != 0) {
        let other_end = other.byte_start().checked_add(u64::from(other.byte_size())).ok_or("Object range overflows")?;
        if other.byte_start() < end as u64 && other_end > offset as u64 {
            return Err("Object ranges overlap. Cannot safely write this file".into());
        }
    }
    if replacement.len() == old_text.len() {
        let start = offset + within_object;
        bytes[start..start + replacement.len()].copy_from_slice(&replacement);
    } else {
        let slot = object_slot(file, id)?;
        let order = file.header.byte_order();
        let mut new_object = handle.raw_data()?[..within_object].to_vec();
        put_u32(&mut new_object, within_object - 4, u32::try_from(replacement.len())?, order);
        new_object.extend_from_slice(&replacement);
        let padded = new_object.len().checked_next_multiple_of(4).ok_or("Payload is too large")?;
        new_object.resize(padded, 0);
        new_object.extend_from_slice(suffix);
        let data_offset = usize::try_from(file.header.data_offset)?;
        let relative = (bytes.len() - data_offset).checked_next_multiple_of(8).ok_or("File is too large")?;
        let new_start = data_offset.checked_add(relative).ok_or("File is too large")?;
        bytes.resize(new_start, 0);
        bytes.extend_from_slice(&new_object);
        bytes[offset..end].fill(0);
        let size = bytes.len() as u64;
        if file.header.version >= 22 {
            put_u64(&mut bytes, slot, relative as u64, order);
            put_u32(&mut bytes, slot + 8, u32::try_from(new_object.len())?, order);
            put_u64(&mut bytes, 24, size, ByteOrder::Big);
        } else {
            put_u32(&mut bytes, slot, u32::try_from(relative)?, order);
            put_u32(&mut bytes, slot + 4, u32::try_from(new_object.len())?, order);
            put_u32(&mut bytes, 4, u32::try_from(size)?, ByteOrder::Big);
        }
    }

    let check = load_unity_file_from_memory(bytes.clone())?;
    let check = check.as_serialized().ok_or("Written file is not a SerializedFile")?;
    if check.header.file_size != bytes.len() as u64 || check.objects.len() != file.objects.len()
        || text_payload(object(check, id)?)?.1 != replacement
    {
        return Err("Written TextAsset or file header differs".into());
    }
    for (before, after) in file.object_handles().zip(check.object_handles()) {
        if before.path_id() != after.path_id() || before.class_id() != after.class_id()
            || (before.path_id() != id && (before.byte_start() != after.byte_start()
                || before.raw_data()? != after.raw_data()?))
        {
            return Err("Another object changed during the write".into());
        }
    }
    write_new(output, &bytes)
}

fn number<T: std::str::FromStr>(arg: &OsStr, field: &str) -> Result<T> {
    arg.to_str().and_then(|value| value.parse().ok())
        .ok_or_else(|| format!("Invalid {field}").into())
}

fn run(args: &[std::ffi::OsString]) -> Result<()> {
    let op = args.first().and_then(|arg| arg.to_str());
    match (op, args) {
        (Some("index"), [_, input]) => index(Path::new(input)),
        (Some("raw" | "text"), [_, input, member, id, output]) => extract(
            Path::new(input), number(member, "member index")?, number(id, "Path ID")?, Path::new(output), op == Some("text")),
        (Some("patch-text"), [_, input, id, replacement, output]) =>
            patch_text(Path::new(input), number(id, "Path ID")?, Path::new(replacement), Path::new(output)),
        _ => Err("Usage: liquid-unity index <file> | raw/text <file> <member-index> <path-id> <new-output> | patch-text <file> <path-id> <replacement> <new-output>".into()),
    }
}

fn main() -> std::process::ExitCode {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    match run(&args) {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => { eprintln!("{error}"); std::process::ExitCode::FAILURE }
    }
}

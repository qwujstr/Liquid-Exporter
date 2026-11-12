using System.Buffers.Binary;

namespace Reaper.Core;

/// <summary>Filters object table entries in a private copy, without deserializing object payloads.</summary>
public static class LightingFilter
{
    private static readonly HashSet<int> LightingClasses = [1120, 157, 258, 850595691];

    public static async Task<string> StageAsync(string source, string destination, Action<string> log, CancellationToken token)
    {
        bool directory = Directory.Exists(source);
        string root = directory ? Path.GetFullPath(source) : Path.GetDirectoryName(Path.GetFullPath(source))!;
        Directory.CreateDirectory(destination);
        // Include sibling resources for a single serialized file, since its objects may reference them.
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = directory, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });
        int removed = 0;
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = File.OpenRead(file))
            await using (var output = File.Create(target))
                await input.CopyToAsync(output, token);
            int count = FilterFile(target);
            removed += count;
            if (count > 0) log($"Skipped {count} baked-lighting objects: {relative}");
        }
        log($"Lighting filter: {removed} objects excluded before import. Original game files were not modified.");
        return directory ? destination : Path.Combine(destination, Path.GetFileName(source));
    }

    public static int FilterFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length < 20) return 0;
        byte[] head = new byte[Math.Min(48, file.Length)];
        file.ReadExactly(head);
        if (System.Text.Encoding.ASCII.GetString(head, 0, 7) is "UnityFS" or "UnityRa" or "UnityWe")
            throw new NotSupportedException("Skip lighting data cannot filter compressed bundles. Disable this option or unpack the bundle first.");
        int version = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(8));
        if (version is < 9 or > 30) return 0; // Not a serialized asset file.
        long declaredSize = version >= 22 && head.Length >= 48 ? BinaryPrimitives.ReadInt64BigEndian(head.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        if (declaredSize != file.Length) return 0;
        if (version is < 17 or > 22 || head[16] != 0)
            throw new NotSupportedException($"Skip lighting data requires little-endian serialized files v17–22: {Path.GetFileName(path)} (v{version}).");
        int headerSize = version >= 22 ? 48 : 20;
        int metadataSize = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(version >= 22 ? 20 : 0));
        long dataOffset = version >= 22 ? BinaryPrimitives.ReadInt64BigEndian(head.AsSpan(32)) : BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(12));
        if (metadataSize < 13 || metadataSize > 128 * 1024 * 1024 || headerSize + (long)metadataSize > dataOffset || dataOffset > file.Length)
            throw new InvalidDataException("Invalid serialized file metadata bounds.");
        byte[] metadata = new byte[metadataSize];
        file.Position = headerSize;
        file.ReadExactly(metadata);
        using var stream = new MemoryStream(metadata, false);
        using var reader = new BinaryReader(stream);
        var unity = new List<byte>();
        for (byte c; (c = reader.ReadByte()) != 0;) { unity.Add(c); if (unity.Count > 128) throw new InvalidDataException("Invalid Unity version string."); }
        string unityVersion = System.Text.Encoding.ASCII.GetString(unity.ToArray());
        bool scriptHash = !Version.TryParse(string.Join('.', unityVersion.Split('.').Take(2)), out var parsed) || parsed >= new Version(2018, 3);
        reader.ReadInt32();
        bool tree = reader.ReadBoolean();
        int typeCount = Count(reader.ReadInt32(), 100000);
        int[] classes = new int[typeCount];
        for (int i = 0; i < typeCount; i++)
        {
            int id = classes[i] = reader.ReadInt32();
            reader.ReadBoolean();
            short scriptIndex = reader.ReadInt16();
            Skip((id is -1 or 114 || (scriptHash && scriptIndex >= 0)) ? 32 : 16);
            if (tree)
            {
                int nodes = Count(reader.ReadInt32(), 1000000), strings = Count(reader.ReadInt32(), metadataSize);
                Skip(checked(nodes * (version >= 19 ? 32 : 24) + strings));
                if (version >= 21) Skip(checked(Count(reader.ReadInt32(), 1000000) * 4));
            }
        }
        int countOffset = (int)stream.Position;
        int objectCount = Count(reader.ReadInt32(), 10000000);
        stream.Position = (stream.Position + headerSize + 3) / 4 * 4 - headerSize;
        int tableStart = (int)stream.Position;
        int entrySize = version >= 22 ? 24 : 20;
        var kept = new List<int>();
        for (int i = 0; i < objectCount; i++)
        {
            int start = checked(tableStart + i * entrySize);
            stream.Position = start;
            reader.ReadInt64();
            long offset = version >= 22 ? reader.ReadInt64() : reader.ReadUInt32();
            uint size = reader.ReadUInt32();
            int type = reader.ReadInt32();
            if (type < 0 || type >= typeCount || offset < 0 || size > file.Length - dataOffset - offset)
                throw new InvalidDataException("Invalid serialized object entry.");
            if (!LightingClasses.Contains(classes[type])) kept.Add(start);
        }
        int removed = objectCount - kept.Count;
        if (removed == 0) return 0;
        int suffix = checked(tableStart + objectCount * entrySize);
        byte[] result = new byte[metadata.Length];
        metadata.AsSpan(0, tableStart).CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(countOffset), kept.Count);
        int dest = tableStart;
        foreach (int start in kept) { metadata.AsSpan(start, entrySize).CopyTo(result.AsSpan(dest)); dest += entrySize; }
        metadata.AsSpan(suffix).CopyTo(result.AsSpan(dest));
        // Object offsets and file size stay identical; spare metadata bytes are zero padding.
        file.Position = headerSize;
        file.Write(result);
        return removed;

        void Skip(int size) { if (size < 0 || size > stream.Length - stream.Position) throw new InvalidDataException("Invalid type metadata bounds."); stream.Position += size; }
        static int Count(int n, int max) => n >= 0 && n <= max ? n : throw new InvalidDataException("Invalid serialized metadata count.");
    }
}

using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Fmod5Sharp;

namespace Reaper.Core;

internal static class ModderAudio
{
    public static (byte[] Bytes, string Extension) Decode(AssetsFileInstance file, AssetTypeValueField field, string root)
    {
        var resource = field["m_Resource"];
        byte[] bytes;
        if (resource.IsDummy)
        {
            var data = field["m_AudioData"]; if (data.IsDummy) throw new NotSupportedException("No readable AudioClip resource data.");
            bytes = data.AsByteArray;
        }
        else
        {
            string source = resource["m_Source"].AsString;
            ulong offset = resource["m_Offset"].AsULong, size = resource["m_Size"].AsULong;
            if (size > 512L * 1024 * 1024 || offset > long.MaxValue) throw new IOException("Audio resource is too large.");
            string name = Path.GetFileName(source);
            var bundled = file.parentBundle?.file.BlockAndDirInfo.DirectoryInfos.FirstOrDefault(e => e.Name == name || e.Name == source);
            if (bundled is not null)
            {
                if (offset > (ulong)bundled.DecompressedSize || size > (ulong)bundled.DecompressedSize - offset) throw new IOException("Audio resource bounds are invalid.");
                var reader = file.parentBundle!.file.DataReader; reader.Position = bundled.Offset + (long)offset; bytes = reader.ReadBytes((int)size);
            }
            else
            {
                string directory = Path.GetDirectoryName(file.parentBundle?.path ?? file.path)!;
                string path = Path.GetFullPath(Path.Combine(directory, source.StartsWith("archive:/", StringComparison.OrdinalIgnoreCase) ? name : source));
                if (!InputValidation.IsInside(path, root) || !File.Exists(path)) path = Path.Combine(directory, name);
                if (!InputValidation.IsInside(path, root) || !File.Exists(path)) throw new FileNotFoundException("Audio resource file is missing: " + name);
                using var stream = File.OpenRead(path);
                if (offset > (ulong)stream.Length || size > (ulong)stream.Length - offset) throw new IOException("Audio resource bounds are invalid.");
                stream.Position = (long)offset; bytes = new byte[(int)size]; stream.ReadExactly(bytes);
            }
        }
        if (bytes.AsSpan().StartsWith("RIFF"u8)) return (bytes, "wav");
        if (bytes.AsSpan().StartsWith("OggS"u8)) return (bytes, "ogg");
        if (!FsbLoader.TryLoadFsbFromByteArray(bytes, out var bank) || bank is null || bank.Samples.Count == 0) throw new NotSupportedException("The AudioClip compression could not be decoded.");
        bank.Samples[0].RebuildAsStandardFileFormat(out byte[]? output, out string? extension);
        if (output is not { Length: > 0 } || extension is not ("wav" or "ogg" or "mp3")) throw new NotSupportedException("Audio output is not a supported WAV, OGG or MP3 stream.");
        return (output, extension);
    }
}

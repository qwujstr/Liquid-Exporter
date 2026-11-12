namespace Reaper.Core;

public sealed record MetadataInspection(bool Valid, int? Version, string Header, long Size, string Format = "standard", bool RequiresNormalization = false)
{
    public static MetadataInspection Read(string path, string binaryPath = "")
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        int count = stream.Read(header);
        string hex = Convert.ToHexString(header[..Math.Min(count, 4)]);
        int? version = count == 8 ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[4..]) : null;
        if (count == 8 && hex == "2C3BC591" && ProtectedMetadataProfiles.Matches(path, binaryPath))
            return new(true, 31, hex, stream.Length, ProtectedMetadataProfiles.PixelWorlds, true);
        return new(count == 8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xFAB11BAF && version is >=16 and <=31,
            version, hex, stream.Length);
    }
}

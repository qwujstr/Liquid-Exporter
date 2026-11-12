using System.Security.Cryptography;

namespace Reaper.Core;

public static class ProtectedMetadataProfiles
{
    public const string PixelWorlds = "pixel-worlds-win64-6000.3.27f2";
    private const string MetadataHash = "00AB67AD1CA823F11E2046960F7F54525AAB9B0114D183909D655785AE6A2C55";
    private const string BinaryHash = "45FD9CC18B12EF19C6D3D8AE0CB96DEEBA08DFE8FA8DCCCC745B76084BC21971";
    public static bool Matches(byte[] metadata, byte[] binary) => metadata.Length == 18283920 && binary.Length == 108783616
        && Convert.ToHexString(SHA256.HashData(metadata)) == MetadataHash && Convert.ToHexString(SHA256.HashData(binary)) == BinaryHash;
    public static bool Matches(string metadata, string binary)
    {
        if (!File.Exists(metadata) || !File.Exists(binary) || new FileInfo(metadata).Length != 18283920 || new FileInfo(binary).Length != 108783616) return false;
        using var meta = File.OpenRead(metadata); using var native = File.OpenRead(binary);
        return Convert.ToHexString(SHA256.HashData(meta)) == MetadataHash && Convert.ToHexString(SHA256.HashData(native)) == BinaryHash;
    }
}

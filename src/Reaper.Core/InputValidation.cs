namespace Reaper.Core;

public static class InputValidation
{
    public static ExportRequest ResolveInputs(ExportRequest request)
    {
        if (request.NeedsScripts && !string.IsNullOrWhiteSpace(request.GamePath) &&
            (string.IsNullOrWhiteSpace(request.BinaryPath) || string.IsNullOrWhiteSpace(request.MetadataPath)))
        {
            var found = DetectIl2Cpp(request.GamePath);
            return request with { BinaryPath = string.IsNullOrWhiteSpace(request.BinaryPath) ? found.Binary ?? "" : request.BinaryPath,
                MetadataPath = string.IsNullOrWhiteSpace(request.MetadataPath) ? found.Metadata ?? "" : request.MetadataPath };
        }
        return request;
    }
    public static void Validate(ExportRequest request)
    {
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentException("Unknown export mode.");
        if (string.IsNullOrWhiteSpace(request.OutputDirectory)) throw new ArgumentException("Choose an output folder.");
        string output = Path.GetFullPath(request.OutputDirectory);
        if (request.NeedsAssetEngine)
        {
            if (!Directory.Exists(request.GamePath) && !File.Exists(request.GamePath))
                throw new ArgumentException("Game folder or file not found.");
            if (!File.Exists(request.AssetRipperPath)) throw new ArgumentException("Project export engine is missing. Run setup.ps1.");
            string source = Directory.Exists(request.GamePath) ? request.GamePath : Path.GetDirectoryName(Path.GetFullPath(request.GamePath))!;
            if (IsInside(output, source) || IsInside(source, output))
                throw new ArgumentException("Game and output folders must not contain each other. Choose a separate output folder.");
        }
        if (request.Mode == ExportMode.Assets && (request.AssetTypes is not { Length: > 0 } || request.AssetTypes.Any(t => !SpecificAssetExporter.SupportedTypes.Contains(t))))
            throw new ArgumentException("Select at least one supported Unity asset category to export.");
        if (request.NeedsScripts)
        {
            if (!File.Exists(request.BinaryPath)) throw new ArgumentException("Native IL2CPP file not found (GameAssembly.dll / libil2cpp.so).");
            if (!File.Exists(request.MetadataPath)) throw new ArgumentException("global-metadata.dat not found.");
            if (!File.Exists(request.WorkerPath)) throw new ArgumentException("Reaper.Worker.exe not found. Run build.ps1.");
            var inspection = MetadataInspection.Read(request.MetadataPath, request.BinaryPath);
            if (!inspection.Valid)
                throw new ArgumentException("Metadata has no standard IL2CPP header. This may be a protected, incompatible or incorrect file. Select matching standard metadata; replacing the header alone cannot recover its structure.");
            if (inspection.RequiresNormalization && request.ForceBinaryVersion is not (null or 31)) throw new ArgumentException("The verified Pixel Worlds profile requires binary version 31.");
            bool code = !string.IsNullOrWhiteSpace(request.CodeRegistration);
            bool meta = !string.IsNullOrWhiteSpace(request.MetadataRegistration);
            if (code != meta) throw new ArgumentException("Provide both registration addresses together.");
            if (code) { ParseAddress(request.CodeRegistration); ParseAddress(request.MetadataRegistration); }
            if (request.ForceBinaryVersion is double v && (!double.IsFinite(v) || v < 16 || v > 31))
                throw new ArgumentException("Forced binary version must be between 16 and 31.");
            if (IsInside(request.BinaryPath, output) || IsInside(request.MetadataPath, output))
                throw new ArgumentException("Source IL2CPP files must be outside the output folder.");
        }
    }

    public static ulong ParseAddress(string value)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (!ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out ulong result) || result == 0)
            throw new ArgumentException("Registration address must be a nonzero hexadecimal VA.");
        return result;
    }

    public static bool IsInside(string candidate, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static (string? Binary, string? Metadata) DetectIl2Cpp(string gamePath)
    {
        string? root = Directory.Exists(gamePath) ? gamePath : Path.GetDirectoryName(gamePath);
        if (root is null || !Directory.Exists(root)) return (null, null);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        string? binary = null, metadata = null;
        foreach (string path in Directory.EnumerateFiles(root, "*", options))
        {
            string name = Path.GetFileName(path);
            if (name.Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase)) metadata ??= path;
            if (name.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) binary ??= path;
            if (name.Equals("libil2cpp.so", StringComparison.OrdinalIgnoreCase))
            {
                if (binary is null || path.Contains("arm64-v8a", StringComparison.OrdinalIgnoreCase)) binary = path;
            }
        }
        return (binary, metadata);
    }
}

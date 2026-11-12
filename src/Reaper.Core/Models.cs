using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reaper.Core;

public enum ExportMode { Project, Scripts, Both, Assets }
public sealed record ExportRequest
{
    public ExportMode Mode { get; init; } = ExportMode.Project;
    public string Language { get; init; } = "en";
    public bool SkipLightingData { get; init; }
    public string GamePath { get; init; } = "";
    public string BinaryPath { get; init; } = "";
    public string MetadataPath { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public string AssetRipperPath { get; init; } = "";
    public string WorkerPath { get; init; } = "";
    public bool RecoverSimpleMethods { get; init; } = true;
    public bool ExperimentalRecovery { get; init; } = true;
    public bool IncludeEngineAssemblies { get; init; }
    public bool IncludeStreamingAssets { get; init; }
    public bool GenerateAnalysisFiles { get; init; }
    public string CodeRegistration { get; init; } = "";
    public string MetadataRegistration { get; init; } = "";
    public double? ForceBinaryVersion { get; init; }
    public string[] AssetTypes { get; init; } = ["Texture2D", "Sprite", "TextAsset"];
    public bool NeedsAssetEngine => NeedsProject || Mode == ExportMode.Assets;
    public bool NeedsProject => Mode is ExportMode.Project or ExportMode.Both;
    public bool NeedsScripts => Mode is ExportMode.Scripts or ExportMode.Both || Mode == ExportMode.Assets && AssetTypes?.Contains("MonoScript") == true;
}

public sealed record WorkerRequest(string BinaryPath, string MetadataPath, string OutputDirectory,
    bool RecoverSimpleMethods, bool IncludeEngineAssemblies, bool GenerateAnalysisFiles,
    string CodeRegistration, string MetadataRegistration, double? ForceBinaryVersion, bool ExperimentalRecovery = false);

public sealed record ProgressUpdate(string Stage, string Message, int Percent);
public sealed record MethodRecovery(string Assembly, string Type, string Method, string Token,
    string? Rva, string Status, string Reason, string Backend = "Il2CppDumper", string[]? Diagnostics = null, string? NativeAnalysis = null, string Confidence = "unverified");
public sealed record ScriptReport(string BinaryFormat, double MetadataVersion, double BinaryVersion,
    int Assemblies, int SourceFiles, int RecoveredMethods, int UnrecoveredMethods,
    List<MethodRecovery> Methods, List<string> Warnings, int PartialMethods = 0, int SignatureOnlyMethods = 0, string PrimaryBackend = "Il2CppDumper", bool CacheHit = false);
public sealed class JobReport
{
    public string Status { get; set; } = "running";
    public string Stage { get; set; } = "validation";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public required ExportRequest Request { get; init; }
    public required string OutputPath { get; init; }
    public string? Error { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> CompletedStages { get; } = [];
}

public static class JsonFiles
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty JSON: {path}");
    public static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}

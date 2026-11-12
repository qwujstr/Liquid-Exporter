using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Reaper.Core;
using NativeMethod = AsmResolver.DotNet.MethodDefinition;

namespace Reaper.Worker;

public sealed record CppMethodEvidence(string Assembly, uint Token, ulong Address, string Signature, string[] Warnings);
public sealed record CppBackendResult(string AssembliesPath, List<CppMethodEvidence> Methods, bool CacheHit);

internal static class CppCoreBackend
{
    private static bool initialized;
    public const string Revision = "liquid-core-fusion-v1";
    public static string CacheRoot => Environment.GetEnvironmentVariable("LIQUID_CACHE_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Il2cppLiquid", "Cache");
    public static string Fingerprint(byte[] binary, byte[] metadata) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(binary)) + Convert.ToHexString(SHA256.HashData(metadata)) + Revision
            + typeof(Cpp2IlApi).Assembly.ManifestModule.ModuleVersionId)));
    public static CppBackendResult Run(byte[] binary, byte[] metadata, string binaryPath, string analysis, Action<int,string> progress)
    {
        string key = Fingerprint(binary, metadata);
        string cache = Path.Combine(CacheRoot, key, "cpp2il");
        string manifest = Path.Combine(cache, "complete.json");
        if (File.Exists(manifest))
        {
            var saved = JsonFiles.Read<CppBackendResult>(manifest);
            if (Directory.Exists(saved.AssembliesPath) && Directory.EnumerateFiles(saved.AssembliesPath, "*.dll").Any())
            { progress(29, "Cpp2IL reconstruction cache loaded"); return saved with { CacheHit = true }; }
        }
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(analysis);
        using var writer = new StreamWriter(Path.Combine(analysis, "liquid-cpp2il.log")) { AutoFlush = true };
        object gate = new();
        void Log(string message, string source) { lock(gate) writer.Write("[" + source + "] " + message); }
        Logger.InfoLog += Log; Logger.WarningLog += Log; Logger.ErrorLog += Log;
        try
        {
            if (!initialized) { new Cpp2IlCorePlugin().OnLoad(); initialized = true; }
            progress(21, "Cpp2IL: metadata and native application model");
            var unity = UnityVersion.Parse(ReadUnityVersion(binaryPath));
            Cpp2IlApi.RuntimeOptions = new Cpp2IlRuntimeArgs { Valid = true, UnityVersion = unity, LowMemoryMode = true };
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, unity);
            var context = Cpp2IlApi.CurrentAppContext!;
            // Upstream's supported processing layer preserves original tokens/RVAs
            // so Cecil can correlate its output with the Dumper metadata model.
            new AttributeInjectorProcessingLayer().Process(context);
            progress(24, "Cpp2IL: ISIL, SSA, control flow and CIL generation");
            using var format = new ReportingFormat(Path.Combine(analysis, "liquid-method-checkpoints.jsonl"), progress);
            string assemblies = Path.Combine(cache, "Assemblies");
            format.DoOutput(context, assemblies);
            var result = new CppBackendResult(assemblies, format.Evidence.Values.OrderBy(e=>e.Assembly).ThenBy(e=>e.Token).ToList(), false);
            JsonFiles.Write(manifest, result);
            return result;
        }
        finally
        {
            Logger.InfoLog -= Log; Logger.WarningLog -= Log; Logger.ErrorLog -= Log;
            Cpp2IlApi.ResetInternalState();
        }
    }
    internal static string ReadUnityVersion(string binary)
    {
        string version = FileVersionInfo.GetVersionInfo(binary).FileVersion ?? "";
        if(System.Text.RegularExpressions.Regex.IsMatch(version,@"^\d+\.\d+\.\d+[abcfpx]\d+$"))return version;
        string root=Path.GetDirectoryName(binary)!;
        foreach(string data in Directory.EnumerateDirectories(root,"*_Data"))
        {
            string managers=Path.Combine(data,"globalgamemanagers");
            if(!File.Exists(managers))continue;
            using var stream=File.OpenRead(managers);byte[] prefix=new byte[256];int count=stream.Read(prefix);
            var match=System.Text.RegularExpressions.Regex.Match(Encoding.ASCII.GetString(prefix,0,count),@"\d+\.\d+\.\d+[abcfpx]\d+");
            if(match.Success)return match.Value;
        }
        // A controlled fixture can supply its version without changing normal UI controls.
        string supplied=Environment.GetEnvironmentVariable("LIQUID_UNITY_VERSION");
        if(!string.IsNullOrWhiteSpace(supplied))return supplied;
        throw new IOException("Unity version was not found in the game files.");
    }
    private sealed class ReportingFormat(string checkpoint, Action<int,string> progress) : AsmResolverDllOutputFormatIlRecovery, IDisposable
    {
        public readonly ConcurrentDictionary<string,CppMethodEvidence> Evidence=new();
        private readonly object writeLock=new();
        private readonly StreamWriter checkpoints = new(checkpoint);
        public void Dispose() => checkpoints.Dispose();
        private int completed;
        protected override void FillMethodBody(NativeMethod target, MethodAnalysisContext method)
        {
            base.FillMethodBody(target,method);
            if(method.Definition is null || method.DeclaringType is null)return;
            var evidence=new CppMethodEvidence(method.DeclaringType.DeclaringAssembly.Name,method.Definition.token,
                method.UnderlyingPointer,target.FullName,method.AnalysisWarnings.ToArray());
            Evidence[evidence.Assembly+":"+evidence.Token]=evidence;
            int count=Interlocked.Increment(ref completed);
            lock(writeLock)
            {
                checkpoints.WriteLine(System.Text.Json.JsonSerializer.Serialize(evidence));
                if(count%1000==0)checkpoints.Flush();
                if(count%10000==0)progress(25+Math.Min(4,count/30000),"Cpp2IL: analyzed "+count+" methods");
            }
        }
    }
}
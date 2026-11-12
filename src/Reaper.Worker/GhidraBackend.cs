using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Reaper.Core;

namespace Reaper.Worker;

public sealed record GhidraCandidate(MethodDefinition Method, ulong Address, string Key);
public sealed record GhidraEvidence(string Address, bool Completed, string Error, string C, bool PureScalarCandidate,
    JsonElement ReturnExpression, JsonElement Pcode, JsonElement Blocks);
public sealed record GhidraBatchResult(Dictionary<ulong,string> Evidence, int Requested, int Cached, int Deferred);

public static class GhidraBackend
{
    public static GhidraBatchResult Analyze(string binary, string fingerprint, IEnumerable<GhidraCandidate> methods,
        string analysis, Action<int,string> progress)
    {
        var candidates=methods.GroupBy(m=>m.Address).Select(g=>g.First()).ToArray();
        string script=Path.Combine(AppContext.BaseDirectory,"ghidra","LiquidAnalyze.java");
        if(!File.Exists(script))script=FindUp("tools/ghidra/LiquidAnalyze.java");
        if(script is null)throw new IOException("Ghidra integration script is missing.");
        string revision=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(script)))[..16];
        string cache=Path.Combine(CppCoreBackend.CacheRoot,fingerprint,"ghidra-12.2-"+revision);
        Directory.CreateDirectory(cache);Directory.CreateDirectory(analysis);
        var existing=Directory.EnumerateFiles(cache,"*.json").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int budget=64;
        if(int.TryParse(Environment.GetEnvironmentVariable("LIQUID_GHIDRA_BUDGET"),out int configured))budget=Math.Clamp(configured,0,512);
        var pending=candidates.Where(c=>!existing.Contains(c.Address.ToString("X"))).Take(budget).ToArray();
        int cached=candidates.Count(c=>existing.Contains(c.Address.ToString("X")));
        bool timedOut=false;
        if(pending.Length>0)
        {
            string ghidra=Environment.GetEnvironmentVariable("LIQUID_GHIDRA_HOME")??FindUp("engines/Ghidra/ghidra_12.2_PUBLIC");
            if(ghidra is null)throw new IOException("Bundled Ghidra 12.2 is missing. Run setup-ghidra.ps1 before building.");
            string java=FindJava();
            string work=Path.Combine(cache,"work");Directory.CreateDirectory(work);
            string requests=Path.Combine(work,"requests.json");
            JsonFiles.Write(requests,pending.Select(c=>new {Address="0x"+c.Address.ToString("X"),c.Key,
                Signature=c.Method.FullName,ReturnSize=Size(c.Method.ReturnType),ReturnFloating=Floating(c.Method.ReturnType),
                Parameters=(c.Method.IsStatic?Array.Empty<TypeReference>():new TypeReference[]{c.Method.DeclaringType})
                    .Concat(c.Method.Parameters.Select(p=>p.ParameterType)).Select(t=>new {Size=Size(t),Floating=Floating(t)})}));
            var start=new ProcessStartInfo(java){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=work};
            foreach(string arg in new[]{"-Djava.system.class.loader=ghidra.GhidraClassLoader","-Dfile.encoding=UTF8","-Duser.language=en",
                "-Dapplication.settingsdir="+Path.Combine(cache,"settings"),"-Dapplication.cachedir="+Path.Combine(cache,"cache"),"-Dapplication.tempdir="+Path.Combine(cache,"temp"),
                "--enable-native-access=ALL-UNNAMED","--sun-misc-unsafe-memory-access=allow","-Xshare:off","-Xmx2G","-cp",
                Path.Combine(ghidra,"Ghidra/Framework/Utility/lib/Utility.jar"),"ghidra.Ghidra","ghidra.app.util.headless.AnalyzeHeadless",
                work,"Liquid_"+Guid.NewGuid().ToString("N"),"-import",binary,"-noanalysis","-max-cpu","2","-scriptPath",Path.GetDirectoryName(script),
                "-postScript","LiquidAnalyze.java",requests,cache,"-deleteProject"})start.ArgumentList.Add(arg);
            progress(52,"Ghidra: analyzing "+pending.Length+" unresolved native functions");
            using var process=Process.Start(start)??throw new IOException("Unable to start Ghidra.");
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(300000)){timedOut=true;process.Kill(true);process.WaitForExit(5000);}
            File.WriteAllText(Path.Combine(analysis,"liquid-ghidra.log"),stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult());
            if(!pending.Any(c=>File.Exists(Path.Combine(cache,c.Address.ToString("X")+".json"))))
                throw new IOException("Ghidra produced no method evidence. See Analysis/liquid-ghidra.log.");
        }
        var found=new Dictionary<ulong,string>();
        string exported=Path.Combine(analysis,"Native");Directory.CreateDirectory(exported);
        foreach(var candidate in candidates)
        {
            string path=Path.Combine(cache,candidate.Address.ToString("X")+".json");
            if(!File.Exists(path))continue;
            string destination=Path.Combine(exported,Path.GetFileName(path));File.Copy(path,destination,true);
            var evidence=JsonFiles.Read<GhidraEvidence>(path);
            if(!string.IsNullOrWhiteSpace(evidence.C))File.WriteAllText(Path.ChangeExtension(destination,".c"),evidence.C);
            found[candidate.Address]=destination;
        }
        var result=new GhidraBatchResult(found,pending.Length,cached,candidates.Length-found.Count);
        JsonFiles.Write(Path.Combine(analysis,"native-analysis-summary.json"),new {Backend="Ghidra 12.2",TotalCandidates=candidates.Length,
            AnalyzedThisRun=pending.Length,Cached=cached,Deferred=result.Deferred,BatchBudget=budget,TimedOut=timedOut,RemainingMethodsAreNotClaimedRecovered=true});
        progress(65,"Ghidra evidence correlated with managed methods");
        return result;
    }
    private static string FindUp(string relative)
    {
        for(var root=new DirectoryInfo(AppContext.BaseDirectory);root is not null;root=root.Parent)
        {string candidate=Path.Combine(root.FullName,relative);if(File.Exists(candidate)||Directory.Exists(candidate))return candidate;}
        return null;
    }
    private static string FindJava()
    {
        var candidates=new List<string>();
        foreach(string value in new[]{Environment.GetEnvironmentVariable("LIQUID_JAVA_HOME"),FindUp("engines/Java"),Environment.GetEnvironmentVariable("JAVA_HOME")})
            if(!string.IsNullOrWhiteSpace(value))candidates.Add(Path.Combine(value,"bin/java.exe"));
        foreach(string root in new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Java"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Eclipse Adoptium")})
            if(Directory.Exists(root))candidates.AddRange(Directory.EnumerateDirectories(root,"*25*").Select(d=>Path.Combine(d,"bin/java.exe")));
        string found=candidates.FirstOrDefault(File.Exists);
        return found??throw new IOException("Ghidra requires JDK 25. Build with setup-ghidra.ps1 to include its portable runtime.");
    }
    private static bool Floating(TypeReference type)=>type.FullName is "System.Single" or "System.Double";
    private static int Size(TypeReference type)=>type.FullName switch
    {"System.Void"=>0,"System.Boolean" or "System.Byte" or "System.SByte"=>1,"System.Char" or "System.Int16" or "System.UInt16"=>2,
        "System.Int32" or "System.UInt32" or "System.Single"=>4,_=>8};
}
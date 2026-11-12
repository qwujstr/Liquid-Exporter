using Mono.Cecil;
using Reaper.Core;

namespace Reaper.Worker;

internal static class NativeFusion
{
    public static void Apply(string binary, string fingerprint, List<AssemblyDefinition> originals, FusionResult fusion,
        string analysis, Action<int,string> progress)
    {
        var reports=fusion.Methods.Select((report,index)=>(report,index)).ToDictionary(p=>MethodFusion.Identity(p.report.Assembly,Convert.ToUInt32(p.report.Token[2..],16)));
        var candidates=originals.SelectMany(a=>MethodBodyCloner.AllTypes(a.MainModule.Types)).SelectMany(t=>t.Methods)
            .Select(m=>new {Method=m,Token=NativeRecovery.AttributeHex(m.CustomAttributes,"TokenAttribute","Token"),Address=NativeRecovery.AttributeHex(m.CustomAttributes,"AddressAttribute","VA")})
            .Where(p=>p.Token.HasValue&&p.Address is >0)
            .Select(p=>new GhidraCandidate(p.Method,p.Address.Value,MethodFusion.Identity(p.Method.Module.Assembly.Name.Name,(uint)p.Token.Value)))
            .Where(p=>reports.TryGetValue(p.Key,out var r)&&r.report.Status is "partially-reconstructed" or "unsupported")
            .OrderByDescending(p=>p.Method.Module.Assembly.Name.Name=="Assembly-CSharp")
            .ThenByDescending(p=>GhidraScalarRecovery.Eligible(p.Method))
            .ThenBy(p=>p.Address).ToArray();
        var result=GhidraBackend.Analyze(binary,fingerprint,candidates,analysis,progress);
        var targets=fusion.Assemblies.SelectMany(a=>MethodBodyCloner.AllTypes(a.MainModule.Types)).SelectMany(t=>t.Methods)
            .Select(m=>new {Method=m,Token=NativeRecovery.AttributeHex(m.CustomAttributes,"TokenAttribute","Token")})
            .Where(p=>p.Token.HasValue).ToDictionary(p=>MethodFusion.Identity(p.Method.Module.Assembly.Name.Name,(uint)p.Token.Value),p=>p.Method);
        var shared=candidates.GroupBy(c=>c.Address).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        foreach(var candidate in candidates)
        {
            if(!result.Evidence.TryGetValue(candidate.Address,out string file))continue;
            var current=reports[candidate.Key];var report=current.report;
            var evidence=JsonFiles.Read<GhidraEvidence>(file);
            string note=evidence.Completed?"Ghidra P-code/CFG evidence preserved; unsupported operations are not promoted.":"Ghidra analysis incomplete: "+evidence.Error;
            if(!shared.Contains(candidate.Address)&&targets.TryGetValue(candidate.Key,out var target)
                &&target.FullName==candidate.Method.FullName&&target.IsStatic==candidate.Method.IsStatic
                &&GhidraScalarRecovery.TryRecover(target,evidence,out string reason))
                report=report with {Status="fully-reconstructed",Backend="Ghidra-pcode",Confidence="instruction-mapped",Reason=reason};
            else report=report with {Diagnostics=(report.Diagnostics??[]).Append(note).ToArray()};
            fusion.Methods[current.index]=report with {NativeAnalysis=Path.GetRelativePath(Path.GetDirectoryName(analysis),file).Replace('\\','/')};
        }
    }
}
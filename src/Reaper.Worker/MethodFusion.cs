using Mono.Cecil;
using Mono.Cecil.Cil;
using Reaper.Core;

namespace Reaper.Worker;
public sealed record FusionResult(List<AssemblyDefinition> Assemblies,List<MethodRecovery> Methods);
public static class MethodFusion
{
    public static string Identity(string assembly,uint token)=>assembly+":"+token.ToString("X8");
    public static FusionResult Merge(List<AssemblyDefinition> metadataAssemblies,List<MethodRecovery> oldReports,CppBackendResult cpp,List<string> warnings,Action<int,string> progress=null)
    {
        var selected=metadataAssemblies.ToList();
        var byAssembly=selected.ToDictionary(a=>a.Name.Name,StringComparer.Ordinal);
        var originals=selected.Where(a=>a.Name.Name!="Il2CppDummyDll").SelectMany(a=>MethodBodyCloner.AllTypes(a.MainModule.Types)).SelectMany(t=>t.Methods)
            .Select(m=>new {Method=m,Token=NativeRecovery.AttributeHex(m.CustomAttributes,"TokenAttribute","Token")})
            .Where(p=>p.Token.HasValue).ToDictionary(p=>Identity(p.Method.Module.Assembly.Name.Name,(uint)p.Token.Value),p=>p.Method);
        var reports=oldReports.ToDictionary(r=>Identity(r.Assembly,Convert.ToUInt32(r.Token[2..],16)));
        var cppMethods=new Dictionary<string,MethodDefinition>();
        AssemblyDefinition[] cppInputs=[];
        if(cpp is not null)
        {
            var resolver=new Il2CppDumper.MyAssemblyResolver();resolver.AddSearchDirectory(cpp.AssembliesPath);
            var inputs=cppInputs=Directory.EnumerateFiles(cpp.AssembliesPath,"*.dll").Select(f=>AssemblyDefinition.ReadAssembly(f,new ReaderParameters{AssemblyResolver=resolver,InMemory=true})).ToArray();
            foreach(var assembly in inputs)resolver.Register(assembly);
            foreach(var method in inputs.SelectMany(a=>MethodBodyCloner.AllTypes(a.MainModule.Types)).SelectMany(t=>t.Methods))
            {
                ulong? token=NativeRecovery.AttributeHex(method.CustomAttributes,"TokenAttribute","Token");
                if(token.HasValue)cppMethods[Identity(method.Module.Assembly.Name.Name,(uint)token.Value)]=method;
            }
            foreach(var assembly in selected.Where(a=>a.Name.Name!="Il2CppDummyDll"))AddDiagnosticHelper(assembly.MainModule,(metadataAssemblies.FirstOrDefault(a=>a.Name.Name=="Il2CppDummyDll")??assembly).MainModule.TypeSystem);
        }
        var evidence=cpp?.Methods.ToDictionary(m=>Identity(m.Assembly,m.Token))??new();
        var states=new Dictionary<string,MethodRecovery>();int conflicts=0,copyFailures=0;
        int completed=0;
        foreach(var pair in originals)
        {
            if(++completed%10000==0)progress?.Invoke(45+Math.Min(6,completed/20000),"Correlating native bodies: "+completed+" methods");
            var original=pair.Value;var report=reports[pair.Key];var diagnostics=new List<string>();
            string status="unsupported",backend="Il2CppDumper",reason=report.Reason,confidence="unverified";
            bool matched=cppMethods.TryGetValue(pair.Key,out var candidate);
            if(matched&&!SameSignature(original,candidate))
            {matched=false;conflicts++;diagnostics.Add("Cpp2IL signature conflict: "+candidate.FullName);}
            if(evidence.TryGetValue(pair.Key,out var native))
            {
                ulong? va=NativeRecovery.AttributeHex(original.CustomAttributes,"AddressAttribute","VA");
                if(va.HasValue&&native.Address!=va.Value){matched=false;conflicts++;diagnostics.Add("Cpp2IL/Dumper native address conflict.");}
                diagnostics.AddRange(native.Warnings);
            }
            if(report.Status=="declaration")
            {status="signature-only";reason="Abstract, runtime, P/Invoke or interface declaration.";confidence="metadata";}
            else if(report.Status=="recovered")
            {status="fully-reconstructed";backend="checked-x64";confidence="instruction-mapped";}
            else if(matched&&candidate.HasBody)
            {
                backend="Cpp2IL";var ops=candidate.Body.Instructions;
                bool empty=ops.All(i=>i.OpCode==OpCodes.Nop||i.OpCode==OpCodes.Ret);
                bool failure=ops.Count==3&&ops[0].OpCode==OpCodes.Ldstr&&ops[1].Operand is MethodReference ex&&ex.DeclaringType.FullName=="System.Exception"&&ops[2].OpCode==OpCodes.Throw
                    ||ops.Count==2&&ops[0].OpCode==OpCodes.Ldnull&&ops[1].OpCode==OpCodes.Throw;
                if(failure)diagnostics.AddRange(ops.Select(i=>i.Operand).OfType<string>());
                if(!empty&&!failure)
                {
                    foreach(var entry in ops.Select((op,index)=>(op,index)))
                        if(entry.op.Operand is MethodReference call&&call.DeclaringType.FullName=="Cpp2ILInjected.Cpp2ILHelpers")
                            diagnostics.Add(entry.index>0&&ops[entry.index-1].OpCode==OpCodes.Ldstr?ops[entry.index-1].Operand.ToString():"Unresolved native operation");
                    try
                    {
                        MethodBodyCloner.Copy(candidate,original,byAssembly);
                        status="partially-reconstructed";
                        reason=diagnostics.Count>0?"Cpp2IL CIL contains unresolved operations; diagnostics are preserved.":"Cpp2IL reconstructed CIL; equivalence and dependency behaviour are not verified.";
                    }
                    catch(Exception error){copyFailures++;diagnostics.Add("Cpp2IL body import rejected: "+error.Message);}
                }
                else reason="Cpp2IL did not reconstruct an analyzable method body.";
            }
            else diagnostics.Add("No matching reconstructable Cpp2IL body.");
            states[pair.Key]=report with{Status=status,Backend=backend,Reason=reason,Diagnostics=diagnostics.Distinct().ToArray(),Confidence=confidence};
        }
        if(conflicts>0)warnings.Add(conflicts+" method signature/address conflicts were isolated; independent metadata and checked bodies were retained for those methods.");
        if(copyFailures>0)warnings.Add(copyFailures+" Cpp2IL bodies could not be imported safely; explicit unsupported bodies were retained.");
        bool changed;
        do
        {
            changed=false;
            foreach(var pair in states.ToArray())
            {
                if(pair.Value.Status!="fully-reconstructed")continue;
                foreach(var call in originals[pair.Key].Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>())
                {
                    MethodDefinition target;try{target=call.Resolve();}catch{continue;}if(target is null)continue;
                    ulong? token=NativeRecovery.AttributeHex(target.CustomAttributes,"TokenAttribute","Token");
                    if(token.HasValue&&states.TryGetValue(Identity(target.Module.Assembly.Name.Name,(uint)token.Value),out var dependency)
                        &&dependency.Status is "partially-reconstructed" or "unsupported")
                    {
                        states[pair.Key]=pair.Value with{Status="partially-reconstructed",Reason="Native call site reconstructed; a called implementation remains unresolved.",
                            Diagnostics=(pair.Value.Diagnostics??[]).Append("Unresolved callee: "+dependency.Method).ToArray()};changed=true;break;
                    }
                }
            }
        }while(changed);
        foreach(var input in cppInputs)input.Dispose();
        return new FusionResult(selected,states.Values.ToList());
    }
    private static bool SameSignature(MethodDefinition a,MethodDefinition b)=>a.Name==b.Name&&a.DeclaringType.FullName==b.DeclaringType.FullName
        &&a.IsStatic==b.IsStatic&&a.GenericParameters.Count==b.GenericParameters.Count&&SameType(a.ReturnType,b.ReturnType)
        &&a.Parameters.Count==b.Parameters.Count&&a.Parameters.Zip(b.Parameters).All(p=>SameType(p.First.ParameterType,p.Second.ParameterType));
    private static bool SameType(TypeReference a,TypeReference b)
    {
        if(a is ArrayType x&&b is ArrayType y)
            return x.Rank==y.Rank&&x.IsVector==y.IsVector&&SameType(x.ElementType,y.ElementType)
                &&x.Dimensions.Zip(y.Dimensions).All(d=>d.First.UpperBound==d.Second.UpperBound
                    &&(d.First.LowerBound==d.Second.LowerBound||(d.First.LowerBound??0)==0&&(d.Second.LowerBound??0)==0));
        if(a is ByReferenceType ar&&b is ByReferenceType br)return SameType(ar.ElementType,br.ElementType);
        if(a is PointerType ap&&b is PointerType bp)return SameType(ap.ElementType,bp.ElementType);
        if(a is GenericInstanceType ag&&b is GenericInstanceType bg)return ag.ElementType.FullName==bg.ElementType.FullName
            &&ag.GenericArguments.Count==bg.GenericArguments.Count&&ag.GenericArguments.Zip(bg.GenericArguments).All(t=>SameType(t.First,t.Second));
        return a.FullName==b.FullName;
    }
    private static void AddDiagnosticHelper(ModuleDefinition module,TypeSystem primitives)
    {
        if(module.Types.Any(t=>t.FullName=="Cpp2ILInjected.Cpp2ILHelpers"))throw new InvalidDataException("Injected helper name conflicts with an original type.");
        var helper=new TypeDefinition("Cpp2ILInjected","Cpp2ILHelpers",TypeAttributes.Public|TypeAttributes.Class|TypeAttributes.Abstract|TypeAttributes.Sealed,module.ImportReference(primitives.Object));module.Types.Add(helper);
        var note=new MethodDefinition("NoteDecompilerIssue",MethodAttributes.Public|MethodAttributes.Static,module.ImportReference(primitives.Void));helper.Methods.Add(note);
        note.Parameters.Add(new ParameterDefinition("operation",ParameterAttributes.None,module.ImportReference(primitives.String)));
        var exception=new TypeReference("System","NotSupportedException",module,module.ImportReference(primitives.Object).Scope);
        var ctor=new MethodReference(".ctor",module.ImportReference(primitives.Void),exception){HasThis=true};ctor.Parameters.Add(new ParameterDefinition(module.ImportReference(primitives.String)));
        note.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));note.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj,ctor));note.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
    }
}
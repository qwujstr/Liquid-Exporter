using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;
using Reaper.Core;

namespace Reaper.Worker;

public static class SourceDecompiler
{
    public static int Export(string file,string output,List<string> warnings,int timeoutSeconds=20,string diagnosticsDirectory=null,ISet<string> selectedTypes=null)
    {
        Directory.CreateDirectory(output);
        using var assembly=AssemblyDefinition.ReadAssembly(file,new ReaderParameters{InMemory=true});
        var settings=new DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp7_3){ThrowOnAssemblyResolveErrors=false};
        CSharpDecompiler Create(string path,DecompilerSettings configuration)
        {
            var value=new CSharpDecompiler(path,configuration);
            value.AstTransforms.Add(new ICSharpCode.Decompiler.CSharp.Transforms.EscapeInvalidIdentifiers());return value;
        }
        var primary=Create(file,settings);var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string temporary=Path.Combine(Path.GetDirectoryName(file),".liquid-source-"+Guid.NewGuid().ToString("N")+".dll");
        var fallbackTokens=new HashSet<uint>();var diagnostics=new List<object>();int files=0;
        try
        {
            foreach(var type in assembly.MainModule.Types.Where(t=>t.Name!="<Module>"&&(selectedTypes is null||selectedTypes.Contains(t.FullName))))
            {
                string source=null;Exception last=null;
                var reader=primary;
                for(int attempt=0;attempt<9;attempt++)
                {
                    try
                    {
                        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));reader.CancellationToken=timeout.Token;
                        source=reader.DecompileTypeAsString(new FullTypeName(type.FullName));break;
                    }
                    catch(Exception error)
                    {
                        last=error;
                        var match=Regex.Match(error.ToString(),@"@(?<token>06[0-9a-fA-F]{6})");
                        if(!match.Success||!uint.TryParse(match.Groups["token"].Value,System.Globalization.NumberStyles.HexNumber,null,out uint token)
                            ||!fallbackTokens.Add(token)||assembly.MainModule.LookupToken(new MetadataToken(token)) is not MethodDefinition method||!method.HasBody)break;
                        ulong? original=NativeRecovery.AttributeHex(method.CustomAttributes,"TokenAttribute","Token");
                        diagnostics.Add(new {Assembly=assembly.Name.Name,Type=method.DeclaringType.FullName,Method=method.FullName,
                            OriginalToken=original.HasValue?"0x"+original.Value.ToString("X8"):null,CilToken="0x"+token.ToString("X8"),
                            Error=error.Message,SourceBodyUnavailable=true,OriginalCilPreserved=true});
                        NativeRecovery.SetUnrecoveredBody(method,"C# decompiler could not represent this CIL method; original native-derived CIL is preserved in Assemblies. "+method.FullName);
                        assembly.Write(temporary);
                        reader=Create(temporary,settings);
                    }
                }
                if(source is null)
                {
                    warnings.Add(type.FullName+": C# bodies incomplete; declarations retained: "+last?.Message);
                    try
                    {
                        // Probe methods independently so several malformed bodies do not
                        // discard the remaining readable implementations of a large class.
                        var scan=Create(file,settings);var elapsed=System.Diagnostics.Stopwatch.StartNew();
                        foreach(var member in MethodBodyCloner.AllTypes(new[]{type}).SelectMany(t=>t.Methods).Where(m=>m.HasBody))
                        {
                            uint row=member.MetadataToken.ToUInt32();if(fallbackTokens.Contains(row))continue;
                            if(elapsed.Elapsed>TimeSpan.FromSeconds(60))break;
                            try
                            {
                                using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(2));scan.CancellationToken=limit.Token;
                                _=scan.DecompileAsString(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(unchecked((int)row)));
                            }
                            catch(Exception error)
                            {
                                var match=Regex.Match(error.ToString(),@"@(?<token>06[0-9a-fA-F]{6})");
                                uint failed=match.Success?Convert.ToUInt32(match.Groups["token"].Value,16):row;
                                if(!fallbackTokens.Add(failed)||assembly.MainModule.LookupToken(new MetadataToken(failed)) is not MethodDefinition rejected||!rejected.HasBody)continue;
                                ulong? original=NativeRecovery.AttributeHex(rejected.CustomAttributes,"TokenAttribute","Token");
                                diagnostics.Add(new {Assembly=assembly.Name.Name,Type=rejected.DeclaringType.FullName,Method=rejected.FullName,
                                    OriginalToken=original.HasValue?"0x"+original.Value.ToString("X8"):null,CilToken="0x"+failed.ToString("X8"),
                                    Error=error.Message,SourceBodyUnavailable=true,OriginalCilPreserved=true});
                                NativeRecovery.SetUnrecoveredBody(rejected,"C# method probe failed; original native-derived CIL is preserved in Assemblies. "+rejected.FullName);
                            }
                        }
                        assembly.Write(temporary);
                        try
                        {
                            var rescued=Create(temporary,settings);using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));rescued.CancellationToken=limit.Token;
                            source=rescued.DecompileTypeAsString(new FullTypeName(type.FullName));
                        }
                        catch { /* Preserve checked bodies and declarations as a last resort below. */ }
                        if(source is null)
                        {
                        var checkedBodies=new HashSet<string>();
                        string statusFile=diagnosticsDirectory is null?null:Path.Combine(Path.GetDirectoryName(diagnosticsDirectory),"script-report.json");
                        if(statusFile is not null&&File.Exists(statusFile))
                            checkedBodies=JsonFiles.Read<ScriptReport>(statusFile).Methods.Where(m=>m.Assembly==assembly.Name.Name&&m.Status=="fully-reconstructed").Select(m=>m.Token).ToHashSet();
                        foreach(var member in MethodBodyCloner.AllTypes(new[]{type}).SelectMany(t=>t.Methods).Where(m=>m.HasBody&&!m.IsAbstract&&!m.IsPInvokeImpl&&!m.IsRuntime))
                        {
                            ulong? token=NativeRecovery.AttributeHex(member.CustomAttributes,"TokenAttribute","Token");
                            string original=token.HasValue?"0x"+token.Value.ToString("X8"):null;
                            if(original is not null&&checkedBodies.Contains(original))continue;
                            NativeRecovery.SetUnrecoveredBody(member,"C# type fallback: this partial body could not be rendered; original CIL remains in Assemblies. "+member.FullName);
                            if(fallbackTokens.Add(member.MetadataToken.ToUInt32()))diagnostics.Add(new {Assembly=assembly.Name.Name,Type=member.DeclaringType.FullName,Method=member.FullName,
                                OriginalToken=original,CilToken="0x"+member.MetadataToken.ToUInt32().ToString("X8"),
                                Error="Type-level C# fallback after repeated invalid CIL methods; checked native bodies were retained.",SourceBodyUnavailable=true,OriginalCilPreserved=true});
                        }
                        assembly.Write(temporary);
                        var declarations=Create(temporary,settings);
                        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));declarations.CancellationToken=timeout.Token;
                        source="// Some partial bodies could not be rendered as C#. Checked bodies and original CIL are preserved.\n"+declarations.DecompileTypeAsString(new FullTypeName(type.FullName));
                        }
                    }
                    catch(Exception error){warnings.Add(type.FullName+": declaration export failed: "+error.Message);continue;}
                }
                string ns=string.IsNullOrWhiteSpace(type.Namespace)?"_Global":Program.SafeName(type.Namespace);
                string directory=Path.Combine(output,ns);Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,Program.SafeName(type.Name)+".cs");
                if(!paths.Add(path))path=Path.Combine(directory,Program.SafeName(type.Name)+"_"+type.MetadataToken.ToInt32().ToString("X8")+".cs");
                File.WriteAllText(path,"// Generated by Liquid. Read script-report.json and Analysis/csharp-fallback-*.json for recovery limits.\n"+source);files++;
            }
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        if(diagnostics.Count>0)
        {
            warnings.Add(assembly.Name.Name+": "+diagnostics.Count+" C# method bodies could not be rendered; remaining class bodies and original managed CIL were preserved.");
            if(diagnosticsDirectory is not null)
            {Directory.CreateDirectory(diagnosticsDirectory);string recordFile=Path.Combine(diagnosticsDirectory,"csharp-fallback-"+Program.SafeName(assembly.Name.Name)+".json");
                if(selectedTypes is not null&&File.Exists(recordFile))
                {
                    using var previous=System.Text.Json.JsonDocument.Parse(File.ReadAllText(recordFile));
                    diagnostics.AddRange(previous.RootElement.EnumerateArray().Where(e=>!selectedTypes.Any(t=>e.GetProperty("Type").GetString()==t||e.GetProperty("Type").GetString().StartsWith(t+"/",StringComparison.Ordinal))).Select(e=>(object)e.Clone()));
                }
                JsonFiles.Write(recordFile,diagnostics);}
        }
        return files;
    }
    public static void AttachDiagnostics(ScriptReport report,string analysis)
    {
        if(!Directory.Exists(analysis))return;
        var entries=new Dictionary<string,List<string>>();
        foreach(string file in Directory.EnumerateFiles(analysis,"csharp-fallback-*.json"))
        {
            using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            foreach(var item in document.RootElement.EnumerateArray())
            {
                string token=item.GetProperty("OriginalToken").GetString();if(token is null)continue;
                string key=item.GetProperty("Assembly").GetString()+":"+token;
                if(!entries.TryGetValue(key,out var messages))entries[key]=messages=[];
                messages.Add("C# body could not be rendered; original native-derived CIL remains in Assemblies. "+item.GetProperty("Error").GetString());
            }
        }
        report.Warnings.RemoveAll(w=>w.Contains(" C# method bodies could not be rendered;",StringComparison.Ordinal));
        foreach(var group in entries.GroupBy(e=>e.Key[..e.Key.LastIndexOf(':')]))
            report.Warnings.Add(group.Key+": "+group.Count()+" C# method bodies could not be rendered; remaining class bodies and original managed CIL were preserved.");
        for(int i=0;i<report.Methods.Count;i++)
        {
            var method=report.Methods[i];
            var current=(method.Diagnostics??[]).Where(d=>!d.StartsWith("C# body could not be rendered;",StringComparison.Ordinal));
            if(entries.TryGetValue(method.Assembly+":"+method.Token,out var messages))current=current.Concat(messages);
            report.Methods[i]=method with{Diagnostics=current.Distinct().ToArray()};
        }
    }
    public static void RefreshReport(string scripts)
    {
        var report=JsonFiles.Read<ScriptReport>(Path.Combine(scripts,"script-report.json"));
        AttachDiagnostics(report,Path.Combine(scripts,"Analysis"));
        JsonFiles.Write(Path.Combine(scripts,"script-report.json"),report with{SourceFiles=Directory.EnumerateFiles(Path.Combine(scripts,"Source"),"*.cs",SearchOption.AllDirectories).Count()});
    }
    public static void Refresh(string scripts,bool errorsOnly=false)
    {
        var report=JsonFiles.Read<ScriptReport>(Path.Combine(scripts,"script-report.json"));
        string analysis=Path.Combine(scripts,"Analysis");
        var failedTypes=report.Warnings.Where(w=>w.Contains(": Error decompiling @",StringComparison.Ordinal)||w.Contains(": C# bodies incomplete;",StringComparison.Ordinal)||w.Contains(": declaration export failed:",StringComparison.Ordinal))
            .Select(w=>w[..w.IndexOf(": ",StringComparison.Ordinal)]).ToHashSet();
        report.Warnings.RemoveAll(w=>w.Contains(": Error decompiling @",StringComparison.Ordinal)||w.Contains(": C# bodies incomplete;",StringComparison.Ordinal)||w.Contains(": declaration export failed:",StringComparison.Ordinal));
        var files=Directory.EnumerateFiles(Path.Combine(scripts,"Assemblies"),"*.dll")
            .Where(f=>Directory.Exists(Path.Combine(scripts,"Source",Program.SafeName(Path.GetFileNameWithoutExtension(f))))).ToArray();
        if(errorsOnly)files=files.Where(f=>report.Methods.Any(m=>m.Assembly==Path.GetFileNameWithoutExtension(f)&&failedTypes.Any(t=>m.Type==t||m.Type.StartsWith(t+"/",StringComparison.Ordinal)))).ToArray();
        int count=0;
        foreach(string file in files)
        {report.Warnings.RemoveAll(w=>w.StartsWith(Path.GetFileNameWithoutExtension(file)+": ",StringComparison.Ordinal)&&w.Contains("C# method bodies could not be rendered;",StringComparison.Ordinal));Console.WriteLine("Refreshing C# "+Path.GetFileName(file));count+=Export(file,Path.Combine(scripts,"Source",Program.SafeName(Path.GetFileNameWithoutExtension(file))),report.Warnings,diagnosticsDirectory:analysis,selectedTypes:errorsOnly?failedTypes:null);}
        count=Directory.EnumerateFiles(Path.Combine(scripts,"Source"),"*.cs",SearchOption.AllDirectories).Count();
        AttachDiagnostics(report,analysis);
        JsonFiles.Write(Path.Combine(scripts,"script-report.json"),report with{SourceFiles=count});
        Console.WriteLine("Source refreshed: "+count+" files.");
    }
}
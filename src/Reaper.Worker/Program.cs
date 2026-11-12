using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Il2CppDumper;
using Mono.Cecil;
using Reaper.Core;
using SectionCharacteristics = System.Reflection.PortableExecutable.SectionCharacteristics;

namespace Reaper.Worker;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length==2&&args[0]=="--refresh-source-report"){SourceDecompiler.RefreshReport(Path.GetFullPath(args[1]));return 0;}
            if(args.Length==2&&args[0]=="--retry-source-errors"){SourceDecompiler.Refresh(Path.GetFullPath(args[1]),true);return 0;}
            if(args.Length==2&&args[0]=="--refresh-source"){SourceDecompiler.Refresh(Path.GetFullPath(args[1]));return 0;}
            if (args.Length != 2 || args[0] != "--request")
            {
                Console.Error.WriteLine("Usage: Reaper.Worker --request request.json");
                return 2;
            }
            Run(JsonFiles.Read<WorkerRequest>(args[1]));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    public static void Run(WorkerRequest request)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        ReportProgress(3, "Reading native image and metadata");
        byte[] metadataBytes = File.ReadAllBytes(request.MetadataPath);
        byte[] binaryBytes = File.ReadAllBytes(request.BinaryPath);
        bool protectedProfile = ProtectedMetadataProfiles.Matches(metadataBytes, binaryBytes);
        if (protectedProfile)
        {
            Console.WriteLine("Decoding verified Pixel Worlds metadata profile...");
            metadataBytes = PixelWorldsMetadata.Normalize(metadataBytes, binaryBytes);
            string normalizedDirectory = Path.Combine(request.OutputDirectory, "Analysis");
            Directory.CreateDirectory(normalizedDirectory);
            File.WriteAllBytes(Path.Combine(normalizedDirectory, "normalized-metadata.dat"), metadataBytes);
            JsonFiles.Write(Path.Combine(normalizedDirectory, "metadata-profile.json"), new { Profile = PixelWorldsMetadata.Profile, NormalizedVersion = 31,
                OriginalFilesModified = false, VtableEntries = 205512, InterfaceOffsets = 18846,
                Omitted = new[] { "Custom attributes", "Parameter default values", "String literal tables", "Exported type forwarding" } });
        }
        using var metadata = new Metadata(new MemoryStream(metadataBytes));
        ReportProgress(12, $"Metadata ready: {metadata.typeDefs.Length} types, {metadata.methodDefs.Length} methods");
        using var binary = OpenBinary(binaryBytes);
        binary.SetProperties(request.ForceBinaryVersion ?? metadata.Version, metadata.metadataUsagesCount);
        Console.WriteLine($"Metadata v{metadata.Version}; binary {binary.GetType().Name}");

        if (!string.IsNullOrWhiteSpace(request.CodeRegistration))
            binary.Init(InputValidation.ParseAddress(request.CodeRegistration), InputValidation.ParseAddress(request.MetadataRegistration));
        else if (protectedProfile)
            binary.Init(PixelWorldsMetadata.CodeRegistration, PixelWorldsMetadata.MetadataRegistration);
        else
        {
            bool found = binary.PlusSearch(metadata.methodDefs.Count(m => m.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length);
            if (!found) found = binary.Search();
            if (!found) found = binary.SymbolSearch();
            if (!found) throw new InvalidDataException("Registration tables not found. Enter CodeRegistration and MetadataRegistration VA in advanced settings. Protected/memory-dumped files may require separate preprocessing.");
        }
        var executor = new Il2CppExecutor(metadata, binary);
        string analysisDirectory = Path.Combine(request.OutputDirectory, "Analysis");
        Directory.CreateDirectory(analysisDirectory);
        new Il2CppDecompiler(executor).Decompile(new Config { DumpProperty = true, DumpAttribute = true }, WithSeparator(analysisDirectory));
        if (request.GenerateAnalysisFiles && !protectedProfile) new StructGenerator(executor).WriteScript(WithSeparator(analysisDirectory));

        Console.WriteLine("Building managed type descriptions...");
        ReportProgress(20, "Building managed type descriptions");
        var generator = new DummyAssemblyGenerator(executor, true);
        string assemblyDirectory = Path.Combine(request.OutputDirectory, "Assemblies");
        string sourceDirectory = Path.Combine(request.OutputDirectory, "Source");
        Directory.CreateDirectory(assemblyDirectory);
        Directory.CreateDirectory(sourceDirectory);
        var methods = new List<MethodRecovery>();
        var warnings = new List<string>();
        if (protectedProfile) warnings.Add("Verified build-specific Pixel Worlds metadata adapter used. Original obfuscated identifiers are preserved. Custom attributes, parameter defaults, string literals and exported type forwarding are not reconstructed.");
        bool x64 = IsWindowsX64(binaryBytes);
        var nativeMethods = generator.Assemblies.SelectMany(a => AllTypes(a.MainModule.Types)).SelectMany(t => t.Methods)
            .Select(m => (Method: m, Address: NativeRecovery.AttributeHex(m.CustomAttributes, "AddressAttribute", "VA").GetValueOrDefault()))
            .Where(m => m.Address > 0).GroupBy(m => m.Address).ToArray();
        // Shared native addresses can represent incompatible signatures; do not guess a call target.
        var callTargets = nativeMethods.Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Method);
        var image = x64 ? new NativeImage(binaryBytes, nativeMethods.Select(g => g.Key), protectedProfile, callTargets) : null;
        if (request.RecoverSimpleMethods && !x64)
            warnings.Add("Body recovery supports simple Windows PE x64 methods only. Declarations and explicit stubs were exported for this architecture.");

        CppBackendResult cpp = null;
        if (request.ExperimentalRecovery)
        {
            try { cpp = CppCoreBackend.Run(binaryBytes, metadataBytes, request.BinaryPath, analysisDirectory, ReportProgress); }
            catch (Exception ex) { warnings.Add("Cpp2IL reconstruction failed; checked metadata/native fallback retained: " + ex.Message); }
        }
        int recovered = 0, missing = 0;
        int assemblyIndex = 0;
        foreach (var assembly in generator.Assemblies)
        {
            ReportProgress(30 + 15 * assemblyIndex++ / generator.Assemblies.Count, $"Reconstructing {assembly.Name.Name}");
            foreach (var type in AllTypes(assembly.MainModule.Types))
            foreach (var method in type.Methods)
            {
                // The support assembly contains real attribute constructors, not IL2CPP methods.
                if (assembly.Name.Name == "Il2CppDummyDll") continue;
                ulong? address = NativeRecovery.AttributeHex(method.CustomAttributes, "AddressAttribute", "VA");
                ulong? rva = NativeRecovery.AttributeHex(method.CustomAttributes, "AddressAttribute", "RVA");
                ulong? token = NativeRecovery.AttributeHex(method.CustomAttributes, "TokenAttribute", "Token");
                bool declaration = method.IsAbstract || method.IsPInvokeImpl || method.IsRuntime || type.IsInterface;
                bool success = false;
                string reason = declaration ? "Abstract/interface/runtime/PInvoke declaration; no body reconstructed."
                    : "Native body is unavailable or outside the supported subset.";
                if (!declaration && request.RecoverSimpleMethods && x64 && address is > 0)
                {
                    byte[] bytes = image.Method(address.Value);
                    if (bytes.Length > 0)
                    {
                        success = ScalarLifter.TryRecover(method, bytes, address.Value, image, out reason);
                        if (!success && NativeRecovery.TryRecover(method, bytes, out string leafReason)) { success = true; reason = leafReason; }
                    }
                }
                if (success) recovered++;
                else if (!declaration)
                {
                    missing++;
                    NativeRecovery.SetUnrecoveredBody(method, method.FullName);
                }
                methods.Add(new MethodRecovery(assembly.Name.Name, type.FullName, method.FullName,
                    $"0x{token.GetValueOrDefault():X8}", rva is null ? null : $"0x{rva:X}",
                    declaration ? "declaration" : success ? "recovered" : "unrecovered", reason));
            }
        }
        var fusion = MethodFusion.Merge(generator.Assemblies, methods, cpp, warnings, ReportProgress);
        methods = fusion.Methods;
        if (request.ExperimentalRecovery && x64)
        {
            try { NativeFusion.Apply(request.BinaryPath, CppCoreBackend.Fingerprint(binaryBytes, metadataBytes),
                generator.Assemblies, fusion, analysisDirectory, ReportProgress); }
            catch (Exception ex) { warnings.Add("Ghidra fallback incomplete: " + ex.Message); }
        }
        foreach (var assembly in fusion.Assemblies)
        {
            string fileName = SafeName(assembly.MainModule.Name);
            if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) fileName += ".dll";
            assembly.Write(Path.Combine(assemblyDirectory, fileName));
        }
        recovered = methods.Count(m => m.Status == "fully-reconstructed");
        missing = methods.Count(m => m.Status == "unsupported");
        int partial = methods.Count(m => m.Status == "partially-reconstructed");
        int declarations = methods.Count(m => m.Status == "signature-only");
        JsonFiles.Write(Path.Combine(request.OutputDirectory, "script-report.json"),
            new ScriptReport(binary.GetType().Name, metadata.Version, binary.Version, 0, 0, recovered, missing,
                methods, warnings, partial, declarations, cpp is null ? "Il2CppDumper + checked-x64" : "Cpp2IL.Core", cpp?.CacheHit ?? false));

        int count = 0, sources = 0;
        var sourceAssemblies = Directory.EnumerateFiles(assemblyDirectory, "*.dll").Where(file =>
            Path.GetFileNameWithoutExtension(file) != "Il2CppDummyDll" &&
            (request.IncludeEngineAssemblies || !IsFrameworkAssembly(Path.GetFileNameWithoutExtension(file)))).ToArray();
        foreach (string file in sourceAssemblies)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name == "Il2CppDummyDll" || (!request.IncludeEngineAssemblies && IsFrameworkAssembly(name))) continue;
            Console.WriteLine($"Decompiling {name}...");
            ReportProgress(66 + 32 * count / sourceAssemblies.Length, $"Decompiling {name}");
            try
            {
                sources += DecompileAssembly(file, Path.Combine(sourceDirectory, SafeName(name)), warnings, 20, analysisDirectory);
                count++;
            }
            catch (Exception ex) { warnings.Add($"{name}: decompilation incomplete: {ex.Message}"); }
        }
        if (sources == 0) throw new InvalidDataException("No C# source files produced. Try including framework assemblies or inspect the generated DLLs.");
        var report = new ScriptReport(binary.GetType().Name, metadata.Version, binary.Version, count, sources, recovered, missing,
            methods, warnings, partial, declarations, cpp is null ? "Il2CppDumper + checked-x64" : "Cpp2IL.Core", cpp?.CacheHit ?? false);
        SourceDecompiler.AttachDiagnostics(report, analysisDirectory);
        JsonFiles.Write(Path.Combine(request.OutputDirectory, "script-report.json"), report);
        File.WriteAllText(Path.Combine(request.OutputDirectory, "README.txt"), """
            Liquid: one primary Source tree and corresponding managed Assemblies.
            Cpp2IL.Core analyzes native methods using ISIL/SSA/CFG/CIL; Dumper supplies metadata/address mappings.
            Checked x64 and Ghidra P-code subsets replace bodies after signature/address validation.
            script-report.json: fully-reconstructed, partially-reconstructed, signature-only, unsupported per original token.
            Fully-reconstructed means instruction-mapped within a restricted subset, not whole-game equivalence.
            Partial methods may contain unresolved-operation markers, approximate types or noncompiling C#.
            Unsupported bodies throw explicitly. Analysis/Native preserves diagnostic C, P-code and CFG evidence.
            Native analysis is cached with a bounded per-run budget; deferred methods remain unresolved.
            """);
        Console.WriteLine($"Done: {sources} C# files; {recovered} instruction-mapped, {partial} partial, {declarations} declarations, {missing} unsupported.");
        ReportProgress(100, "C# export complete");
    }

    private static void ReportProgress(int percent, string message) => Console.WriteLine($"@liquid-progress:{percent}|{message}");

    public static int DecompileAssembly(string file,string output,List<string> warnings,int typeTimeoutSeconds=20)
        =>SourceDecompiler.Export(file,output,warnings,typeTimeoutSeconds);
    public static int DecompileAssembly(string file,string output,List<string> warnings,int typeTimeoutSeconds,string diagnosticsDirectory)
        =>SourceDecompiler.Export(file,output,warnings,typeTimeoutSeconds,diagnosticsDirectory);

    internal static bool IsFrameworkAssembly(string name) => name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp"
        || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("UnityEngine", StringComparison.Ordinal)
        || name.StartsWith("UnityEditor", StringComparison.Ordinal) || name.StartsWith("Mono.", StringComparison.Ordinal);
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
    private static string WithSeparator(string path) => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
    public static string SafeName(string name)
    {
        string value = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..") value = "Unnamed";
        string stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) value = "_" + value;
        return value.Length > 100 ? value[..80] + "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16] : value;
    }
    private static Il2Cpp OpenBinary(byte[] bytes)
    {
        if (bytes.Length < 8) throw new InvalidDataException("Binary is too small.");
        var stream = new MemoryStream(bytes);
        if (bytes[0] == 'M' && bytes[1] == 'Z') return new PE(stream);
        return BitConverter.ToUInt32(bytes) switch
        {
            0x464c457f => bytes[4] == 2 ? new Elf64(stream) : new Elf(stream),
            0xFEEDFACF => new Macho64(stream),
            0xFEEDFACE => new Macho(stream),
            0x304F534E => new NSO(stream).UnCompress(),
            0x6D736100 => new WebAssembly(stream).CreateMemory(),
            _ => throw new NotSupportedException("Unsupported binary. For a FAT Mach-O extract one architecture first.")
        };
    }
    private static bool IsWindowsX64(byte[] bytes)
    {
        if (bytes[0] != 'M' || bytes[1] != 'Z') return false;
        using var reader = new PEReader(new MemoryStream(bytes));
        return reader.PEHeaders.CoffHeader.Machine == Machine.Amd64;
    }
    private static byte[] ReadExecutableMethod(byte[] bytes, ulong? rva)
    {
        if (rva is null || rva > int.MaxValue) return [];
        using var reader = new PEReader(new MemoryStream(bytes));
        foreach (var section in reader.PEHeaders.SectionHeaders)
        {
            long relative = (long)rva.Value - section.VirtualAddress;
            if (relative < 0 || relative >= section.SizeOfRawData || !section.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute)) continue;
            long offset = section.PointerToRawData + relative;
            int length = (int)Math.Min(64, Math.Min(section.SizeOfRawData - relative, bytes.LongLength - offset));
            return length > 0 && offset >= 0 ? bytes.AsSpan((int)offset, length).ToArray() : [];
        }
        return [];
    }
}

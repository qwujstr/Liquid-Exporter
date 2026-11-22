using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

namespace Reaper.Core;

internal static class ModderSchemaRecovery
{
    public static void Extract(string worker, string binary, string metadata, string output)
    {
        var context = new WorkerContext(worker);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(worker));
            Type Type(string name) => assembly.GetType(name, throwOnError: true)!;
            var profile = Type("Reaper.Worker.PixelWorldsMetadata");
            byte[] native = File.ReadAllBytes(binary);
            byte[] bytes = (byte[])profile.GetMethod("Normalize")!.Invoke(null, [File.ReadAllBytes(metadata), native])!;
            using var metadataObject = (IDisposable)Activator.CreateInstance(Type("Il2CppDumper.Metadata"), new MemoryStream(bytes))!;
            using var image = (IDisposable)Type("Reaper.Worker.Program").GetMethod("OpenBinary", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [native])!;
            var imageType = image.GetType();
            var version = Member(metadataObject, "Version");
            var usages = Member(metadataObject, "metadataUsagesCount");
            imageType.GetMethod("SetProperties")!.Invoke(image, [version, usages]);
            imageType.GetMethod("Init", [typeof(ulong), typeof(ulong)])!.Invoke(image, [
                profile.GetField("CodeRegistration")!.GetRawConstantValue(),
                profile.GetField("MetadataRegistration")!.GetRawConstantValue()]);
            var executor = Activator.CreateInstance(Type("Il2CppDumper.Il2CppExecutor"), metadataObject, image)!;
            var generator = Activator.CreateInstance(Type("Il2CppDumper.DummyAssemblyGenerator"), executor, true)!;
            foreach (var definition in (IEnumerable)Member(generator, "Assemblies"))
            {
                var module = Member(definition, "MainModule");
                string name = Path.GetFileName((string)Member(module, "Name"));
                if (name.Length == 0) continue;
                definition.GetType().GetMethod("Write", [typeof(string)])!.Invoke(definition, [Path.Combine(output, name)]);
                if (definition is IDisposable disposable) disposable.Dispose();
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidDataException(ex.InnerException.Message, ex.InnerException);
        }
        finally { context.Unload(); }

        static object Member(object value, string name)
        {
            var type = value.GetType();
            return type.GetProperty(name)?.GetValue(value) ?? type.GetField(name)?.GetValue(value)
                ?? throw new MissingMemberException(type.FullName, name);
        }
    }

    private sealed class WorkerContext(string worker) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(Path.GetFullPath(worker));

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

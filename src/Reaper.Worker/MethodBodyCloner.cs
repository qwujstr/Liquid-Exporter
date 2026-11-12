using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Reaper.Worker;

/// <summary>Copies the validated lifter body into the Cpp2IL type model, rebinding every member.</summary>
public static class MethodBodyCloner
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AssemblyDefinition,Dictionary<string,TypeDefinition>> TypeIndexes=new();
    public static void Copy(MethodDefinition source, MethodDefinition target, IReadOnlyDictionary<string,AssemblyDefinition> assemblies)
    {
        var body=new MethodBody(target){InitLocals=source.Body.InitLocals,MaxStackSize=source.Body.MaxStackSize};
        var locals=new Dictionary<VariableDefinition,VariableDefinition>();
        var instructions=new Dictionary<Instruction,Instruction>();
        TypeReference Type(TypeReference type)
        {
            if(type is GenericParameter generic)
            {
                if(generic.Type==GenericParameterType.Method && generic.Position<target.GenericParameters.Count)return target.GenericParameters[generic.Position];
                if(generic.Owner is TypeReference owner)
                {
                    var mapped=Type(owner).Resolve();
                    if(mapped is not null&&generic.Position<mapped.GenericParameters.Count)return mapped.GenericParameters[generic.Position];
                }
                throw new InvalidOperationException("Cannot rebind generic parameter.");
            }
            if(type is GenericInstanceType instance){var result=new GenericInstanceType(Type(instance.ElementType));foreach(var arg in instance.GenericArguments)result.GenericArguments.Add(Type(arg));return result;}
            if(type is ArrayType array)return new ArrayType(Type(array.ElementType),array.Rank);
            if(type is ByReferenceType byref)return new ByReferenceType(Type(byref.ElementType));
            if(type is PointerType pointer)return new PointerType(Type(pointer.ElementType));
            if(type is PinnedType pinned)return new PinnedType(Type(pinned.ElementType));
            string assemblyName=type.Scope is AssemblyNameReference reference?reference.Name:type.Module?.Assembly?.Name.Name;
            if(assemblyName is not null&&assemblies.TryGetValue(assemblyName,out var ownerAssembly))
            {
                var definition=TypeIndexes.GetValue(ownerAssembly,a=>AllTypes(a.MainModule.Types).ToDictionary(t=>t.FullName)).GetValueOrDefault(type.FullName);
                if(definition is not null)return definition.Module==target.Module?definition:target.Module.ImportReference(definition);
            }
            return target.Module.ImportReference(type,target);
        }
        MethodReference Method(MethodReference method)
        {
            if(method is GenericInstanceMethod || method.HasGenericParameters || method.DeclaringType is GenericInstanceType)return target.Module.ImportReference(method,target);
            var owner=Type(method.DeclaringType);
            var definition=owner.Resolve();
            var matches=definition?.Methods.Where(m=>m.FullName==method.FullName).ToArray();
            if(matches is {Length:1})return target.Module.ImportReference(matches[0],target);
            var reference=new MethodReference(method.Name,Type(method.ReturnType),owner){HasThis=method.HasThis,ExplicitThis=method.ExplicitThis,CallingConvention=method.CallingConvention};
            foreach(var parameter in method.Parameters)reference.Parameters.Add(new ParameterDefinition(Type(parameter.ParameterType)));
            return reference;
        }
        FieldReference Field(FieldReference field)
        {
            var owner=Type(field.DeclaringType);if(owner is GenericInstanceType)return new FieldReference(field.Name,Type(field.FieldType),owner);var definition=owner.Resolve();
            var match=definition?.Fields.SingleOrDefault(f=>f.Name==field.Name&&f.FieldType.FullName==field.FieldType.FullName);
            return match is not null?target.Module.ImportReference(match):new FieldReference(field.Name,Type(field.FieldType),owner);
        }
        foreach(var local in source.Body.Variables){var copy=new VariableDefinition(Type(local.VariableType));locals[local]=copy;body.Variables.Add(copy);}
        foreach(var instruction in source.Body.Instructions){var copy=Instruction.Create(OpCodes.Nop);copy.OpCode=instruction.OpCode;instructions[instruction]=copy;body.Instructions.Add(copy);}
        foreach(var instruction in source.Body.Instructions)
        {
            object operand=instruction.Operand;
            instructions[instruction].Operand=operand switch
            {
                null=>null,Instruction branch=>instructions[branch],Instruction[] branches=>branches.Select(b=>instructions[b]).ToArray(),
                VariableDefinition local=>locals[local],ParameterDefinition parameter=>target.Parameters[parameter.Index],
                TypeReference type=>Type(type),MethodReference method=>Method(method),FieldReference field=>Field(field),
                CallSite=>throw new InvalidOperationException("Unverified unmanaged call site."),_=>operand
            };
        }
        foreach(var handler in source.Body.ExceptionHandlers)
            body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType){TryStart=instructions[handler.TryStart],TryEnd=handler.TryEnd is null?null:instructions[handler.TryEnd],
                HandlerStart=instructions[handler.HandlerStart],HandlerEnd=handler.HandlerEnd is null?null:instructions[handler.HandlerEnd],
                FilterStart=handler.FilterStart is null?null:instructions[handler.FilterStart],CatchType=handler.CatchType is null?null:Type(handler.CatchType)});
        target.Body=body;
    }
    internal static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {foreach(var type in types){yield return type;foreach(var nested in AllTypes(type.NestedTypes))yield return nested;}}
}
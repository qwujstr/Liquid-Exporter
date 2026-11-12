using Iced.Intel;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CilInstruction = Mono.Cecil.Cil.Instruction;
using Code = Iced.Intel.Code;

namespace Reaper.Worker;

/// <summary>
/// Intentionally bounded lifter for Windows x64 leaf methods. Every accepted instruction
/// must fit a complete known pattern. No control-flow or call guessing is performed.
/// </summary>
public static class NativeRecovery
{
    public static bool TryRecover(MethodDefinition method, byte[] code, out string reason)
    {
        reason = "Native pattern is outside the supported leaf-method subset.";
        if (method.IsConstructor || method.HasGenericParameters || method.DeclaringType.HasGenericParameters
            || method.DeclaringType.IsValueType || method.IsAbstract || method.IsPInvokeImpl)
            return false;

        var decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
        var instructions = new List<Iced.Intel.Instruction>();
        bool returned = false;
        for (int i = 0; i < 8 && decoder.IP < (ulong)code.Length; i++)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid || instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix
                || instruction.SegmentPrefix != Register.None) return false;
            if (instruction.Mnemonic == Mnemonic.Nop) continue;
            if (instruction.Code == Code.Retnq) { returned = true; break; }
            instructions.Add(instruction);
            if (instructions.Count > 1) return false;
        }
        if (!returned) return false;
        var body = new MethodBody(method);
        var il = body.GetILProcessor();
        string returnType = method.ReturnType.FullName;

        if (instructions.Count == 0 && returnType == "System.Void")
        {
            il.Emit(OpCodes.Ret);
            method.Body = body;
            reason = "x64 leaf: ret (empty void method).";
            return true;
        }
        if (instructions.Count != 1) return false;
        var op = instructions[0];

        if (IsInt32(returnType) && op.Code == Code.Mov_r32_imm32 && op.Op0Register == Register.EAX)
        {
            il.Emit(OpCodes.Ldc_I4, unchecked((int)op.Immediate32));
            il.Emit(OpCodes.Ret);
            reason = "x64 leaf: constant Int32/UInt32 return.";
        }
        else if (IsInt64(returnType) && op.Code == Code.Mov_r64_imm64 && op.Op0Register == Register.RAX)
        {
            il.Emit(OpCodes.Ldc_I8, unchecked((long)op.Immediate64));
            il.Emit(OpCodes.Ret);
            reason = "x64 leaf: constant Int64/UInt64 return.";
        }
        else if ((IsInt32(returnType) || IsInt64(returnType) || returnType == "System.Boolean")
            && op.Code == Code.Xor_rm32_r32 && op.Op0Register == Register.EAX && op.Op1Register == Register.EAX)
        {
            if (IsInt64(returnType)) il.Emit(OpCodes.Ldc_I8, 0L); else il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            reason = "x64 leaf: zero / false return.";
        }
        else if (!method.IsStatic && method.Parameters.Count == 0 && IsThisMemory(op)
            && ((IsInt32(returnType) && op.Code == Code.Mov_r32_rm32 && op.Op0Register == Register.EAX)
                || (IsInt64(returnType) && op.Code == Code.Mov_r64_rm64 && op.Op0Register == Register.RAX))
            && op.Op1Kind == OpKind.Memory
            && FindField(method, op.MemoryDisplacement64, returnType) is { } getterField)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, getterField);
            il.Emit(OpCodes.Ret);
            reason = "x64 leaf: integer instance field getter, offset and type matched.";
        }
        else if (!method.IsStatic && returnType == "System.Void" && method.Parameters.Count == 1 && IsThisMemory(op)
            && op.Op0Kind == OpKind.Memory
            && ((IsInt32(method.Parameters[0].ParameterType.FullName) && op.Code == Code.Mov_rm32_r32 && op.Op1Register == Register.EDX)
                || (IsInt64(method.Parameters[0].ParameterType.FullName) && op.Code == Code.Mov_rm64_r64 && op.Op1Register == Register.RDX))
            && FindField(method, op.MemoryDisplacement64, method.Parameters[0].ParameterType.FullName) is { } setterField)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, setterField);
            il.Emit(OpCodes.Ret);
            reason = "x64 leaf: integer instance field setter, Windows ABI/offset/type matched.";
        }
        else return false;
        method.Body = body;
        return true;
    }

    private static bool IsThisMemory(Iced.Intel.Instruction op) => op.MemoryBase == Register.RCX
        && op.MemoryIndex == Register.None && op.MemoryDisplacement64 >= 16 && op.MemoryDisplacement64 <= int.MaxValue;
    private static bool IsInt32(string type) => type is "System.Int32" or "System.UInt32";
    private static bool IsInt64(string type) => type is "System.Int64" or "System.UInt64";

    private static FieldDefinition FindField(MethodDefinition method, ulong offset, string type)
    {
        // Restrict to fields declared on this reference type; no guesses about base layouts.
        var matching = method.DeclaringType.Fields.Where(f => !f.IsStatic && f.FieldType.FullName == type
            && AttributeHex(f.CustomAttributes, "FieldOffsetAttribute", "Offset") == offset).ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    public static ulong? AttributeHex(IEnumerable<CustomAttribute> attributes, string name, string field)
    {
        var attr = attributes.FirstOrDefault(a => (a.AttributeType.Namespace is "Il2CppDummyDll" or "Cpp2ILInjected") && a.AttributeType.Name == name);
        string value = attr?.Fields.FirstOrDefault(f => f.Name == field).Argument.Value as string;
        if (value is null) return null;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var number) ? number : null;
    }

    public static void SetUnrecoveredBody(MethodDefinition method, string detail)
    {
        if (method.IsAbstract || method.IsPInvokeImpl || method.IsRuntime || method.DeclaringType.IsInterface) return;
        // Refer to the game's corlib instead of importing .NET 10 System.Private.CoreLib.
        var exceptionType = new TypeReference("System", "NotSupportedException", method.Module, method.Module.TypeSystem.CoreLibrary);
        TypeReference voidType = method.Module.GetType("System.Void") ?? method.Module.TypeSystem.Void;
        TypeReference stringType = method.Module.GetType("System.String") ?? method.Module.TypeSystem.String;
        var constructor = new MethodReference(".ctor", voidType, exceptionType) { HasThis = true };
        constructor.Parameters.Add(new ParameterDefinition(stringType));
        method.Body = new MethodBody(method);
        method.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ldstr, "IL2CPP body not recovered: " + detail));
        method.Body.Instructions.Add(CilInstruction.Create(OpCodes.Newobj, constructor));
        method.Body.Instructions.Add(CilInstruction.Create(OpCodes.Throw));
    }
}

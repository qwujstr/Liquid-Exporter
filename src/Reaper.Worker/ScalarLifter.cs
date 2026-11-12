using Iced.Intel;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NI = Iced.Intel.Instruction;
using CI = Mono.Cecil.Cil.Instruction;
using FlowControl = Iced.Intel.FlowControl;

namespace Reaper.Worker;

/// <summary>
/// Typed x64 -> CIL translation. Registers become typed locals; instruction addresses
/// become labels. A fixed-point pass intersects definitely assigned registers at CFG
/// joins. Unsupported operations reject the whole method, never a partial body.
/// </summary>
public sealed class ScalarLifter
{
    private sealed record Value(TypeReference Type, VariableDefinition Local);
    private sealed record StackValue(Value Value, int Width);
    private sealed class State
    {
        public Dictionary<Register, Value> Registers = new();
        public int Stack;
        public Dictionary<int, StackValue> StackValues = new();
        public (Value Left, Value Right, bool Test)? Flags;
        public State Copy() => new() { Registers = new(Registers), Stack = Stack, StackValues = new(StackValues), Flags = Flags };
    }
    private readonly MethodDefinition method;
    private readonly NativeImage image;
    private readonly MethodBody body;
    private readonly Dictionary<ulong, NI> native = new();
    private readonly Dictionary<ulong, State> incoming = new();
    private readonly Dictionary<ulong, List<CI>> blocks = new();
    private readonly Dictionary<ulong, CI> labels = new();
    private readonly Dictionary<(Register, string), Value> slots = new();
    private readonly Dictionary<(ulong, int, string), Value> temporaries = new();
    private int temporaryIndex;
    private readonly Queue<ulong> pending = new();
    private List<CI> output;
    private State state;
    private NI current;
    private readonly ulong start;
    private int operations, branches, stores;
    private ScalarLifter(MethodDefinition method, NativeImage image, ulong start)
    { this.method = method; this.image = image; this.start = start; body = new MethodBody(method) { InitLocals = true, MaxStackSize = 8 }; }

    public static bool TryRecover(MethodDefinition method, byte[] bytes, ulong address, NativeImage image, out string reason)
    {
        reason = "No typed native translation available.";
        if (method.IsConstructor || method.HasGenericParameters || method.DeclaringType.HasGenericParameters || method.DeclaringType.IsValueType
            || method.IsAbstract || method.IsPInvokeImpl || bytes.Length == 0 || bytes.Length > 16384) return false;
        var lifter = new ScalarLifter(method, image, address);
        try { reason = lifter.Translate(bytes); return true; }
        catch (Unsupported ex) { reason = $"x64 at 0x{lifter.current.IP:X}: {ex.Message}"; return false; }
        catch (AssemblyResolutionException) { reason = "A field type could not be resolved for native translation."; return false; }
    }
    private sealed class Unsupported(string reason) : Exception(reason);
    private static void Require(bool condition, string reason) { if (!condition) throw new Unsupported(reason); }
    private string Translate(byte[] bytes)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = start;
        // Decode on demand, so padding/data after a return never becomes method logic.
        NI Decode(ulong address)
        {
            Require(address >= start && address < start + (ulong)bytes.Length, "Branch leaves the bounded method.");
            if (native.TryGetValue(address, out var old)) return old;
            decoder.IP = address;
            decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes, (int)(address - start), bytes.Length - (int)(address - start)));
            decoder.IP = address;
            var instruction = decoder.Decode();
            Require(!instruction.IsInvalid && !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix
                && instruction.SegmentPrefix == Register.None, "Invalid, prefixed, or unsupported instruction.");
            foreach (var other in native.Values)
                Require(address >= other.NextIP || instruction.NextIP <= other.IP, "Overlapping instruction/branch boundary.");
            native[address] = instruction; Label(address); return instruction;
        }
        output = new(); state = new State();
        int position = 0;
        if (!method.IsStatic)
        {
            Emit(OpCodes.Ldarg_0); Set(Register.RCX, method.DeclaringType); position++;
        }
        Register[] integers = [Register.RCX, Register.RDX, Register.R8, Register.R9];
        Register[] floats = [Register.XMM0, Register.XMM1, Register.XMM2, Register.XMM3];
        foreach (var parameter in method.Parameters)
        {
            if (Supported(parameter.ParameterType))
            {
                Emit(OpCodes.Ldarg, parameter);
                if (position < 4) Set(IsFloat(parameter.ParameterType) ? floats[position] : integers[position], parameter.ParameterType);
                else state.StackValues[40 + 8 * (position - 4)] = new StackValue(Temporary(parameter.ParameterType), Size(parameter.ParameterType));
            }
            position++;
        }
        var entry = output;
        incoming[start] = state.Copy(); pending.Enqueue(start);
        int iterations = 0;
        while (pending.TryDequeue(out ulong address))
        {
            Require(++iterations < 8192 && native.Count < 1024, "Method exceeds the bounded CFG analysis budget.");
            current = Decode(address); state = incoming[address].Copy(); output = new(); temporaryIndex = 0;
            bool falls = TranslateInstruction();
            blocks[address] = output;
            if (falls) Edge(current.NextIP);
        }
        Require(blocks.Values.Any(b => b.Any(i => i.OpCode == OpCodes.Ret)), "No reachable managed return.");
        foreach (var instruction in entry) body.Instructions.Add(instruction);
        foreach (ulong address in blocks.Keys.Order())
        {
            body.Instructions.Add(labels[address]);
            foreach (var instruction in blocks[address]) body.Instructions.Add(instruction);
        }
        LiftedBodyCleanup.Run(body, native.Values.Any(i => i.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch && i.NearBranchTarget < i.IP));
        method.Body = body;
        operations = native.Values.Count(i => i.Op0Register != Register.RSP && i.Mnemonic is Mnemonic.Add or Mnemonic.Sub or Mnemonic.Imul or Mnemonic.Addss or Mnemonic.Mulss or Mnemonic.Subss or Mnemonic.Divss);
        branches = native.Values.Count(i => i.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch);
        return $"Typed x64 CFG: {native.Count} instructions, {branches} branches, {operations} arithmetic operations; all reachable operations translated.";
    }

    private void Edge(ulong target)
    {
        Require(target >= start && target < start + 16384, "External branch is not a managed call.");
        if (!labels.ContainsKey(target)) labels[target] = CI.Create(OpCodes.Nop);
        if (!incoming.TryGetValue(target, out var previous))
        { incoming[target] = state.Copy(); pending.Enqueue(target); return; }
        Require(previous.Stack == state.Stack, "Unbalanced native stack at CFG join.");
        bool changed = false;
        foreach (var reg in previous.Registers.Keys.ToArray())
            if (!state.Registers.TryGetValue(reg, out var other) || other != previous.Registers[reg]) { previous.Registers.Remove(reg); changed = true; }
        foreach (int offset in previous.StackValues.Keys.ToArray())
            if (!state.StackValues.TryGetValue(offset, out var other) || other != previous.StackValues[offset]) { previous.StackValues.Remove(offset); changed = true; }
        if (previous.Flags != state.Flags && previous.Flags is not null) { previous.Flags = null; changed = true; }
        if (changed) pending.Enqueue(target);
    }

    private bool TranslateInstruction()
    {
        var i = current;
        if (i.Mnemonic == Mnemonic.Nop) return true;
        if (i.Mnemonic == Mnemonic.Push && i.Op0Kind == OpKind.Register)
        {
            state.Stack += 8;
            state.Registers.TryGetValue(i.Op0Register.GetFullRegister(), out var saved);
            state.StackValues[-state.Stack] = new StackValue(saved is null ? null : Snapshot(saved), 8);
            return true;
        }
        if (i.Mnemonic == Mnemonic.Pop && i.Op0Kind == OpKind.Register)
        {
            Require(state.StackValues.TryGetValue(-state.Stack, out var saved) && saved.Width == 8, "Unknown stack POP.");
            if (saved.Value is null) state.Registers.Remove(i.Op0Register.GetFullRegister());
            else { Load(saved.Value); Set(i.Op0Register, saved.Value.Type); }
            state.StackValues.Remove(-state.Stack); state.Stack -= 8;
            Require(state.Stack >= 0, "Stack POP underflow."); return true;
        }
        if (i.Code == Iced.Intel.Code.Retnq)
        {
            Require(state.Stack == 0, "Return has an unbalanced native stack.");
            if (method.ReturnType.FullName != "System.Void")
            {
                var result = Get(IsFloat(method.ReturnType) ? Register.XMM0 : Register.RAX);
                Require(Compatible(result.Type, method.ReturnType), "Native return type does not match metadata.");
                Load(result);
                if (Kind(method.ReturnType) == "i4" && Kind(result.Type) == "i8") Emit(OpCodes.Conv_I4);
            }
            Emit(OpCodes.Ret); return false;
        }
        if (i.Mnemonic is Mnemonic.Add or Mnemonic.Sub && i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP)
        {
            Require(i.Op1Kind != OpKind.Register && i.Op1Kind != OpKind.Memory, "Dynamic native stack adjustment.");
            int delta = checked((int)Immediate(i, 1));
            state.Stack += i.Mnemonic == Mnemonic.Sub ? delta : -delta;
            Require(state.Stack is >= 0 and <= 4096, "Invalid native stack frame."); state.Flags = null; return true;
        }
        if (i.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movss or Mnemonic.Movsd or Mnemonic.Movaps)
        {
            if (i.Op0Kind == OpKind.Register)
            {
                if (i.Op1Kind == OpKind.Memory && i.MemoryBase == Register.RSP && i.MemoryIndex == Register.None
                    && state.StackValues.TryGetValue(StackOffset(), out var saved) && saved.Value is null)
                { state.Registers.Remove(i.Op0Register.GetFullRegister()); return true; }
                TypeReference hint = i.Mnemonic == Mnemonic.Movss ? SystemType("Single") : i.Mnemonic == Mnemonic.Movsd ? SystemType("Double") : null;
                var value = Operand(1, hint);
                int size = i.Op0Register.GetSize();
                Require(size >= 4 || i.Op0Register == Register.AL && Kind(value.Type) == "i4", "Partial-register write is unsupported.");
                Require(i.Mnemonic != Mnemonic.Movzx || Kind(value.Type) == "i4", "MOVZX requires an integer field.");
                if (size == 4) Require(Kind(value.Type) == "i4", "Integer/float bit reinterpretation is unsupported.");
                Load(value);
                TypeReference storedType = value.Type;
                if (i.Mnemonic == Mnemonic.Movzx && value.Type.FullName is "System.SByte" or "System.Int16")
                { Emit(value.Type.FullName == "System.SByte" ? OpCodes.Conv_U1 : OpCodes.Conv_U2); storedType = SystemType("Int32"); }
                Set(i.Op0Register, storedType); return true;
            }
            if (i.Op0Kind == OpKind.Memory)
            {
                if (i.MemoryBase == Register.RSP && i.MemoryIndex == Register.None)
                {
                    int offset = StackOffset(), width = i.MemorySize.GetSize();
                    Require(offset >= -state.Stack && offset + width <= 512, "Stack store outside supported frame.");
                    Value stackValue = null;
                    if (i.Op1Kind != OpKind.Register || state.Registers.ContainsKey(i.Op1Register.GetFullRegister())) stackValue = Operand(1);
                    if (stackValue is not null) Require(width == Size(stackValue.Type) || width == 16 && IsFloat(stackValue.Type) && i.Mnemonic == Mnemonic.Movaps
                        || width == 8 && Kind(stackValue.Type) == "i4" && i.Op1Register.GetSize() == 8, "Stack store width mismatch.");
                    foreach (var cell in state.StackValues.ToArray())
                        if (cell.Key < offset + width && offset < cell.Key + cell.Value.Width) state.StackValues.Remove(cell.Key);
                    state.StackValues[offset] = new StackValue(stackValue is null ? null : Snapshot(stackValue), width); return true;
                }
                var (owner, fields) = MemoryFields();
                var field = fields[^1];
                Require(Kind(field.FieldType) != "ref", "Reference writes need a verified GC write barrier.");
                var value = Operand(1, field.FieldType);
                Require(Compatible(value.Type, field.FieldType) && i.MemorySize.GetSize() == Size(field.FieldType), "Field write width/type mismatch.");
                LoadOwner(owner, fields); Load(value); Emit(OpCodes.Stfld, method.Module.ImportReference(field)); stores++; return true;
            }
        }
        if (i.Mnemonic is Mnemonic.Xor or Mnemonic.Xorps or Mnemonic.Xorpd
            && i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register && i.Op0Register == i.Op1Register)
        {
            TypeReference t = i.Mnemonic == Mnemonic.Xor ? (i.Op0Register.GetSize() == 8 ? SystemType("Int64") : SystemType("Int32"))
                : i.Mnemonic == Mnemonic.Xorps ? SystemType("Single") : SystemType("Double");
            Constant(0, t); Set(i.Op0Register, t); state.Flags = null; return true;
        }
        if (i.Mnemonic is Mnemonic.Cmp or Mnemonic.Test)
        {
            var left = Operand(0); var right = Operand(1, left.Type);
            Require(Compatible(left.Type, right.Type), "Comparison operand types differ.");
            if (i.Mnemonic == Mnemonic.Test) Require(i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register && i.Op0Register == i.Op1Register, "Only self TEST is supported.");
            state.Flags = (Snapshot(left), Snapshot(right), i.Mnemonic == Mnemonic.Test); return true;
        }
        if (i.FlowControl == FlowControl.ConditionalBranch)
        {
            EmitCondition(i.Mnemonic); Emit(OpCodes.Brtrue, Label(i.NearBranchTarget)); Edge(i.NearBranchTarget); return true;
        }
        if (i.FlowControl == FlowControl.UnconditionalBranch)
        {
            Require(i.IsJmpShortOrNear, "Indirect/tail call is unsupported.");
            Emit(OpCodes.Br, Label(i.NearBranchTarget)); Edge(i.NearBranchTarget); return false;
        }
        if (i.Mnemonic is Mnemonic.Sete or Mnemonic.Setne or Mnemonic.Setl or Mnemonic.Setle or Mnemonic.Setg or Mnemonic.Setge or Mnemonic.Seta or Mnemonic.Setae or Mnemonic.Setb or Mnemonic.Setbe)
        {
            Require(i.Op0Kind == OpKind.Register && i.Op0Register.GetSize() == 1 && i.Op0Register is not (Register.AH or Register.BH or Register.CH or Register.DH), "Unsupported SETcc target.");
            // The upper bytes are unchanged by SETcc: only accept a previously zeroed full register.
            Require(i.Op0Register == Register.AL && method.ReturnType.FullName == "System.Boolean",
                "SETcc would preserve unknown upper register bits.");
            EmitCondition(i.Mnemonic); Set(i.Op0Register, SystemType("Boolean")); return true;
        }
        if (i.Mnemonic is Mnemonic.Add or Mnemonic.Sub or Mnemonic.Imul or Mnemonic.And or Mnemonic.Or or Mnemonic.Xor
            or Mnemonic.Addss or Mnemonic.Subss or Mnemonic.Mulss or Mnemonic.Divss or Mnemonic.Addsd or Mnemonic.Subsd or Mnemonic.Mulsd or Mnemonic.Divsd)
        {
            Require(i.Op0Kind == OpKind.Register && i.OpCount == 2, "Only register arithmetic with two operands is supported.");
            var left = Get(i.Op0Register); var right = Operand(1, left.Type);
            Require(Kind(left.Type) == Kind(right.Type) && Kind(left.Type) != "ref", "Arithmetic type mismatch.");
            Require(IsFloat(left.Type) || left.Type.FullName is "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64", "Arithmetic on a narrow scalar requires explicit extension.");
            bool floating = i.Mnemonic.ToString().EndsWith("ss") || i.Mnemonic.ToString().EndsWith("sd");
            Require(floating == IsFloat(left.Type), "Arithmetic instruction/register kind mismatch.");
            Require(floating || i.Op0Register.GetSize() >= 4, "Partial-register arithmetic is unsupported.");
            Load(left); Load(right);
            Emit(i.Mnemonic switch
            {
                Mnemonic.Add or Mnemonic.Addss or Mnemonic.Addsd => OpCodes.Add,
                Mnemonic.Sub or Mnemonic.Subss or Mnemonic.Subsd => OpCodes.Sub,
                Mnemonic.Imul or Mnemonic.Mulss or Mnemonic.Mulsd => OpCodes.Mul,
                Mnemonic.Divss or Mnemonic.Divsd => OpCodes.Div,
                Mnemonic.And => OpCodes.And, Mnemonic.Or => OpCodes.Or, _ => OpCodes.Xor
            });
            Set(i.Op0Register, left.Type); state.Flags = null; return true;
        }
        if (i.Mnemonic is Mnemonic.Neg or Mnemonic.Not)
        {
            Require(i.Op0Kind == OpKind.Register && i.Op0Register.GetSize() >= 4, "Unsupported unary operand.");
            var value = Get(i.Op0Register); Require(Kind(value.Type) is "i4" or "i8", "Unary operation requires integer.");
            Load(value); Emit(i.Mnemonic == Mnemonic.Neg ? OpCodes.Neg : OpCodes.Not); Set(i.Op0Register, value.Type); state.Flags = null; return true;
        }
        if (i.IsCallNear && image?.NullReferenceHelper == i.NearBranchTarget)
        {
            // Explicit null throw, not a silently discarded native call.
            var exception = new TypeReference("System", "NullReferenceException", method.Module, method.Module.TypeSystem.CoreLibrary);
            var constructor = new MethodReference(".ctor", method.Module.GetType("System.Void") ?? method.Module.TypeSystem.Void, exception) { HasThis = true };
            Emit(OpCodes.Newobj, constructor); Emit(OpCodes.Throw); return false;
        }
        if (i.IsCallNear && image is not null && image.ManagedMethods.TryGetValue(i.NearBranchTarget, out var target))
        {
            Require(!target.IsConstructor && !target.HasGenericParameters && !target.DeclaringType.HasGenericParameters && !target.DeclaringType.IsValueType
                && (target.ReturnType.FullName == "System.Void" || Supported(target.ReturnType)), "Managed call has an unsupported ABI/signature.");
            Register[] integerArgs = [Register.RCX, Register.RDX, Register.R8, Register.R9];
            Register[] floatArgs = [Register.XMM0, Register.XMM1, Register.XMM2, Register.XMM3];
            var arguments = new List<TypeReference>();
            if (!target.IsStatic) arguments.Add(target.DeclaringType);
            arguments.AddRange(target.Parameters.Select(p => p.ParameterType));
            for (int position = 0; position < arguments.Count; position++)
            {
                TypeReference expected = arguments[position]; Require(Supported(expected), "Managed call argument has an unsupported ABI.");
                Value value;
                if (position < 4) value = Get(IsFloat(expected) ? floatArgs[position] : integerArgs[position]);
                else
                {
                    Require(state.StackValues.TryGetValue(32 + 8 * (position - 4) - state.Stack, out var cell) && cell.Value is not null, "Managed call stack argument is unknown.");
                    value = cell.Value;
                }
                Require(Compatible(value.Type, expected), "Managed call argument type does not match metadata."); Load(value);
            }
            Emit(OpCodes.Call, method.Module.ImportReference(target));
            foreach (Register register in new[] { Register.RAX, Register.RCX, Register.RDX, Register.R8, Register.R9, Register.R10, Register.R11,
                Register.XMM0, Register.XMM1, Register.XMM2, Register.XMM3, Register.XMM4, Register.XMM5 }) state.Registers.Remove(register);
            // Calls may overwrite the shadow space and arguments passed on the stack.
            foreach (int offset in state.StackValues.Keys.Where(o => o >= -state.Stack && o < -state.Stack + 32 + Math.Max(0, arguments.Count - 4) * 8).ToArray()) state.StackValues.Remove(offset);
            state.Flags = null;
            if (target.ReturnType.FullName != "System.Void") Set(IsFloat(target.ReturnType) ? Register.XMM0 : Register.RAX, target.ReturnType);
            return true;
        }
        throw new Unsupported("Unsupported operation: " + i);
    }

    private Value Operand(int index, TypeReference hint = null)
    {
        OpKind op = current.GetOpKind(index);
        if (op == OpKind.Register)
        {
            Register reg = current.GetOpRegister(index);
            var value = Get(reg);
            Require(reg.GetSize() >= 4 || Size(value.Type) == reg.GetSize(), "Unsupported partial-register read.");
            return value;
        }
        if (op == OpKind.Memory)
        {
            if (current.MemoryBase == Register.RSP && current.MemoryIndex == Register.None)
            {
                Require(state.StackValues.TryGetValue(StackOffset(), out var cell) && cell.Value is not null, "Read of uninitialized/native stack slot.");
                Require(cell.Width == current.MemorySize.GetSize() || cell.Width >= Size(cell.Value.Type) && current.MemorySize.GetSize() == Size(cell.Value.Type), "Stack read width mismatch.");
                if (hint is not null) Require(Compatible(cell.Value.Type, hint), "Stack read type mismatch.");
                return cell.Value;
            }
            if (current.IsIPRelativeMemoryOperand)
            {
                int size = current.MemorySize.GetSize();
                Require(image is not null && hint is not null && IsFloat(hint) && Size(hint) == size
                    && image.ReadConstant(current.IPRelativeMemoryAddress, size, out _), "Mutable/global/native-pointer memory read.");
                image.ReadConstant(current.IPRelativeMemoryAddress, size, out ulong bits);
                Constant(bits, hint); return Temporary(hint);
            }
            var (owner, fields) = MemoryFields(); var field = fields[^1];
            Require(current.MemorySize.GetSize() == Size(field.FieldType), "Field read width/type mismatch.");
            if (hint is not null) Require(Compatible(field.FieldType, hint), "Field read type mismatch.");
            LoadOwner(owner, fields); Emit(OpCodes.Ldfld, method.Module.ImportReference(field)); return Temporary(field.FieldType);
        }
        Require(op is OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate32to64,
            "Unsupported operand kind: " + op);
        TypeReference type = hint ?? (current.GetOpRegister(0).GetSize() == 8 ? SystemType("Int64") : SystemType("Int32"));
        Require(Kind(type) is "i4" or "i8", "Immediate operand cannot become a managed reference or float.");
        Constant(unchecked((ulong)Immediate(current, index)), type); return Temporary(type);
    }

    private (Value Owner, List<FieldDefinition> Fields) MemoryFields()
    {
        Require(current.MemoryIndex == Register.None && !current.IsIPRelativeMemoryOperand, "Indexed/native global memory is unsupported.");
        var owner = Get(current.MemoryBase);
        Require(Kind(owner.Type) == "ref", "Field access requires a typed object register.");
        long offset = checked((long)current.MemoryDisplacement64);
        Require(offset >= 16 && offset <= int.MaxValue, "Invalid object field offset.");
        var matches = new List<List<FieldDefinition>>();
        void Find(TypeDefinition type, long displacement, List<FieldDefinition> path, int depth)
        {
            if (type is null || depth > 4) return;
            foreach (var field in type.Fields.Where(f => !f.IsStatic))
            {
                ulong? position = NativeRecovery.AttributeHex(field.CustomAttributes, "FieldOffsetAttribute", "Offset");
                if (position is null || position > (ulong)displacement) continue;
                long remaining = displacement - (long)position;
                var next = new List<FieldDefinition>(path) { field };
                if (remaining == 0 && Supported(field.FieldType)) matches.Add(next);
                else if (field.FieldType.IsValueType && !Supported(field.FieldType)) Find(field.FieldType.Resolve(), remaining, next, depth + 1);
            }
            if (type.BaseType is not null && type.BaseType.FullName != "System.Object") Find(type.BaseType.Resolve(), displacement, path, depth + 1);
        }
        Find(owner.Type.Resolve(), offset, new(), 0);
        Require(matches.Count == 1, "Field offset is unresolved or ambiguous: " + offset.ToString("X"));
        return (owner, matches[0]);
    }
    private void LoadOwner(Value owner, List<FieldDefinition> fields)
    {
        Load(owner);
        foreach (var field in fields.Take(fields.Count - 1)) Emit(OpCodes.Ldflda, method.Module.ImportReference(field));
    }
    private void EmitCondition(Mnemonic mnemonic)
    {
        Require(state.Flags.HasValue, "Branch uses unknown/clobbered native flags.");
        var flags = state.Flags.Value;
        Load(flags.Left);
        if (flags.Test)
        {
            if (Kind(flags.Left.Type) == "ref") Emit(OpCodes.Ldnull);
            else Constant(0, flags.Left.Type);
        }
        else Load(flags.Right);
        bool equal = mnemonic is Mnemonic.Je or Mnemonic.Jne or Mnemonic.Sete or Mnemonic.Setne;
        Require(equal || !flags.Test && Kind(flags.Left.Type) is "i4" or "i8", "Unsupported native flag condition.");
        bool negate;
        if (equal) { Emit(OpCodes.Ceq); negate = mnemonic is Mnemonic.Jne or Mnemonic.Setne; }
        else
        {
            bool less = mnemonic is Mnemonic.Jl or Mnemonic.Jge or Mnemonic.Jb or Mnemonic.Jae or Mnemonic.Setl or Mnemonic.Setge or Mnemonic.Setb or Mnemonic.Setae;
            bool unsigned = mnemonic is Mnemonic.Ja or Mnemonic.Jae or Mnemonic.Jb or Mnemonic.Jbe or Mnemonic.Seta or Mnemonic.Setae or Mnemonic.Setb or Mnemonic.Setbe;
            Require(mnemonic is Mnemonic.Jl or Mnemonic.Jle or Mnemonic.Jg or Mnemonic.Jge or Mnemonic.Ja or Mnemonic.Jae or Mnemonic.Jb or Mnemonic.Jbe
                or Mnemonic.Setl or Mnemonic.Setle or Mnemonic.Setg or Mnemonic.Setge or Mnemonic.Seta or Mnemonic.Setae or Mnemonic.Setb or Mnemonic.Setbe, "Unsupported conditional branch.");
            Emit(less ? (unsigned ? OpCodes.Clt_Un : OpCodes.Clt) : (unsigned ? OpCodes.Cgt_Un : OpCodes.Cgt));
            negate = mnemonic is Mnemonic.Jge or Mnemonic.Jle or Mnemonic.Jae or Mnemonic.Jbe or Mnemonic.Setge or Mnemonic.Setle or Mnemonic.Setae or Mnemonic.Setbe;
        }
        if (negate) { Emit(OpCodes.Ldc_I4_0); Emit(OpCodes.Ceq); }
    }
    private CI Label(ulong address) { if (!labels.TryGetValue(address, out var label)) labels[address] = label = CI.Create(OpCodes.Nop); return label; }
    private int StackOffset() => checked((int)(long)current.MemoryDisplacement64 - state.Stack);
    private Value Get(Register register)
    {
        Require(state.Registers.TryGetValue(register.GetFullRegister(), out var value), "Read of unknown native register: " + register);
        return value;
    }
    private void Set(Register register, TypeReference type)
    {
        var key = (register.GetFullRegister(), type.FullName);
        if (!slots.TryGetValue(key, out var value)) { value = NewValue(type); slots[key] = value; }
        Emit(OpCodes.Stloc, value.Local); state.Registers[key.Item1] = value;
    }
    private Value NewValue(TypeReference type)
    { var local = new VariableDefinition(method.Module.ImportReference(type)); body.Variables.Add(local); return new Value(type, local); }
    private Value Temporary(TypeReference type)
    {
        var key = (current.IP, temporaryIndex++, type.FullName);
        if (!temporaries.TryGetValue(key, out var value)) temporaries[key] = value = NewValue(type);
        Emit(OpCodes.Stloc, value.Local); return value;
    }
    private Value Snapshot(Value value) { Load(value); return Temporary(value.Type); }
    private void Load(Value value) => Emit(OpCodes.Ldloc, value.Local);
    private void Constant(ulong bits, TypeReference type)
    {
        switch (Kind(type))
        {
            case "i4": Emit(OpCodes.Ldc_I4, unchecked((int)bits)); break;
            case "i8": output.Add(CI.Create(OpCodes.Ldc_I8, unchecked((long)bits))); break;
            case "f4": output.Add(CI.Create(OpCodes.Ldc_R4, BitConverter.Int32BitsToSingle(unchecked((int)bits)))); break;
            case "f8": output.Add(CI.Create(OpCodes.Ldc_R8, BitConverter.Int64BitsToDouble(unchecked((long)bits)))); break;
            default: throw new Unsupported("Unsupported constant type.");
        }
    }
    private static long Immediate(NI i, int operand) => i.GetOpKind(operand) switch
    {
        OpKind.Immediate8 => i.Immediate8, OpKind.Immediate16 => i.Immediate16, OpKind.Immediate32 => unchecked((int)i.Immediate32),
        OpKind.Immediate64 => unchecked((long)i.Immediate64), OpKind.Immediate8to32 => i.Immediate8to32,
        OpKind.Immediate8to64 => i.Immediate8to64, OpKind.Immediate32to64 => i.Immediate32to64, _ => throw new Unsupported("Unsupported immediate.")
    };
    private TypeReference SystemType(string name) => method.Module.GetType("System." + name) ?? (name switch
    {
        "Int32" => method.Module.TypeSystem.Int32, "Int64" => method.Module.TypeSystem.Int64,
        "Single" => method.Module.TypeSystem.Single, "Double" => method.Module.TypeSystem.Double,
        "Boolean" => method.Module.TypeSystem.Boolean, _ => throw new Unsupported("Unknown scalar system type.")
    });
    private static string Kind(TypeReference type) => type.FullName switch
    {
        "System.Boolean" or "System.Char" or "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => "i4",
        "System.Int64" or "System.UInt64" => "i8", "System.Single" => "f4", "System.Double" => "f8",
        _ => !type.IsValueType && type.MetadataType is MetadataType.Class or MetadataType.Object or MetadataType.String or MetadataType.Array ? "ref" : "unsupported"
    };
    private static bool Supported(TypeReference type) => Kind(type) != "unsupported" && !type.ContainsGenericParameter;
    private static bool IsFloat(TypeReference type) => Kind(type) is "f4" or "f8";
    private static int Size(TypeReference type) => type.FullName switch
    { "System.Boolean" or "System.Byte" or "System.SByte" => 1, "System.Char" or "System.Int16" or "System.UInt16" => 2, _ => Kind(type) is "i8" or "f8" or "ref" ? 8 : 4 };
    private static bool Compatible(TypeReference left, TypeReference right) => left.FullName == right.FullName || Kind(left) == "i4" && Kind(right) == "i4" || Kind(left) == "i8" && Kind(right) == "i8";
    private void Emit(OpCode code) => output.Add(CI.Create(code));
    private void Emit(OpCode code, int value) => output.Add(CI.Create(code, value));
    private void Emit(OpCode code, CI target) => output.Add(CI.Create(code, target));
    private void Emit(OpCode code, VariableDefinition local) => output.Add(CI.Create(code, local));
    private void Emit(OpCode code, ParameterDefinition parameter) => output.Add(CI.Create(code, parameter));
    private void Emit(OpCode code, FieldReference field) => output.Add(CI.Create(code, field));
    private void Emit(OpCode code, MethodReference target) => output.Add(CI.Create(code, target));
}

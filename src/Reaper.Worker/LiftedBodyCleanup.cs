using Mono.Cecil.Cil;

namespace Reaper.Worker;

internal static class LiftedBodyCleanup
{
    public static void Run(MethodBody body, bool hasLoop)
    {
        // Do not propagate snapshots across loops: an earlier iteration can own their value.
        if (!hasLoop)
            for (int pass = 0; pass < 12; pass++)
            {
                var stores = body.Instructions.Where(i => i.OpCode == OpCodes.Stloc)
                    .GroupBy(i => (VariableDefinition)i.Operand).ToDictionary(g => g.Key, g => g.Count());
                bool changed = false;
                for (int n = 0; n + 1 < body.Instructions.Count; n++)
                {
                    var load = body.Instructions[n]; var store = body.Instructions[n + 1];
                    if (load.OpCode != OpCodes.Ldloc || store.OpCode != OpCodes.Stloc) continue;
                    var source = (VariableDefinition)load.Operand; var target = (VariableDefinition)store.Operand;
                    if (source == target || source.VariableType.FullName != target.VariableType.FullName
                        || stores.GetValueOrDefault(source) != 1 || stores.GetValueOrDefault(target) != 1) continue;
                    foreach (var instruction in body.Instructions)
                        if (instruction.OpCode == OpCodes.Ldloc && instruction.Operand == target) instruction.Operand = source;
                    Nop(load); Nop(store); changed = true;
                }
                if (!changed) break;
            }
        // Drop only dead copies of locals/constants. Field reads, calls and other effects stay.
        for (int pass = 0; pass < 4; pass++)
        {
            var read = body.Instructions.Where(i => i.OpCode == OpCodes.Ldloc).Select(i => i.Operand).ToHashSet();
            for (int n = 1; n < body.Instructions.Count; n++)
            {
                var store = body.Instructions[n]; var previous = body.Instructions[n - 1];
                if (store.OpCode == OpCodes.Stloc && !read.Contains(store.Operand)
                    && (previous.OpCode == OpCodes.Ldloc || previous.OpCode.Code is Code.Ldc_I4 or Code.Ldc_I8 or Code.Ldc_R4 or Code.Ldc_R8))
                { Nop(previous); Nop(store); }
            }
        }
    }
    private static void Nop(Instruction instruction) { instruction.OpCode = OpCodes.Nop; instruction.Operand = null; }
}

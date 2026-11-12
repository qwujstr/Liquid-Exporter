using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Reaper.Worker;

/// <summary>Validated P-code expression subset; C pseudocode is never parsed as C#.</summary>
public static class GhidraScalarRecovery
{
    public static bool Eligible(MethodDefinition method)=>method.IsStatic&&!method.IsConstructor&&!method.HasGenericParameters
        &&!method.DeclaringType.HasGenericParameters&&method.Parameters.Count<=4
        &&method.Parameters.All(p=>IntegerSize(p.ParameterType) is 4 or 8)
        &&(IntegerSize(method.ReturnType) is 4 or 8 || method.ReturnType.FullName=="System.Boolean");
    public static bool TryRecover(MethodDefinition method,GhidraEvidence evidence,out string reason)
    {
        reason="Ghidra result is outside the validated pure-scalar subset.";
        if(!Eligible(method)||!evidence.Completed||!evidence.PureScalarCandidate||evidence.Blocks.ValueKind!=JsonValueKind.Array||evidence.Blocks.GetArrayLength()!=1||evidence.ReturnExpression.ValueKind!=JsonValueKind.Object)return false;
        var body=new MethodBody(method){MaxStackSize=128};var il=body.GetILProcessor();
        int nodes=0;
        int Emit(JsonElement expression,int depth)
        {
            if(depth>64||++nodes>1024)throw new InvalidDataException("P-code expression budget exceeded.");
            string op=expression.GetProperty("Op").GetString();int size=expression.GetProperty("Size").GetInt32();
            if(size is not(1 or 2 or 4 or 8))throw new InvalidDataException("Unsupported integer width.");
            if(op=="CONST")
            {
                ulong bits=ulong.Parse(expression.GetProperty("Value").GetString(),System.Globalization.CultureInfo.InvariantCulture);
                if(size==8)il.Emit(OpCodes.Ldc_I8,unchecked((long)bits));else il.Emit(OpCodes.Ldc_I4,unchecked((int)(bits & (size==4?uint.MaxValue:(1UL<<(size*8))-1))));
                return size;
            }
            if(op=="ARG")
            {
                int index=expression.GetProperty("Arg").GetInt32();
                if(index<0||index>=method.Parameters.Count||IntegerSize(method.Parameters[index].ParameterType)!=size)throw new InvalidDataException("P-code parameter/signature mismatch.");
                il.Emit(OpCodes.Ldarg,method.Parameters[index]);return size;
            }
            var inputs=expression.GetProperty("Inputs").EnumerateArray().ToArray();
            if(inputs.Length is <1 or >2)throw new InvalidDataException("Unsupported expression arity.");
            if(op is "COPY" or "CAST")
            {int source=Emit(inputs[0],depth+1);if(source!=size)throw new InvalidDataException("Unproven cast width.");return size;}
            if(op is "INT_ZEXT" or "INT_SEXT")
            {
                int source=Emit(inputs[0],depth+1);if(source>=size)throw new InvalidDataException("Invalid extension.");
                bool signed=op=="INT_SEXT";
                if(source==1)il.Emit(signed?OpCodes.Conv_I1:OpCodes.Conv_U1);else if(source==2)il.Emit(signed?OpCodes.Conv_I2:OpCodes.Conv_U2);
                if(size==8)il.Emit(signed?OpCodes.Conv_I8:OpCodes.Conv_U8);return size;
            }
            int left=Emit(inputs[0],depth+1);
            if(inputs.Length==1)
            {
                if(op=="INT_NEGATE"){if(left!=size||size<4)throw new InvalidDataException("Partial bitwise operand.");il.Emit(OpCodes.Not);}
                else if(op=="INT_2COMP"){if(left!=size||size<4)throw new InvalidDataException("Partial negate operand.");il.Emit(OpCodes.Neg);}
                else if(op=="BOOL_NEGATE"){if(left!=1||size!=1)throw new InvalidDataException("Boolean operand width mismatch.");il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Ceq);}
                else throw new InvalidDataException("Unsupported unary P-code operation.");
                return size;
            }
            int right=Emit(inputs[1],depth+1);
            bool comparison=op is "INT_EQUAL" or "INT_NOTEQUAL" or "INT_LESS" or "INT_SLESS" or "INT_LESSEQUAL" or "INT_SLESSEQUAL";
            bool shift=op is "INT_LEFT" or "INT_RIGHT" or "INT_SRIGHT";
            if(shift){if(inputs[1].GetProperty("Op").GetString()!="CONST"||!ulong.TryParse(inputs[1].GetProperty("Value").GetString(),out ulong shiftCount)||shiftCount>=(ulong)(size*8))throw new InvalidDataException("Unproven shift count semantics.");if(left!=size||size<4)throw new InvalidDataException("Shift width mismatch.");if(right==8)il.Emit(OpCodes.Conv_I4);}
            else if(left!=right || !comparison&&(left!=size||size<4))throw new InvalidDataException("P-code operand widths differ.");
            if(comparison&&(size!=1||left<4))throw new InvalidDataException("Comparison result is not boolean.");
            switch(op)
            {
                case "INT_ADD":il.Emit(OpCodes.Add);break;case "INT_SUB":il.Emit(OpCodes.Sub);break;case "INT_MULT":il.Emit(OpCodes.Mul);break;
                case "INT_AND":case "BOOL_AND":il.Emit(OpCodes.And);break;case "INT_OR":case "BOOL_OR":il.Emit(OpCodes.Or);break;case "INT_XOR":case "BOOL_XOR":il.Emit(OpCodes.Xor);break;
                case "INT_LEFT":il.Emit(OpCodes.Shl);break;case "INT_RIGHT":il.Emit(OpCodes.Shr_Un);break;case "INT_SRIGHT":il.Emit(OpCodes.Shr);break;
                case "INT_EQUAL":il.Emit(OpCodes.Ceq);break;case "INT_NOTEQUAL":il.Emit(OpCodes.Ceq);il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Ceq);break;
                case "INT_LESS":il.Emit(OpCodes.Clt_Un);break;case "INT_SLESS":il.Emit(OpCodes.Clt);break;
                case "INT_LESSEQUAL":il.Emit(OpCodes.Cgt_Un);il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Ceq);break;
                case "INT_SLESSEQUAL":il.Emit(OpCodes.Cgt);il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Ceq);break;
                default:throw new InvalidDataException("Unsupported P-code operation: "+op);
            }
            return size;
        }
        try
        {
            int size=Emit(evidence.ReturnExpression,0);
            int expected=method.ReturnType.FullName=="System.Boolean"?1:IntegerSize(method.ReturnType);
            if(size!=expected)throw new InvalidDataException("Native return width does not match metadata.");
            il.Emit(OpCodes.Ret);method.Body=body;
            reason="Ghidra high P-code pure expression translated to CIL; no memory access, indirect operations or calls accepted.";return true;
        }
        catch(Exception ex)when(ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {reason="P-code translation rejected: "+ex.Message;return false;}
    }
    private static int IntegerSize(TypeReference type)=>type.FullName switch{"System.Int32" or "System.UInt32"=>4,"System.Int64" or "System.UInt64"=>8,_=>0};
}
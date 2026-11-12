// IL2CPP Liquid: structured native evidence for metadata-correlated methods.
// @category Liquid
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.data.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.*;
import ghidra.program.model.symbol.SourceType;
import com.google.gson.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

public class LiquidAnalyze extends GhidraScript {
    private final Gson gson = new GsonBuilder().setPrettyPrinting().create();
    private JsonObject expr(Varnode node, int depth, Set<Varnode> seen) {
        if(node == null || depth > 64 || !seen.add(node)) return null;
        JsonObject result = new JsonObject(); result.addProperty("Size",node.getSize());
        if(node.isConstant()) { result.addProperty("Op","CONST"); result.addProperty("Value",Long.toUnsignedString(node.getOffset())); return result; }
        if(node instanceof VarnodeAST && ((VarnodeAST)node).getHigh() instanceof HighParam param) {
            result.addProperty("Op","ARG"); result.addProperty("Arg",param.getSlot()); return result;
        }
        PcodeOp definition=node.getDef(); if(definition == null)return null;
        String op=definition.getMnemonic();
        Set<String> supported=Set.of("COPY","CAST","INT_ZEXT","INT_SEXT","INT_ADD","INT_SUB","INT_MULT","INT_AND","INT_OR","INT_XOR","INT_NEGATE","INT_2COMP","INT_LEFT","INT_RIGHT","INT_SRIGHT","INT_EQUAL","INT_NOTEQUAL","INT_LESS","INT_SLESS","INT_LESSEQUAL","INT_SLESSEQUAL","BOOL_NEGATE","BOOL_AND","BOOL_OR","BOOL_XOR");
        if(!supported.contains(op))return null;
        JsonArray inputs=new JsonArray();
        for(int i=0;i<definition.getNumInputs();i++) { JsonObject value=expr(definition.getInput(i),depth+1,new HashSet<>(seen));if(value==null)return null;inputs.add(value); }
        result.addProperty("Op",op);result.add("Inputs",inputs);return result;
    }
    private DataType type(int size, boolean floating) {
        if(size==0)return VoidDataType.dataType;
        if(floating)return size==8?DoubleDataType.dataType:FloatDataType.dataType;
        return size==8?LongLongDataType.dataType:size==2?ShortDataType.dataType:size==1?ByteDataType.dataType:IntegerDataType.dataType;
    }
    @Override public void run() throws Exception {
        String[] args=getScriptArgs();
        JsonArray requests=JsonParser.parseString(Files.readString(Path.of(args[0]),StandardCharsets.UTF_8)).getAsJsonArray();
        Path output=Path.of(args[1]);Files.createDirectories(output);
        DecompInterface decompiler=new DecompInterface();
        decompiler.toggleCCode(true);decompiler.toggleSyntaxTree(true);decompiler.openProgram(currentProgram);
        try {
            for(JsonElement item: requests) {
                monitor.checkCancelled();JsonObject request=item.getAsJsonObject();
                String addressText=request.get("Address").getAsString();JsonObject result=new JsonObject();
                result.addProperty("Address",addressText);result.addProperty("Backend","Ghidra 12.2");
                try {
                    Address address=toAddr(Long.parseUnsignedLong(addressText.replace("0x",""),16));
                    disassemble(address);Function function=getFunctionAt(address);
                    if(function==null)function=createFunction(address,"Liquid_"+addressText.replace("0x",""));
                    if(function==null)throw new Exception("Function could not be created");
                    JsonArray parameters=request.getAsJsonArray("Parameters");List<Variable> variables=new ArrayList<>();
                    for(int i=0;i<parameters.size();i++) { JsonObject p=parameters.get(i).getAsJsonObject();variables.add(new ParameterImpl("arg"+i,type(p.get("Size").getAsInt(),p.get("Floating").getAsBoolean()),currentProgram)); }
                    function.setCallingConvention(currentProgram.getCompilerSpec().getDefaultCallingConvention().getName());
                    function.setReturnType(type(request.get("ReturnSize").getAsInt(),request.get("ReturnFloating").getAsBoolean()),SourceType.USER_DEFINED);
                    function.replaceParameters(variables,Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS,true,SourceType.USER_DEFINED);
                    DecompileResults decoded=decompiler.decompileFunction(function,3,monitor);
                    result.addProperty("Completed",decoded.decompileCompleted());result.addProperty("Error",decoded.getErrorMessage());
                    if(decoded.decompileCompleted() && decoded.getHighFunction()!=null) {
                        HighFunction high=decoded.getHighFunction();result.addProperty("C",decoded.getDecompiledFunction().getC());
                        JsonArray ops=new JsonArray();List<PcodeOp> returns=new ArrayList<>();boolean pure=true;
                        Iterator<PcodeOpAST> iterator=high.getPcodeOps();
                        while(iterator.hasNext()) {
                            PcodeOpAST op=iterator.next();JsonObject entry=new JsonObject();entry.addProperty("Op",op.getMnemonic());entry.addProperty("Address",op.getSeqnum().getTarget().toString());
                            if(op.getOutput()!=null)entry.addProperty("Output",op.getOutput().toString());
                            JsonArray inputs=new JsonArray();for(int i=0;i<op.getNumInputs();i++)inputs.add(op.getInput(i).toString());entry.add("Inputs",inputs);ops.add(entry);
                            int code=op.getOpcode();if(code==PcodeOp.RETURN)returns.add(op);
                            if(code==PcodeOp.LOAD||code==PcodeOp.STORE||code==PcodeOp.CALL||code==PcodeOp.CALLIND||code==PcodeOp.CALLOTHER||code==PcodeOp.BRANCHIND||code==PcodeOp.INDIRECT) pure=false;
                        }
                        JsonArray blocks=new JsonArray();
                        for(PcodeBlockBasic block:high.getBasicBlocks()) { JsonObject b=new JsonObject();b.addProperty("Index",block.getIndex());b.addProperty("Start",block.getStart().toString());JsonArray successors=new JsonArray();for(int n=0;n<block.getOutSize();n++)successors.add(block.getOut(n).getIndex());b.add("Successors",successors);blocks.add(b); }
                        result.add("Pcode",ops);result.add("Blocks",blocks);
                        result.addProperty("PureScalarCandidate",pure && returns.size()==1);
                        if(pure&&returns.size()==1&&returns.get(0).getNumInputs()==2)result.add("ReturnExpression",expr(returns.get(0).getInput(1),0,new HashSet<>()));
                    }
                } catch(Exception error) { result.addProperty("Completed",false);result.addProperty("Error",error.toString()); }
                Files.writeString(output.resolve(addressText.replace("0x","")+".json"),gson.toJson(result),StandardCharsets.UTF_8);
            }
        } finally {decompiler.dispose();}
    }
}
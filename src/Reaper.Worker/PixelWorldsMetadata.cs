using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.PortableExecutable;
using Il2CppDumper;
using Reaper.Core;

namespace Reaper.Worker;

/// <summary>Strict, build-specific offline adapter recovered from the supplied Windows loader.</summary>
public static class PixelWorldsMetadata
{
    public const string Profile = "pixel-worlds-win64-6000.3.27f2";
    public const ulong CodeRegistration = 0x1858D2770;
    public const ulong MetadataRegistration = 0x185D7C940;
    public static byte[] Normalize(byte[] metadata, byte[] binary)
    {
        if (!ProtectedMetadataProfiles.Matches(metadata, binary)) throw new InvalidDataException("The protected metadata profile does not match this exact native/metadata build pair.");
        using var pe = new PEReader(new MemoryStream(binary));
        int Off(uint rva)
        {
            foreach (var section in pe.PEHeaders.SectionHeaders)
                if (rva >= section.VirtualAddress && rva - section.VirtualAddress < section.SizeOfRawData)
                    return checked(section.PointerToRawData + (int)(rva - section.VirtualAddress));
            throw new InvalidDataException("Profile RVA is outside the native image.");
        }
        byte[] key = binary.AsSpan(Off(0x5EB0450), 256).ToArray();
        byte[] header = metadata.AsSpan(0, 0x4ec).ToArray();
        Transform(header, key, 0x6EBF187B88208015);
        int H(int field) => BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(field));
        byte[] Table(int field, int delta)
        {
            int offset = checked(H(field) + delta), size = H(field + 4);
            if (offset < header.Length || size < 0 || offset > metadata.Length - size) throw new InvalidDataException($"Profile table bounds failed: {field:X}.");
            return metadata.AsSpan(offset, size).ToArray();
        }
        byte[] Decrypt(int field, int delta, ulong seed) { byte[] value = Table(field, delta); Transform(value, key, seed); return value; }
        byte[] names = Decrypt(0x130, 0xfec, 0x9879EA72B52A454C);
        byte[] fields = Decrypt(0x28c, 0x1ae0, 0x1A6ED0FB589F629A);
        byte[] assemblies = Decrypt(0x88, 0x5a0, 0x67C6DD6AE7DC9FB1);
        byte[] methods = Decrypt(0x1c0, -0x14dc, 0x6029FF6DC380FD1C);
        byte[] properties = Decrypt(0x31c, -0x170c, 0xF11C36E0114DA38B);
        byte[] defaults = Decrypt(0x10c, 0x70c, 0x3478C12ABF11149B);
        int typeWidth = Width(BinaryPrimitives.ReadInt32LittleEndian(binary.AsSpan(Off(0x5D7C970))));
        int definitionWidth = Width(H(0x4e0)), genericWidth = Width(H(0x1f8)), parameterWidth = Width(H(0x354));
        if (typeWidth != 2 || definitionWidth != 2 || genericWidth != 2 || parameterWidth != 4 || H(0x4e0) != 19454 || H(0x1bc) != 136459)
            throw new InvalidDataException("Build-specific metadata counts do not match the verified profile.");
        var fieldDefs = Decode<Il2CppFieldDefinition>(fields, new() { ["typeIndex"] = typeWidth });
        var typeDefs = Decode<Il2CppTypeDefinition>(Table(0x4e4, 0x744), new() { ["byvalTypeIndex"] = typeWidth, ["declaringTypeIndex"] = typeWidth, ["parentIndex"] = typeWidth, ["genericContainerIndex"] = genericWidth }, skipElement: true);
        foreach (var type in typeDefs)
            if (type.IsEnum && type.fieldStart >= 0 && type.fieldStart < fieldDefs.Length) type.elementTypeIndex = fieldDefs[type.fieldStart].typeIndex;
        var methodDefs = Decode<Il2CppMethodDefinition>(methods, new() { ["declaringType"] = definitionWidth, ["returnType"] = typeWidth, ["parameterStart"] = parameterWidth, ["genericContainerIndex"] = genericWidth });
        var imageData = Table(0x268, 0xc20);
        var imageDefs = Decode<Il2CppImageDefinition>(imageData, new() { ["typeStart"] = definitionWidth, ["exportedTypeStart"] = definitionWidth });
        foreach (var image in imageDefs)
        {
            image.customAttributeStart = 0; image.customAttributeCount = 0;
            // The forwarding table is not exported by this adapter. Do not leave
            // dangling ranges that stricter consumers would interpret as valid.
            image.exportedTypeStart = 0; image.exportedTypeCount = 0;
        }
        byte[] assemblyData = new byte[H(0x84) * 64];
        if (assemblies.Length != H(0x84) * 68) throw new InvalidDataException("Assembly table size mismatch.");
        for (int i = 0; i < H(0x84); i++)
        {
            assemblies.AsSpan(i * 68, 8).CopyTo(assemblyData.AsSpan(i * 64));
            assemblies.AsSpan(i * 68 + 12, 56).CopyTo(assemblyData.AsSpan(i * 64 + 8));
            assemblyData.AsSpan(i * 64 + 8, 8).Clear(); // References are reconstructed from type usage.
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(new byte[512]);
        var standard = new Il2CppGlobalMetadataHeader { sanity = 0xFAB11BAF, version = 31 };
        void Add(string name, byte[] data)
        {
            var offset = typeof(Il2CppGlobalMetadataHeader).GetField(name + "Offset")!;
            offset.SetValue(standard, offset.FieldType == typeof(uint) ? (object)(uint)output.Position : (int)output.Position);
            typeof(Il2CppGlobalMetadataHeader).GetField(name + "Size")!.SetValue(standard, data.Length);
            writer.Write(data);
        }
        Add("string", names);
        Add("fields", Serialize(fieldDefs));
        Add("typeDefinitions", Serialize(typeDefs));
        Add("methods", Serialize(methodDefs));
        Add("images", Serialize(imageDefs));
        Add("assemblies", assemblyData);
        Add("properties", properties);
        Add("parameters", Serialize(Decode<Il2CppParameterDefinition>(Table(0x358, 0x54c), new() { ["typeIndex"] = typeWidth })));
        Add("events", Serialize(Decode<Il2CppEventDefinition>(Table(0xc4, -0x2ac), new() { ["typeIndex"] = typeWidth })));
        Add("genericContainers", Table(0x1fc, 0x16f0));
        Add("genericParameters", Serialize(Decode<Il2CppGenericParameter>(Table(0x37c, -0x163c), new() { ["ownerIndex"] = genericWidth })));
        Add("genericParameterConstraints", ExpandIndices(Table(0x1a8, -0x3c8), typeWidth));
        Add("nestedTypes", Table(0x40, -0x330));
        Add("interfaces", ExpandIndices(Table(0x58, 0x1b44), typeWidth));
        // GlobalMetadata::GetMethodInfoFromVTableSlot, VA 0x1809748C0.
        Add("vtableMethods", Table(0x310, -0x770));
        // GlobalMetadata::GetInterfaceOffsetInfo, VA 0x180974FF0: compact type + Int32 offset.
        byte[] interfaceOffsets = Table(0x19c, 0x10ac);
        using (var expanded = new MemoryStream())
        using (var pairs = new BinaryWriter(expanded))
        {
            if (interfaceOffsets.Length != 18846 * 6) throw new InvalidDataException("Interface offset table size mismatch.");
            for (int offset = 0; offset < interfaceOffsets.Length; offset += 6)
            {
                ushort index = BinaryPrimitives.ReadUInt16LittleEndian(interfaceOffsets.AsSpan(offset));
                pairs.Write(index == ushort.MaxValue ? -1 : (int)index);
                pairs.Write(BinaryPrimitives.ReadInt32LittleEndian(interfaceOffsets.AsSpan(offset + 2)));
            }
            Add("interfaceOffsets", expanded.ToArray());
        }
        Add("fieldDefaultValues", Serialize(Decode<Il2CppFieldDefaultValue>(Table(0x3b8, 0xebc), new() { ["typeIndex"] = typeWidth })));
        Add("fieldAndParameterDefaultValueData", Table(0x244, -0x12b0));
        output.Position = 0;
        Write(writer, standard);
        byte[] result = output.ToArray();
        using var parsed = new Metadata(new MemoryStream(result));
        Validate(parsed);
        return result;
    }
    private static T[] Decode<T>(byte[] data, Dictionary<string, int> widths, bool skipElement = false, bool skipEntryPoint = false) where T : new()
    {
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);
        var rows = new List<T>();
        var fields = ActiveFields(typeof(T));
        while (stream.Position < stream.Length)
        {
            T row = new();
            foreach (var field in fields)
            {
                if ((skipElement && field.Name == "elementTypeIndex") || (skipEntryPoint && field.Name == "entryPointIndex")) { field.SetValue(row, -1); continue; }
                if (widths.TryGetValue(field.Name, out int width)) { field.SetValue(row, ReadIndex(reader, width)); continue; }
                if (field.FieldType == typeof(int)) field.SetValue(row, reader.ReadInt32());
                else if (field.FieldType == typeof(uint)) field.SetValue(row, reader.ReadUInt32());
                else if (field.FieldType == typeof(ushort)) field.SetValue(row, reader.ReadUInt16());
                else if (field.FieldType == typeof(short)) field.SetValue(row, reader.ReadInt16());
                else throw new NotSupportedException("Unsupported compact field: " + field.Name);
            }
            rows.Add(row);
        }
        return rows.ToArray();
    }
    private static byte[] ExpandIndices(byte[] bytes, int width)
    {
        using var input = new BinaryReader(new MemoryStream(bytes));
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        while (input.BaseStream.Position < bytes.Length) writer.Write(ReadIndex(input, width));
        return output.ToArray();
    }
    private static int ReadIndex(BinaryReader reader, int width) => width switch { 1 => reader.ReadByte() is var b && b != byte.MaxValue ? b : -1, 2 => reader.ReadUInt16() is var s && s != ushort.MaxValue ? s : -1, 4 => reader.ReadInt32(), _ => throw new InvalidDataException("Invalid compact width.") };
    private static int Width(int count) => count <= 255 ? 1 : count <= 65535 ? 2 : 4;
    private static FieldInfo[] ActiveFields(Type type) => type.GetFields().Where(f => { var versions = f.GetCustomAttributes<VersionAttribute>().ToArray(); return versions.Length == 0 || versions.Any(v => 31 >= v.Min && 31 <= v.Max); }).ToArray();
    private static byte[] Serialize<T>(IEnumerable<T> rows) { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); foreach (var row in rows) Write(writer, row!); return stream.ToArray(); }
    private static void Write(BinaryWriter writer, object value)
    {
        foreach (var field in ActiveFields(value.GetType()))
        {
            object item = field.GetValue(value)!;
            switch (item) { case int i: writer.Write(i); break; case uint u: writer.Write(u); break; case ushort s: writer.Write(s); break; case short s: writer.Write(s); break; default: throw new NotSupportedException(field.Name); }
        }
    }
    public static void Transform(Span<byte> data, ReadOnlySpan<byte> key, ulong state)
    {
        if (key.Length != 256 || state == 0) throw new ArgumentException("Invalid transform parameters.");
        for (int i = 0; i < data.Length; i++) { state ^= state << 13; state ^= state >> 7; state ^= state << 17; byte index = (byte)(state ^ (state >> 8) ^ (state >> 16) ^ (state >> 24)); data[i] ^= key[index]; }
    }
    private static void Validate(Metadata metadata)
    {
        if (metadata.typeDefs.Length != 19454 || metadata.methodDefs.Length != 136459 || metadata.imageDefs.Length != 108 ||
            metadata.fieldDefs.Length != 108175 || metadata.parameterDefs.Length != 499778 || metadata.eventDefs.Length != 1183 ||
            metadata.propertyDefs.Length != 19727 || metadata.genericContainers.Length != 1954 || metadata.genericParameters.Length != 3117)
            throw new InvalidDataException("Normalized metadata counts failed validation.");
        void Name(uint index) { if (index >= metadata.header.stringSize) throw new InvalidDataException("Metadata name is outside the decoded string heap."); metadata.GetStringFromIndex(index); }
        foreach (var image in metadata.imageDefs) if (image.typeStart < 0 || image.typeCount > metadata.typeDefs.Length - image.typeStart) throw new InvalidDataException("Image type range failed validation.");
        foreach (var type in metadata.typeDefs) { Name(type.nameIndex); Name(type.namespaceIndex); if (type.method_count > 0 && (type.methodStart < 0 || type.methodStart > metadata.methodDefs.Length - type.method_count)) throw new InvalidDataException("Type method range failed validation."); }
        foreach (var method in metadata.methodDefs) { Name(method.nameIndex); if (method.declaringType < 0 || method.declaringType >= metadata.typeDefs.Length || (method.parameterCount > 0 && (method.parameterStart < 0 || method.parameterStart > metadata.parameterDefs.Length - method.parameterCount))) throw new InvalidDataException("Method ranges failed validation."); }
        foreach (var field in metadata.fieldDefs) Name(field.nameIndex);
        foreach (var parameter in metadata.parameterDefs) Name(parameter.nameIndex);
        foreach (var property in metadata.propertyDefs) Name(property.nameIndex);
        foreach (var eventDef in metadata.eventDefs) Name(eventDef.nameIndex);
        if (metadata.vtableMethods.Length != 205512) throw new InvalidDataException("Vtable method count mismatch.");
        foreach (var type in metadata.typeDefs)
            if (type.vtable_count > 0 && (type.vtableStart < 0 || type.vtableStart > metadata.vtableMethods.Length - type.vtable_count))
                throw new InvalidDataException("Vtable range failed validation.");
        foreach (uint encoded in metadata.vtableMethods)
        {
            uint usage = Metadata.GetEncodedIndexType(encoded), index = metadata.GetDecodedMethodIndex(encoded);
            if (encoded > 1 && (usage is not (3 or 6) || usage == 3 && index >= metadata.methodDefs.Length))
                throw new InvalidDataException("Vtable encoded method reference failed validation.");
        }
    }
}

using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using Mono.Cecil;

namespace Reaper.Worker;

/// <summary>Static PE view. Function ends come from unwind records and known managed entries.</summary>
public sealed class NativeImage
{
    private readonly byte[] bytes;
    private readonly SectionHeader[] sections;
    private readonly Dictionary<uint, uint> ends = new();
    private readonly ulong[] entries;
    public ulong ImageBase { get; }
    public ulong? NullReferenceHelper { get; }
    public IReadOnlyDictionary<ulong, MethodDefinition> ManagedMethods { get; }

    public NativeImage(byte[] bytes, IEnumerable<ulong> methodAddresses, bool verifiedPixelWorlds = false, IReadOnlyDictionary<ulong, MethodDefinition> managedMethods = null)
    {
        this.bytes = bytes;
        ManagedMethods = managedMethods ?? new Dictionary<ulong, MethodDefinition>();
        using var pe = new PEReader(new MemoryStream(bytes));
        sections = pe.PEHeaders.SectionHeaders.ToArray();
        ImageBase = pe.PEHeaders.PEHeader.ImageBase;
        entries = methodAddresses.Where(a => a >= ImageBase).Distinct().Order().ToArray();
        var table = pe.PEHeaders.PEHeader.ExceptionTableDirectory;
        int offset = Offset((ulong)table.RelativeVirtualAddress, table.Size);
        if (offset >= 0)
            for (int i = 0; i + 12 <= table.Size; i += 12)
            {
                uint start = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + i));
                uint end = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + i + 4));
                if (end > start) ends[start] = end;
            }
        // This runtime function was verified by disassembly in this exact, hash-checked build.
        NullReferenceHelper = verifiedPixelWorlds ? 0x180911090UL : null;
    }

    public byte[] Method(ulong address)
    {
        if (address < ImageBase || address - ImageBase > uint.MaxValue) return [];
        uint rva = (uint)(address - ImageBase);
        int offset = Offset(rva, 1, executable: true);
        if (offset < 0) return [];
        ulong end = ends.TryGetValue(rva, out uint unwindEnd) ? ImageBase + unwindEnd : address + 4096;
        int index = Array.BinarySearch(entries, address);
        if (index >= 0 && index + 1 < entries.Length) end = Math.Min(end, entries[index + 1]);
        var section = sections.First(s => rva >= s.VirtualAddress && rva - s.VirtualAddress < s.SizeOfRawData);
        end = Math.Min(end, ImageBase + (uint)section.VirtualAddress + (uint)section.SizeOfRawData);
        int length = (int)Math.Min(16384UL, end - address);
        return length > 0 && offset + length <= bytes.Length ? bytes.AsSpan(offset, length).ToArray() : [];
    }

    public bool ReadConstant(ulong address, int size, out ulong value)
    {
        value = 0;
        if (address < ImageBase) return false;
        ulong rva = address - ImageBase;
        var section = sections.FirstOrDefault(s => rva >= (ulong)s.VirtualAddress && rva - (ulong)s.VirtualAddress < (ulong)s.SizeOfRawData);
        if (section.SizeOfRawData == 0 || section.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite)) return false;
        int offset = Offset(rva, size);
        if (offset < 0) return false;
        value = size switch { 4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)), 8 => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset)), _ => 0 };
        return size is 4 or 8;
    }

    private int Offset(ulong rva, int size, bool executable = false)
    {
        foreach (var s in sections)
        {
            if (executable && !s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute)) continue;
            if (rva < (ulong)s.VirtualAddress) continue;
            ulong relative = rva - (ulong)s.VirtualAddress;
            long offset = s.PointerToRawData + (long)relative;
            if (relative + (ulong)size <= (ulong)s.SizeOfRawData && offset >= 0 && offset + size <= bytes.Length) return (int)offset;
        }
        return -1;
    }
}

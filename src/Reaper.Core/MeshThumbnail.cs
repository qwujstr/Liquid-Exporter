using AssetsTools.NET;
using System.Buffers.Binary;
using System.Numerics;
using StbImageWriteSharp;

namespace Reaper.Core;

internal static class MeshThumbnail
{
    private const int Size = 64;

    public static string? Render(AssetTypeValueField mesh, bool bigEndian)
    {
        var vertices = mesh["m_VertexData"];
        if (vertices.IsDummy || vertices["m_Channels"].IsDummy) return null;
        var channels = vertices["m_Channels"]["Array"].Children;
        var countField = vertices["m_VertexCount"];
        if (channels.Count == 0 || countField.IsDummy) return null;
        int count = checked((int)countField.AsUInt);
        if (count is < 3 or > 500_000) return null;

        // Unity stores the position channel first. Only draw layouts with Float32 positions.
        var position = channels[0];
        if (position["format"].AsInt != 0 || position["stream"].AsInt != 0 || (position["dimension"].AsInt & 15) < 3) return null;
        int offset = position["offset"].AsInt;
        var stride = channels.Where(c => c["stream"].AsInt == 0).Select(c =>
        {
            int dimension = c["dimension"].AsInt & 15;
            return dimension == 0 ? 0 : c["offset"].AsInt + dimension * FormatSize(c["format"].AsInt);
        }).Max();
        byte[] vertexBytes = Bytes(vertices["m_DataSize"]);
        if (offset < 0 || stride < offset + 12 || (long)stride * (count - 1) + offset + 12 > vertexBytes.Length) return null;

        var projected = new Vector2[count];
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        for (int i = 0; i < count; i++)
        {
            int at = i * stride + offset;
            float x = ReadFloat(at), y = ReadFloat(at + 4), z = ReadFloat(at + 8);
            var point = new Vector2(x - .48f * z, -y + .36f * z + .16f * x);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return null;
            projected[i] = point;
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        var extent = max - min;
        float scale = 55f / Math.Max(extent.X, extent.Y);
        if (!float.IsFinite(scale)) return null;
        for (int i = 0; i < count; i++) projected[i] = (projected[i] - min - extent / 2) * scale + new Vector2(31.5f);

        var pixels = new byte[Size * Size * 4];
        byte[] indices = Bytes(mesh["m_IndexBuffer"]);
        var format = mesh["m_IndexFormat"];
        int step = !format.IsDummy && format.AsInt == 1 ? 4 : 2;
        var submeshField = mesh["m_SubMeshes"];
        var submeshes = submeshField.IsDummy ? [] : submeshField["Array"].Children;
        int drawn = 0;
        foreach (var sub in submeshes)
        {
            var topology = sub["topology"];
            if (!topology.IsDummy && topology.AsInt != 0) continue;
            long first = sub["firstByte"].AsUInt;
            long end = first + (long)sub["indexCount"].AsUInt * step;
            if (first < 0 || end > indices.Length) continue;
            long baseVertex = sub["baseVertex"].IsDummy ? 0 : sub["baseVertex"].AsUInt;
            int triangleStride = (int)Math.Max(1, (end - first) / (4500 * 3 * step)) * 3 * step;
            for (long i = first; i + 3 * step <= end && drawn < 6000; i += triangleStride)
            {
                long a = Index((int)i) + baseVertex, b = Index((int)i + step) + baseVertex, c = Index((int)i + 2 * step) + baseVertex;
                if (a >= count || b >= count || c >= count) continue;
                Line(projected[a], projected[b]);
                Line(projected[b], projected[c]);
                Line(projected[c], projected[a]);
                drawn++;
            }
        }
        if (drawn == 0)
            for (int i = 0; i < count; i += Math.Max(1, count / 5000)) Mark((int)projected[i].X, (int)projected[i].Y);

        using var output = new MemoryStream();
        new ImageWriter().WritePng(pixels, Size, Size, ColorComponents.RedGreenBlueAlpha, output);
        return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());

        float ReadFloat(int at) => bigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(vertexBytes.AsSpan(at, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(vertexBytes.AsSpan(at, 4));
        long Index(int at) => step == 4
            ? bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(indices.AsSpan(at, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(indices.AsSpan(at, 4))
            : bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(indices.AsSpan(at, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan(at, 2));

        void Line(Vector2 a, Vector2 b)
        {
            int segments = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b)));
            for (int i = 0; i <= segments; i++)
            {
                var point = Vector2.Lerp(a, b, (float)i / segments);
                Mark((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
            }
        }
        void Mark(int x, int y)
        {
            if ((uint)x >= Size || (uint)y >= Size) return;
            int index = (y * Size + x) * 4;
            pixels[index] = 175;
            pixels[index + 1] = 200;
            pixels[index + 2] = 224;
            pixels[index + 3] = 255;
        }
    }

    private static int FormatSize(int format) => format switch
    {
        0 or 10 or 11 => 4,
        1 or 4 or 5 or 8 or 9 => 2,
        2 or 3 or 6 or 7 => 1,
        _ => throw new NotSupportedException("Unsupported vertex channel format.")
    };

    private static byte[] Bytes(AssetTypeValueField field) =>
        field.Value?.ValueType == AssetValueType.ByteArray ? field.AsByteArray
        : field.Children.Select(child => child.AsByte).ToArray();
}

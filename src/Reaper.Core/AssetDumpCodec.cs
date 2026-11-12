using AssetsTools.NET;
using System.Globalization;
using System.Text.Json;
using System.Text;

namespace Reaper.Core;

public sealed record AssetDumpNode(string Name, string Type, string? Value, List<AssetDumpNode> Children, string? Encoding = null);
public sealed record AssetDump(string Format, int ClassId, string UnityVersion, AssetDumpNode Root);

internal static class AssetDumpCodec
{
    public static AssetDumpNode Read(AssetTypeValueField field)
    {
        string? value = field.Value is null ? null : field.Value.ValueType switch
        {
            AssetValueType.ByteArray => Convert.ToBase64String(field.AsByteArray),
            AssetValueType.String => ValidUtf8(field.AsByteArray) ? field.AsString : Convert.ToBase64String(field.AsByteArray),
            AssetValueType.Array => null,
            AssetValueType.Float => field.AsFloat.ToString("R", CultureInfo.InvariantCulture),
            AssetValueType.Double => field.AsDouble.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(field.Value.AsObject, CultureInfo.InvariantCulture)
        };
        return new(field.TemplateField.Name, field.TemplateField.Type, value, field.Children.Select(Read).ToList(), field.Value?.ValueType == AssetValueType.ByteArray || field.Value?.ValueType == AssetValueType.String && !ValidUtf8(field.AsByteArray) ? "base64" : null);
    }

    private static bool ValidUtf8(byte[] data) { try { new UTF8Encoding(false, true).GetString(data); return true; } catch (DecoderFallbackException) { return false; } }

    public static void Apply(AssetTypeValueField field, AssetDumpNode node)
    {
        if (node.Name != field.TemplateField.Name || node.Type != field.TemplateField.Type || node.Children.Count != field.Children.Count)
            throw new InvalidDataException("Dump structure differs from the selected asset. Use raw import for a different array layout.");
        if (field.Value is not null && node.Value is not null)
        {
            var invariant = CultureInfo.InvariantCulture;
            var value = field.Value;
            switch (value.ValueType)
            {
                case AssetValueType.String: if (node.Encoding == "base64") field.AsByteArray = Convert.FromBase64String(node.Value); else field.AsString = node.Value; break;
                case AssetValueType.ByteArray: field.AsByteArray = Convert.FromBase64String(node.Value); break;
                case AssetValueType.Bool: field.AsBool = bool.Parse(node.Value); break;
                case AssetValueType.Int8: field.AsSByte = sbyte.Parse(node.Value, invariant); break;
                case AssetValueType.UInt8: field.AsByte = byte.Parse(node.Value, invariant); break;
                case AssetValueType.Int16: field.AsShort = short.Parse(node.Value, invariant); break;
                case AssetValueType.UInt16: field.AsUShort = ushort.Parse(node.Value, invariant); break;
                case AssetValueType.Int32: field.AsInt = int.Parse(node.Value, invariant); break;
                case AssetValueType.UInt32: field.AsUInt = uint.Parse(node.Value, invariant); break;
                case AssetValueType.Int64: field.AsLong = long.Parse(node.Value, invariant); break;
                case AssetValueType.UInt64: field.AsULong = ulong.Parse(node.Value, invariant); break;
                case AssetValueType.Float: field.AsFloat = float.Parse(node.Value, invariant); break;
                case AssetValueType.Double: field.AsDouble = double.Parse(node.Value, invariant); break;
                default: throw new NotSupportedException($"Editing {value.ValueType} through a dump is not supported; use raw import.");
            }
        }
        for (int i = 0; i < field.Children.Count; i++) Apply(field.Children[i], node.Children[i]);
    }
}

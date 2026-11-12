using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StbImageSharp;
using StbImageWriteSharp;

namespace Reaper.Core;

public sealed record ModderFile(string Id, string Name, string Path, bool Bundle, int Changes);
public sealed record ModderAsset(string Id, string Name, string Type, int ClassId, string PathId, string Member, long Size, bool Modified);
public sealed record ModderPage(ModderFile File, List<ModderAsset> Assets, string[] Types, int Total, int Offset);
public sealed record ModderPreview(string FileId, string AssetId, string Name, string Type, string? Image, string? Text, string? Error, int Width = 0, int Height = 0, string? Audio = null, bool TextEditable = false, string? TextEncoding = null);
public sealed record ModderSaveResult(string[] Files, string BackupFolder);

/// <summary>AssetsTools.NET workspace. Reads lazily; replacements stay in memory until a verified atomic save.</summary>
public sealed class AssetModder : IDisposable
{
    private sealed class Document(string path, string root)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Path { get; } = path;
        public string Name { get; } = System.IO.Path.GetRelativePath(root, path);
        public bool IsBundle;
        public string? Hash;
        public BundleFileInstance? Bundle;
        public readonly List<AssetsFileInstance> Members = [];
        public List<ModderAsset>? Assets;
        public readonly Dictionary<string, byte[]> Edits = [];
    }
    private readonly AssetsManager manager = new() { UseQuickLookup = true, UseTemplateFieldCache = true, UseMonoTemplateFieldCache = true };
    private readonly List<Document> documents = [];
    private readonly Stack<(string File, string Asset, byte[]? Previous)> history = [];
    public string Root { get; }
    public bool Modified => documents.Any(d => d.Edits.Count > 0);
    public ModderFile[] Files => documents.Select(Describe).ToArray();

    public AssetModder(string input, string classData)
    {
        input = Path.GetFullPath(input);
        if (!Directory.Exists(input) && !File.Exists(input)) throw new FileNotFoundException("Game folder or Unity file not found.", input);
        Root = Directory.Exists(input) ? input : Path.GetDirectoryName(input)!;
        if (!File.Exists(classData)) throw new FileNotFoundException("Unity type database is missing. Rebuild Liquid.", classData);
        manager.LoadClassPackage(classData);
        IEnumerable<string> candidates = File.Exists(input) ? [input] : Directory.EnumerateFiles(Root, "*", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(IsGameData);
        foreach (string path in candidates.OrderBy(p => Path.GetFileName(p).Equals("data.unity3d", StringComparison.OrdinalIgnoreCase) ? 0 : Path.GetFileName(p).StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) ? 1 : Path.GetFileName(p).Equals("resources.assets", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
                     .ThenBy(p => Regex.Replace(p, @"\d+", m => m.Value.PadLeft(10, '0')), StringComparer.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            byte[] magic = new byte[8]; int read = stream.Read(magic);
            bool bundle = read >= 7 && Encoding.ASCII.GetString(magic, 0, 7) is "UnityFS" or "UnityRa" or "UnityWe";
            if (!bundle && !AssetsFile.IsAssetsFile(new AssetsFileReader(stream), 0, stream.Length)) continue;
            documents.Add(new Document(path, Root) { IsBundle = bundle });
        }
        if (documents.Count == 0) throw new InvalidDataException("No Unity serialized files or bundles found. Choose the game's _Data folder or a bundle file.");
    }

    private static bool IsGameData(string path)
    {
        string name = Path.GetFileName(path);
        if (path.Split(Path.DirectorySeparatorChar).Any(part => part is ".liquid-backups" || part.StartsWith("Liquid_", StringComparison.OrdinalIgnoreCase))) return false;
        return name.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".assets", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(name, @"^sharedassets\.?\d+(\.assets)?$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(name, @"^level\d+$", RegexOptions.IgnoreCase) || name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);
    }
    private ModderFile Describe(Document d) => new(d.Id, d.Name, d.Path, d.IsBundle, d.Edits.Count);
    private Document Find(string id) => documents.SingleOrDefault(d => d.Id == id) ?? throw new ArgumentException("The selected file is no longer open.");
    private void Load(Document d, CancellationToken token)
    {
        if (d.Members.Count > 0) return;
        token.ThrowIfCancellationRequested();
        d.Hash ??= Hash(d.Path);
        if (d.IsBundle)
        {
            d.Bundle = manager.Bundles.FirstOrDefault(b => b.path.Equals(d.Path, StringComparison.OrdinalIgnoreCase))
                ?? manager.LoadBundleFile(new FileStream(d.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), true);
            for (int i = 0; i < d.Bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (d.Bundle.file.IsAssetsFile(i)) d.Members.Add(manager.LoadAssetsFileFromBundle(d.Bundle, i, false));
            }
        }
        else d.Members.Add(manager.Files.FirstOrDefault(f => f.path.Equals(d.Path, StringComparison.OrdinalIgnoreCase))
            ?? manager.LoadAssetsFile(new FileStream(d.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), false));
        if (d.Members.Count == 0) throw new InvalidDataException("The bundle contains no serialized asset files.");
        foreach (var member in d.Members)
        {
            manager.LoadClassDatabaseFromPackage(member.file.Metadata.UnityVersion);
            int index = d.Members.IndexOf(member);
            foreach (var info in member.file.Metadata.AssetInfos)
                if (d.Edits.TryGetValue(index + ":" + info.PathId, out var bytes)) info.SetNewData(bytes);
        }
    }
    public ModderPage GetAssets(string fileId, string query = "", string type = "", int offset = 0, CancellationToken token = default)
    {
        var d = Find(fileId); Load(d, token);
        if (d.Assets is null)
        {
            d.Assets = [];
            foreach (var member in d.Members)
            {
                manager.LoadClassDatabaseFromPackage(member.file.Metadata.UnityVersion);
                int index = d.Members.IndexOf(member);
                foreach (var info in member.file.Metadata.AssetInfos)
                {
                    token.ThrowIfCancellationRequested();
                    string className = Enum.GetName(typeof(AssetClassID), info.TypeId) ?? "Class " + info.TypeId;
                    string name;
                    try { name = AssetHelper.GetAssetNameFast(member.file, manager.ClassDatabase, info); } catch { name = className; }
                    if (string.IsNullOrEmpty(name)) name = className;
                    d.Assets.Add(new(index + ":" + info.PathId, name, className, info.TypeId, info.PathId.ToString(), member.name, info.ByteSize, false));
                }
            }
        }
        var matches = d.Assets.Where(a => (type.Length == 0 || a.Type == type) && (query.Length == 0 || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || a.Type.Contains(query, StringComparison.OrdinalIgnoreCase) || a.PathId.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Member.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        offset = 0;
        return new(Describe(d), matches.Select(a => a with { Modified = d.Edits.ContainsKey(a.Id), Size = d.Edits.TryGetValue(a.Id, out var b) ? b.Length : a.Size }).ToList(),
            d.Assets.Select(a => a.Type).Distinct().Order().ToArray(), matches.Count, offset);
    }
    private (Document Doc, AssetsFileInstance File, AssetFileInfo Info) Resolve(string fileId, string assetId)
    {
        var d = Find(fileId); Load(d, default);
        string[] parts = assetId.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int member) || !long.TryParse(parts[1], out long pathId) || member < 0 || member >= d.Members.Count)
            throw new ArgumentException("Invalid asset selection.");
        var file = d.Members[member];
        return (d, file, file.file.GetAssetInfo(pathId) ?? throw new ArgumentException("Asset not found."));
    }
    private AssetTypeValueField Field(AssetsFileInstance file, AssetFileInfo info)
    {
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        return manager.GetBaseField(file, info);
    }
    public ModderPreview Preview(string fileId, string assetId)
    {
        var (d, file, info) = Resolve(fileId, assetId);
        string name = d.Assets?.Find(a => a.Id == assetId)?.Name ?? info.PathId.ToString();
        string type = Enum.GetName(typeof(AssetClassID), info.TypeId) ?? info.TypeId.ToString();
        try
        {
            var field = Field(file, info);
            if (info.TypeId is 28 or 213)
            {
                var image = GetImage(file, info, field);
                return new(fileId, assetId, name, type, "data:image/png;base64," + Convert.ToBase64String(Png(image.Pixels, image.Width, image.Height)), null, null, image.Width, image.Height);
            }
            if (info.TypeId == 83)
            {
                var audio = ModderAudio.Decode(file, field, Root);
                return new(fileId, assetId, name, type, null, null, null, Audio: audio.Bytes.Length <= 24 * 1024 * 1024 ? "data:audio/" + (audio.Extension == "mp3" ? "mpeg" : audio.Extension) + ";base64," + Convert.ToBase64String(audio.Bytes) : null);
            }
            if (info.TypeId == 49)
            {
                try { var decoded = DecodeText(field["m_Script"].AsByteArray); return new(fileId, assetId, name, type, null, decoded.Text, null, TextEditable: true, TextEncoding: decoded.Encoding.WebName); }
                catch (InvalidDataException ex) { return new(fileId, assetId, name, type, null, ex.Message, null); }
                catch (DecoderFallbackException) { return new(fileId, assetId, name, type, null, "Binary TextAsset. Use file export/import to preserve its bytes.", null); }
            }
            if (info.ByteSize > 2 * 1024 * 1024 && info.TypeId != 49) return new(fileId, assetId, name, type, null, "Large structured asset. Export a JSON dump or raw bytes to inspect all data.", null);
            string text = JsonSerializer.Serialize(new AssetDump("Liquid.AssetDump/1", info.TypeId, file.file.Metadata.UnityVersion, AssetDumpCodec.Read(field)), JsonFiles.Options);
            return new(fileId, assetId, name, type, null, text.Length > 100000 ? text[..100000] + "\n…" : text, null);
        }
        catch (Exception ex) { return new(fileId, assetId, name, type, null, null, "Preview unavailable: " + ex.Message + " Raw export/import is still available."); }
    }
    private static (string Text, Encoding Encoding, byte[] Preamble) DecodeText(byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("TextAsset exceeds the 8 MB inline editor limit. Use file export/import.");
        Encoding encoding = new UTF8Encoding(false, true); int skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) throw new InvalidDataException("UTF-32 TextAsset: use file export/import.");
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        string text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) throw new InvalidDataException("Binary TextAsset. Use file export/import to preserve its bytes.");
        return (text, encoding, bytes[..skip]);
    }
    public bool IsAssetModified(string fileId, string assetId) => Find(fileId).Edits.ContainsKey(assetId);
    public void ImportText(string fileId, string assetId, string text)
    {
        var (_, file, info) = Resolve(fileId, assetId);
        if (info.TypeId != 49) throw new InvalidDataException("Only TextAsset can be edited as text.");
        var original = DecodeText(Field(file, info)["m_Script"].AsByteArray);
        if (text == original.Text) return;
        byte[] body = original.Encoding.GetBytes(text);
        if (body.Length + original.Preamble.Length > 8 * 1024 * 1024) throw new InvalidDataException("TextAsset exceeds the 8 MB inline editor limit.");
        Import(fileId, assetId, "decoded", [.. original.Preamble, .. body]);
    }
    public byte[] Export(string fileId, string assetId, string format)
    {
        var (_, file, info) = Resolve(fileId, assetId);
        if (format == "raw") return Raw(file, info);
        var field = Field(file, info);
        if (format == "dump") return JsonSerializer.SerializeToUtf8Bytes(new AssetDump("Liquid.AssetDump/1", info.TypeId, file.file.Metadata.UnityVersion, AssetDumpCodec.Read(field)), JsonFiles.Options);
        if (info.TypeId == 49) return field["m_Script"].AsByteArray;
        if (info.TypeId == 83) return ModderAudio.Decode(file, field, Root).Bytes;
        if (info.TypeId is 28 or 213) { var image = GetImage(file, info, field); return Png(image.Pixels, image.Width, image.Height); }
        throw new NotSupportedException("Use JSON dump or raw bytes for this asset type.");
    }
    public string ExportExtension(string fileId, string assetId, string format)
    {
        var (_, file, info) = Resolve(fileId, assetId);
        if (format == "raw") return "bin";
        if (format == "dump") return "json";
        return info.TypeId switch { 28 or 213 => "png", 49 => "txt", 83 => ModderAudio.Decode(file, Field(file, info), Root).Extension, _ => "bin" };
    }
    public void Import(string fileId, string assetId, string format, byte[] bytes)
    {
        var (d, file, info) = Resolve(fileId, assetId);
        byte[] replacement;
        if (format == "raw")
        {
            if (bytes.Length == 0) throw new InvalidDataException("An asset cannot be replaced with an empty raw file.");
            if (info.TypeId is 1 or 4 or 21 or 28 or 43 or 49 or 83 or 115 or 213)
            {
                var previous = info.Replacer;
                try { info.SetNewData(bytes); _ = Field(file, info); }
                catch (Exception ex) { throw new InvalidDataException("Raw replacement could not be read as the selected Unity asset type.", ex); }
                finally { info.Replacer = previous; }
            }
            replacement = bytes;
        }
        else
        {
            var field = Field(file, info);
            if (format == "dump")
            {
                var dump = JsonSerializer.Deserialize<AssetDump>(bytes, JsonFiles.Options) ?? throw new InvalidDataException("Empty asset dump.");
                if (dump.Format != "Liquid.AssetDump/1" || dump.ClassId != info.TypeId || dump.UnityVersion != file.file.Metadata.UnityVersion)
                    throw new InvalidDataException("Dump type or Unity version differs from the selected asset.");
                AssetDumpCodec.Apply(field, dump.Root);
            }
            else if (info.TypeId == 49) field["m_Script"].AsByteArray = bytes;
            else if (info.TypeId is 28 or 213)
            {
                var imported = ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                if (info.TypeId == 213)
                {
                    var original = GetImage(file, info, field);
                    if (original.Width != imported.Width || original.Height != imported.Height) throw new InvalidDataException($"Sprite replacement must be {original.Width} × {original.Height} pixels.");
                    var target = ResolveSpriteTexture(file, field);
                    var image = Texture(target.File, target.Info, target.Field);
                    var rect = SpriteRect(target.RenderData, image.Width, image.Height);
                    int rotation = Rotation(target.RenderData);
                    for (int row = 0; row < imported.Height; row++) for (int col = 0; col < imported.Width; col++)
                    {
                        var (sx, sy) = PackedCoordinate(col, row, rect.W, rect.H, rotation);
                        imported.Data.AsSpan((row * imported.Width + col) * 4, 4).CopyTo(image.Pixels.AsSpan(((rect.Y + sy) * image.Width + rect.X + sx) * 4, 4));
                    }
                    ReplaceTexture(target.Field, image.Pixels, image.Width, image.Height);
                    var targetDoc = Owner(target.File); string targetKey = targetDoc.Members.IndexOf(target.File) + ":" + target.Info.PathId;
                    SetEdit(targetDoc, targetKey, target.Info, target.Field.WriteToByteArray(target.File.file.Header.Endianness));
                    return;
                }
                if ((long)imported.Width * imported.Height > 67108864) throw new InvalidDataException("Replacement texture is too large.");
                ReplaceTexture(field, imported.Data, imported.Width, imported.Height);
            }
            else throw new NotSupportedException("Use JSON dump or raw bytes to replace this asset type.");
            replacement = field.WriteToByteArray(file.file.Header.Endianness);
        }
        SetEdit(d, assetId, info, replacement);
    }
    private void SetEdit(Document doc, string key, AssetFileInfo info, byte[] bytes)
    {
        history.Push((doc.Id, key, doc.Edits.GetValueOrDefault(key)));
        doc.Edits[key] = bytes; info.SetNewData(bytes);
    }
    public void Undo()
    {
        if (!history.TryPop(out var change)) return;
        var (doc, _, info) = Resolve(change.File, change.Asset);
        if (change.Previous is null) { doc.Edits.Remove(change.Asset); info.Replacer = null; }
        else { doc.Edits[change.Asset] = change.Previous; info.SetNewData(change.Previous); }
    }
    public void Discard()
    {
        foreach (var d in documents) { d.Edits.Clear(); foreach (var m in d.Members) foreach (var i in m.file.Metadata.AssetInfos) i.Replacer = null; }
        history.Clear();
    }
    private Document Owner(AssetsFileInstance file)
    {
        var known = documents.Find(d => d.Members.Contains(file));
        if (known is not null) return known;
        string path = file.parentBundle?.path ?? file.path;
        if (!InputValidation.IsInside(path, Root)) throw new IOException("The referenced texture is outside the opened game folder.");
        var doc = documents.Find(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (doc is null) { doc = new Document(path, Root) { IsBundle = file.parentBundle is not null }; documents.Add(doc); }
        Load(doc, default);
        if (!doc.Members.Contains(file)) throw new IOException("Unable to locate the referenced texture in its file.");
        return doc;
    }
    private (AssetsFileInstance File, AssetFileInfo Info, AssetTypeValueField Field, AssetTypeValueField RenderData) ResolveSpriteTexture(AssetsFileInstance file, AssetTypeValueField sprite)
    {
        var rd = sprite["m_RD"];
        var relative = file;
        var texture = rd["texture"];
        if (texture.IsDummy || texture["m_PathID"].AsLong == 0)
        {
            var atlasPointer = sprite["m_SpriteAtlas"];
            if (atlasPointer.IsDummy) throw new IOException("Sprite has neither texture nor atlas data.");
            var atlas = manager.GetExtAsset(file, atlasPointer);
            if (atlas.file is null || atlas.info is null) throw new IOException("SpriteAtlas dependency could not be resolved.");
            var atlasField = Field(atlas.file, atlas.info);
            byte[] key = sprite["m_RenderDataKey"].WriteToByteArray();
            var match = atlasField["m_RenderDataMap"]["Array"].Children.FirstOrDefault(pair => pair["first"].WriteToByteArray().AsSpan().SequenceEqual(key));
            if (match is null) throw new IOException("Sprite render key is missing from its atlas.");
            rd = match["second"]; texture = rd["texture"]; relative = atlas.file;
        }
        var ext = manager.GetExtAsset(relative, texture);
        if (ext.file is null || ext.info is null) throw new IOException("Sprite texture dependency could not be resolved.");
        return (ext.file, ext.info, Field(ext.file, ext.info), rd);
    }
    private static int Rotation(AssetTypeValueField rd)
    {
        uint settings = rd["settingsRaw"].IsDummy ? 0 : rd["settingsRaw"].AsUInt;
        return (settings & 1) == 0 ? 0 : (int)(settings >> 2 & 15);
    }
    private static (int X, int Y) PackedCoordinate(int col, int row, int width, int height, int rotation) => rotation switch
    {
        0 => (col, row), 1 => (width - 1 - col, row), 2 => (col, height - 1 - row),
        3 => (width - 1 - col, height - 1 - row), 4 => (width - 1 - row, col),
        _ => throw new NotSupportedException("Unsupported sprite packing rotation: " + rotation)
    };
    private (byte[] Pixels, int Width, int Height) GetImage(AssetsFileInstance file, AssetFileInfo info, AssetTypeValueField field)
    {
        if (info.TypeId == 28) return Texture(file, info, field);
        var target = ResolveSpriteTexture(file, field);
        var image = Texture(target.File, target.Info, target.Field);
        var rect = SpriteRect(target.RenderData, image.Width, image.Height);
        int rotation = Rotation(target.RenderData), width = rotation == 4 ? rect.H : rect.W, height = rotation == 4 ? rect.W : rect.H;
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++) for (int col = 0; col < width; col++)
        {
            var (sx, sy) = PackedCoordinate(col, row, rect.W, rect.H, rotation);
            image.Pixels.AsSpan(((rect.Y + sy) * image.Width + rect.X + sx) * 4, 4).CopyTo(pixels.AsSpan((row * width + col) * 4, 4));
        }
        return (pixels, width, height);
    }
    private static (int X, int Y, int W, int H) SpriteRect(AssetTypeValueField renderData, int width, int height)
    {
        var rect = renderData["textureRect"];
        int x = (int)Math.Round(rect["x"].AsFloat), y = (int)Math.Round(rect["y"].AsFloat), w = (int)Math.Round(rect["width"].AsFloat), h = (int)Math.Round(rect["height"].AsFloat);
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + (long)w > width || y + (long)h > height) throw new InvalidDataException("Invalid sprite rectangle.");
        return (x, height - y - h, w, h);
    }
    private static (byte[] Pixels, int Width, int Height) Texture(AssetsFileInstance file, AssetFileInfo info, AssetTypeValueField field)
    {
        var texture = TextureFile.ReadTextureFile(field);
        if (texture.m_Width <= 0 || texture.m_Height <= 0 || (long)texture.m_Width * texture.m_Height > 67108864) throw new InvalidDataException("Texture dimensions exceed the preview limit.");
        byte[] data = texture.FillPictureData(file);
        using var stream = new MemoryStream();
        if (data is null || !texture.DecodeTextureImage(data, stream, ImageExportType.Png)) throw new IOException("This texture format could not be decoded.");
        var image = ImageResult.FromMemory(stream.ToArray(), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return (image.Data, image.Width, image.Height);
    }
    private static void ReplaceTexture(AssetTypeValueField field, byte[] pixels, int width, int height)
    {
        byte[] encoded = TextureFile.EncodeManagedData(pixels, TextureFormat.RGBA32, width, height, false);
        field["m_Width"].AsInt = width; field["m_Height"].AsInt = height; field["m_TextureFormat"].AsInt = (int)TextureFormat.RGBA32;
        field["m_CompleteImageSize"].AsInt = encoded.Length;
        if (!field["m_MipCount"].IsDummy) field["m_MipCount"].AsInt = 1;
        if (!field["m_MipMap"].IsDummy) field["m_MipMap"].AsBool = false;
        if (!field["m_StreamingMipmaps"].IsDummy) field["m_StreamingMipmaps"].AsBool = false;
        var stream = field["m_StreamData"];
        if (!stream.IsDummy) { stream["offset"].AsULong = 0; stream["size"].AsUInt = 0; stream["path"].AsString = ""; }
        var data = field["image data"]; data.Value.ValueType = AssetValueType.ByteArray; data.TemplateField.ValueType = AssetValueType.ByteArray; data.AsByteArray = encoded;
    }
    private static byte[] Png(byte[] pixels, int width, int height)
    {
        using var output = new MemoryStream(); new ImageWriter().WritePng(pixels, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output); return output.ToArray();
    }
    private static byte[] Raw(AssetsFileInstance file, AssetFileInfo info)
    {
        if (info.IsReplacerPreviewable) { using var stream = info.Replacer.GetPreviewStream(); using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
        file.file.Reader.Position = info.GetAbsoluteByteOffset(file.file); return file.file.Reader.ReadBytes(checked((int)info.ByteSize));
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    public ModderSaveResult Save(CancellationToken token = default)
    {
        var changed = documents.Where(d => d.Edits.Count > 0).ToList();
        if (changed.Count == 0) return new([], "");
        var prepared = new List<(Document Doc, string Temp)>();
        var committed = new List<(Document Doc, FileStream Original)>();
        try
        {
            foreach (var d in changed)
            {
                token.ThrowIfCancellationRequested();
                Load(d, token);
                if (Hash(d.Path) != d.Hash) throw new IOException($"{d.Name} changed outside Liquid. Reopen it before saving.");
                string temporary = d.Path + ".liquid-" + Guid.NewGuid().ToString("N") + ".tmp";
                prepared.Add((d, temporary));
                using (var writer = new AssetsFileWriter(temporary))
                {
                    if (d.Bundle is not null)
                    {
                        foreach (var member in d.Members) d.Bundle.file.BlockAndDirInfo.DirectoryInfos[d.Bundle.file.GetFileIndex(member.name)].SetNewData(member.file);
                        d.Bundle.file.Write(writer);
                    }
                    else d.Members[0].file.Write(writer);
                }
                if (d.Bundle is not null && d.Bundle.originalCompression != AssetBundleCompressionType.None)
                {
                    string packed = temporary + ".packed";
                    var bundle = new AssetBundleFile();
                    try
                    {
                        bundle.Read(new AssetsFileReader(temporary));
                        using (var writer = new AssetsFileWriter(packed)) bundle.Pack(writer, d.Bundle.originalCompression);
                        bundle.Close(); File.Move(packed, temporary, true);
                    }
                    finally { bundle.Close(); if (File.Exists(packed)) File.Delete(packed); }
                }
                ValidateWritten(d, temporary);
            }
            token.ThrowIfCancellationRequested();
            manager.UnloadAll(); // Release Windows file locks before atomic replacement.
            foreach (var d in documents) { d.Members.Clear(); d.Bundle = null; }
            foreach (var item in prepared)
            {
                if (Hash(item.Doc.Path) != item.Doc.Hash) throw new IOException($"{item.Doc.Name} changed while saving. No edits were overwritten.");
                var original = new FileStream(item.Doc.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try { File.Replace(item.Temp, item.Doc.Path, null); }
                catch { original.Dispose(); throw; }
                committed.Add((item.Doc, original));
            }
            foreach (var d in changed) { d.Edits.Clear(); d.Assets = null; d.Hash = null; }
            history.Clear();
            return new(changed.Select(d => d.Path).ToArray(), "");
        }
        catch
        {
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                string restore = item.Doc.Path + ".liquid-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    item.Original.Position = 0;
                    using (var output = File.Create(restore)) item.Original.CopyTo(output);
                    File.Replace(restore, item.Doc.Path, null);
                }
                finally { if (File.Exists(restore)) File.Delete(restore); }
            }
            throw;
        }
        finally
        {
            foreach (var item in committed) item.Original.Dispose();
            foreach (var item in prepared) if (File.Exists(item.Temp)) File.Delete(item.Temp);
        }
    }
    private static void ValidateWritten(Document d, string path)
    {
        var check = new AssetsManager();
        try
        {
            List<AssetsFileInstance> members = [];
            if (d.IsBundle)
            {
                var bundle = check.LoadBundleFile(path, true);
                if (bundle.file.BlockAndDirInfo.DirectoryInfos.Count != d.Bundle!.file.BlockAndDirInfo.DirectoryInfos.Count) throw new IOException("Bundle member count changed unexpectedly.");
                foreach (var source in d.Members) members.Add(check.LoadAssetsFileFromBundle(bundle, source.name));
            }
            else members.Add(check.LoadAssetsFile(path));
            for (int index = 0; index < members.Count; index++)
            {
                var before = d.Members[index].file.Metadata.AssetInfos; var after = members[index].file.Metadata.AssetInfos;
                if (before.Count != after.Count || !before.Select(a => (a.PathId, a.TypeId)).SequenceEqual(after.Select(a => (a.PathId, a.TypeId)))) throw new IOException("Written asset IDs/types do not match the original file.");
                foreach (var edit in d.Edits.Where(e => e.Key.StartsWith(index + ":", StringComparison.Ordinal)))
                {
                    long id = long.Parse(edit.Key[(edit.Key.IndexOf(':') + 1)..]);
                    if (!Raw(members[index], members[index].file.GetAssetInfo(id)).AsSpan().SequenceEqual(edit.Value)) throw new IOException("Written replacement differs from staged bytes.");
                }
            }
        }
        finally { check.UnloadAll(); }
    }
    public void Dispose() => manager.UnloadAll(true);
}

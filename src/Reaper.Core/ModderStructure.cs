using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Cpp2IL;
using StbImageSharp;

namespace Reaper.Core;

public sealed record StructureRow(string Name, string Type, string Path, string? Value, int Children,
    int? ReferenceType, string? VisualPath, int? VisualType, bool Scalar, bool Link);
public sealed record StructurePage(string FileId, string AssetId, string Path, StructureRow[] Rows, int Total);
public sealed record ReferenceChoice(string FileId, string AssetId, string Name, string File);

public sealed partial class AssetModder
{
    private readonly string schemaWorker;
    private string? schemaError;
    private readonly Dictionary<(string File, int Dependency, long Script), string?> scriptNames = [];

    private string? ScriptName(AssetsFileInstance file, AssetFileInfo info)
    {
        try
        {
            var pointer = manager.GetBaseField(file, info, AssetReadFlags.SkipMonoBehaviourFields)["m_Script"];
            var key = (file.path, pointer["m_FileID"].AsInt, pointer["m_PathID"].AsLong);
            if (scriptNames.TryGetValue(key, out var name)) return name;
            var ext = Follow(file, pointer);
            if (ext.file is not null && ext.info is not null)
                name = Field(ext.file, ext.info)["m_ClassName"].AsString;
            scriptNames[key] = name;
            return name;
        }
        catch { return null; }
    }
    private bool schemaProbed;
    private string? generatedSchemas;

    public void UseManagedSchemas(string directory)
    {
        if (!Directory.EnumerateFiles(directory, "*.dll").Any())
            throw new IOException("Choose a folder containing the game's managed assemblies.");
        if (manager.MonoTempGenerator is IDisposable previous) previous.Dispose();
        manager.MonoTempGenerator = new MonoCecilTempGenerator(directory);
        manager.UseMonoTemplateFieldCache = false;
        schemaProbed = true;
        schemaError = null;
    }

    private void FindSchemas(AssetsFileInstance file)
    {
        if (schemaProbed) return;
        schemaProbed = true;
        manager.UseMonoTemplateFieldCache = false;
        try
        {
            var directory = Path.GetDirectoryName(file.parentBundle?.path ?? file.path)!;
            var managed = new[] { Path.Combine(directory, "Managed"), Path.Combine(Root, "Managed") }
                .FirstOrDefault(p => Directory.Exists(p) && Directory.EnumerateFiles(p, "*.dll").Any());
            if (managed is not null) UseManagedSchemas(managed);
            else
            {
                var inputs = FindCpp2IlFiles.Find(directory);
                if (inputs.success)
                {
                    if (ProtectedMetadataProfiles.Matches(inputs.metaPath, inputs.asmPath))
                    {
                        generatedSchemas = Path.Combine(Path.GetTempPath(), "Liquid-schemas-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(generatedSchemas);
                        ModderSchemaRecovery.Extract(schemaWorker, inputs.asmPath, inputs.metaPath, generatedSchemas);
                        UseManagedSchemas(generatedSchemas);
                    }
                    else if (MetadataInspection.Read(inputs.metaPath).Valid)
                        manager.MonoTempGenerator = new Cpp2IlTempGenerator(inputs.metaPath, inputs.asmPath);
                    else schemaError = "This IL2CPP metadata format is unsupported by the bundled schema readers.";
                }
            }
        }
        catch (Exception ex) { schemaError = ex.Message; }
    }

    private AssetTypeValueField StructureField(AssetsFileInstance file, AssetFileInfo info)
    {
        if (info.TypeId == 114 && !file.file.Metadata.TypeTreeEnabled) FindSchemas(file);
        var field = Field(file, info);
        // Reject partial schemas before allowing changes. Unknown bytes must survive an edit.
        if (!Raw(file, info).AsSpan().SequenceEqual(field.WriteToByteArray(file.file.Header.Endianness)))
            throw new InvalidDataException("The complete asset schema is unavailable or does not round-trip. Load matching managed assemblies using Script schemas."
                + (schemaError is null ? "" : " " + schemaError));
        return field;
    }

    private static bool Pointer(AssetTypeValueField field) =>
        field.TemplateField.Type.StartsWith("PPtr<", StringComparison.Ordinal)
        && !field["m_FileID"].IsDummy && !field["m_PathID"].IsDummy;

    private AssetExternal Follow(AssetsFileInstance file, AssetTypeValueField pointer)
    {
        if (pointer["m_PathID"].AsLong == 0) return default;
        return manager.GetExtAsset(file, pointer, true);
    }

    private static int? VisualType(AssetTypeValueField field) => field.TemplateField.Type switch
    {
        "PPtr<Sprite>" or "PPtr<$Sprite>" => 213,
        "PPtr<Texture2D>" or "PPtr<$Texture2D>" or "PPtr<Texture>" or "PPtr<$Texture>" => 28,
        "PPtr<Mesh>" or "PPtr<$Mesh>" => 43,
        _ => null
    };

    // A path identifies child indices. @ follows a serialized object reference into its owner.
    private (AssetsFileInstance File, AssetFileInfo Info, AssetTypeValueField Root, AssetTypeValueField Node)
        Locate(string fileId, string assetId, string path)
    {
        var (_, file, info) = Resolve(fileId, assetId);
        var root = StructureField(file, info);
        var node = root;
        foreach (var step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (step == "@")
            {
                if (!Pointer(node)) throw new InvalidDataException("Expected an object reference.");
                var ext = Follow(file, node);
                file = ext.file ?? throw new IOException("Referenced file is unavailable.");
                info = ext.info ?? throw new IOException("Referenced asset is unavailable.");
                root = node = StructureField(file, info);
            }
            else if (int.TryParse(step, out int index) && index >= 0 && index < node.Children.Count) node = node.Children[index];
            else throw new InvalidDataException("Invalid field path.");
        }
        return (file, info, root, node);
    }

    public StructurePage ReadStructure(string fileId, string assetId, string path, int offset)
    {
        var (file, _, _, node) = Locate(fileId, assetId, path);
        var rows = node.Children.Skip(offset).Take(60).Select((child, index) =>
        {
            string childPath = path + "/" + (offset + index);
            int budget = 12;
            var visual = FirstVisual(file, child, childPath, 0, ref budget);
            bool pointer = Pointer(child);
            bool scalar = !pointer && child.Children.Count == 0 && child.Value is { ValueType: not (AssetValueType.Array or AssetValueType.ByteArray or AssetValueType.None) };
            var name = node.TemplateField.IsArray ? $"[{offset + index}]" : child.TemplateField.Name;
            var named = child.Children.FirstOrDefault(f => f.Value?.ValueType == AssetValueType.String && (f.TemplateField.Name.Contains("name", StringComparison.OrdinalIgnoreCase) || f.TemplateField.Name == "helper"));
            if (named is not null && named.AsString.Length > 0) name += "  " + named.AsString;
            var representation = scalar ? AssetDumpCodec.Read(child) : null;
            if (representation?.Encoding is not null) scalar = false;
            string? value = scalar ? representation?.Value : null;
            if (pointer)
            {
                var ext = Follow(file, child);
                value = ext.info is not null && ext.file is not null
                    ? ReferenceNameWithSchema(ext.file, ext.info)
                    : child["m_PathID"].AsLong == 0 ? "None" : $"Path ID {child["m_PathID"].AsLong}";
            }
            return new StructureRow(name, child.TemplateField.Type, childPath, value, pointer ? 0 : child.Children.Count,
                VisualType(child), visual.Path, visual.Type, scalar, pointer && child["m_PathID"].AsLong != 0);
        }).ToArray();
        return new(fileId, assetId, path, rows, node.Children.Count);
    }

    private string ReferenceNameWithSchema(AssetsFileInstance file, AssetFileInfo info)
    {
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        return ReferenceName(file, info);
    }

    private string ReferenceName(AssetsFileInstance file, AssetFileInfo info)
    {
        if (info.IsReplacerPreviewable)
        {
            var name = Field(file, info)["m_Name"];
            if (!name.IsDummy) return name.AsString;
        }
        return AssetHelper.GetAssetNameFast(file.file, manager.ClassDatabase, info);
    }

    private (string? Path, int? Type) FirstVisual(AssetsFileInstance file, AssetTypeValueField node, string path, int depth, ref int budget)
    {
        if (VisualType(node) is int type && node["m_PathID"].AsLong != 0) return (path, type);
        if (depth >= 9) return default;
        if (Pointer(node))
        {
            if (budget <= 0 || node.TemplateField.Name is "m_GameObject" or "m_Father" or "m_Script") return default;
            budget--;
            try
            {
                var ext = Follow(file, node);
                if (ext.file is not null && ext.info?.TypeId is 1 or 4 or 21 or 33 or 74 or 114 or 212 or 224)
                    return FirstVisual(ext.file, Field(ext.file, ext.info), path + "/@", depth + 1, ref budget);
            }
            catch { /* A missing dependency does not hide the rest of the item. */ }
            return default;
        }
        for (int i = 0; i < Math.Min(node.Children.Count, 64); i++)
        {
            var found = FirstVisual(file, node.Children[i], path + "/" + i, depth + 1, ref budget);
            if (found.Path is not null) return found;
        }
        return default;
    }

    public string? StructureThumbnail(string fileId, string assetId, string path)
    {
        var (file, _, _, node) = Locate(fileId, assetId, path);
        if (!Pointer(node)) return null;
        var ext = Follow(file, node);
        if (ext.file is null || ext.info is null) return null;
        var field = Field(ext.file, ext.info);
        if (ext.info.TypeId == 43) return MeshThumbnail.Render(field, ext.file.file.Header.Endianness);
        if (ext.info.TypeId is not (28 or 213)) return null;
        return EncodeThumbnail(GetImage(ext.file, ext.info, field), 64);
    }

    public string? AssetThumbnail(string fileId, string assetId)
    {
        var (_, file, info) = Resolve(fileId, assetId);
        if (info.TypeId is not (28 or 213)) return null;
        return EncodeThumbnail(GetImage(file, info, Field(file, info)), 40);
    }

    private static string EncodeThumbnail((byte[] Pixels, int Width, int Height) image, int maxSide)
    {
        double factor = Math.Min(1d, (double)maxSide / Math.Max(image.Width, image.Height));
        int width = Math.Max(1, (int)(image.Width * factor)), height = Math.Max(1, (int)(image.Height * factor));
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.Pixels.AsSpan(((int)(y / factor) * image.Width + (int)(x / factor)) * 4, 4)
                    .CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
        return "data:image/png;base64," + Convert.ToBase64String(Png(pixels, width, height));
    }

    public void SetStructureValue(string fileId, string assetId, string path, string value)
    {
        var (file, info, root, node) = Locate(fileId, assetId, path);
        if (node.Value is null || node.Children.Count != 0 || node.Value.ValueType is AssetValueType.ByteArray or AssetValueType.Array)
            throw new InvalidDataException("Select a scalar field.");
        var original = AssetDumpCodec.Read(node);
        if (original.Encoding is not null) throw new InvalidDataException("This field contains non-text bytes.");
        AssetDumpCodec.Apply(node, original with { Value = value });
        StageStructure(file, info, root);
    }

    private void StageStructure(AssetsFileInstance file, AssetFileInfo info, AssetTypeValueField root, Action? revert = null)
    {
        var document = Owner(file);
        SetEdit(document, document.Members.IndexOf(file) + ":" + info.PathId, info, root.WriteToByteArray(file.file.Header.Endianness), revert);
    }

    public ReferenceChoice[] FindReferences(int classId, string query)
    {
        if (classId is not (28 or 43 or 213)) return [];
        var matches = new List<ReferenceChoice>();
        foreach (var document in documents.ToArray())
        {
            Load(document, default);
            for (int member = 0; member < document.Members.Count; member++)
            {
                var file = document.Members[member];
                manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
                foreach (var info in file.file.GetAssetsOfType(classId))
                {
                    string name = ReferenceName(file, info);
                    if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !info.PathId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    matches.Add(new(document.Id, member + ":" + info.PathId, name, document.Name));
                    if (matches.Count == 100) return matches.ToArray();
                }
            }
        }
        return matches.ToArray();
    }

    public void ReplaceReference(string fileId, string assetId, string path, string targetFile, string targetAsset)
    {
        var (source, info, root, pointer) = Locate(fileId, assetId, path);
        var (_, target, targetInfo) = Resolve(targetFile, targetAsset);
        if (VisualType(pointer) != targetInfo.TypeId) throw new InvalidDataException("Replacement asset has the wrong Unity type.");
        var revert = SetPointer(source, pointer, target, targetInfo.PathId);
        try { StageStructure(source, info, root, revert); }
        catch { revert?.Invoke(); throw; }
    }

    public void ImportReferenceSprite(string fileId, string assetId, string path, string name, byte[] png)
    {
        var (source, info, root, pointer) = Locate(fileId, assetId, path);
        if (VisualType(pointer) != 213) throw new InvalidDataException("Select a Sprite reference.");
        var template = Follow(source, pointer);
        if (template.file is null || template.info?.TypeId != 213)
            throw new InvalidDataException("Assign an existing sprite as a template first.");

        var sprite = StructureField(template.file, template.info);
        var target = ResolveSpriteTexture(template.file, sprite);
        var spriteData = sprite["m_RD"];
        if (!ReferenceEquals(spriteData, target.RenderData)
            || !spriteData["alphaTexture"].IsDummy && spriteData["alphaTexture"]["m_PathID"].AsLong != 0)
            throw new NotSupportedException("Choose a sprite template with a direct texture and no external alpha.");
        var image = Texture(target.File, target.Info, target.Field);
        var rect = SpriteRect(spriteData, image.Width, image.Height);
        int rotation = Rotation(spriteData);
        int width = rotation == 4 ? rect.H : rect.W, height = rotation == 4 ? rect.W : rect.H;
        var imported = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
        if (imported.Width != width || imported.Height != height)
            throw new InvalidDataException($"Custom image must match the template size ({width} × {height}).");
        string assetName = Path.GetFileNameWithoutExtension(name).Trim();
        if (assetName.Length is 0 or > 128) throw new InvalidDataException("Choose a sprite name of 1–128 characters.");

        // Keep the template mesh, UVs and packing. The custom sprite gets a private texture.
        Array.Clear(image.Pixels);
        WriteSpritePixels(imported, image.Pixels, image.Width, rect, rotation);
        target.Field["m_Name"].AsString = assetName + " Texture";
        ReplaceTexture(target.Field, image.Pixels, image.Width, image.Height);
        sprite["m_Name"].AsString = assetName;
        var tags = sprite["m_AtlasTags"];
        if (!tags.IsDummy && !tags["Array"].IsDummy)
        {
            var tagArray = tags["Array"];
            tagArray.AsArray = new AssetTypeArrayInfo(0);
            tagArray.Children.Clear();
        }
        var atlas = sprite["m_SpriteAtlas"];
        if (!atlas.IsDummy)
        {
            atlas["m_FileID"].AsInt = 0;
            atlas["m_PathID"].AsLong = 0;
        }
        var rollback = new Stack<Action>();
        try
        {
            long textureId = AddClone(target.File, target.Info, target.Field, rollback);
            var texturePointer = spriteData["texture"]["m_PathID"];
            if (texturePointer.Value.ValueType == AssetValueType.Int32) texturePointer.AsInt = checked((int)textureId);
            else texturePointer.AsLong = textureId;
            long spriteId = AddClone(template.file, template.info, sprite, rollback);
            var undoPointer = SetPointer(source, pointer, template.file, spriteId);
            if (undoPointer is not null) rollback.Push(undoPointer);
            StageStructure(source, info, root, Revert);
        }
        catch { Revert(); throw; }

        void Revert() { while (rollback.TryPop(out var action)) action(); }
    }

    private long AddClone(AssetsFileInstance file, AssetFileInfo original, AssetTypeValueField field, Stack<Action> rollback)
    {
        var owner = Owner(file);
        int member = owner.Members.IndexOf(file);
        long id = 1;
        while (file.file.GetAssetInfo(id) is not null) id++;
        var info = new AssetFileInfo
        {
            PathId = id, TypeId = original.TypeId, TypeIdOrIndex = original.TypeIdOrIndex,
            OldTypeId = original.OldTypeId, ScriptTypeIndex = original.ScriptTypeIndex
        };
        byte[] data = field.WriteToByteArray(file.file.Header.Endianness);
        info.ByteSize = checked((uint)data.Length);
        info.SetNewData(data);
        file.file.Metadata.AddAssetInfo(info);
        string key = member + ":" + id;
        owner.AddedAssets.Add((member, info));
        owner.Edits.Add(key, data);
        owner.Assets = null;
        rollback.Push(() =>
        {
            owner.AddedAssets.Remove((member, info));
            owner.Edits.Remove(key);
            owner.Assets = null;
            if (owner.Members.Count > member) owner.Members[member].file.Metadata.RemoveAssetInfo(info);
        });
        return id;
    }

    private Action? SetPointer(AssetsFileInstance source, AssetTypeValueField pointer, AssetsFileInstance target, long pathId)
    {
        var pathField = pointer["m_PathID"];
        if (pathField.Value.ValueType == AssetValueType.Int32 && pathId is < int.MinValue or > int.MaxValue)
            throw new InvalidDataException("This Unity file uses 32-bit path IDs.");
        int index = 0;
        Action? revert = null;
        if (!ReferenceEquals(source, target))
        {
            var existing = source.file.Metadata.Externals;
            int i = existing.FindIndex(e => string.Equals(Path.GetFileName(e.PathName), Path.GetFileName(target.name), StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                if (!ReferenceEquals(source.parentBundle, target.parentBundle)
                    || source.parentBundle is null && !string.Equals(Path.GetDirectoryName(source.path), Path.GetDirectoryName(target.path), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The replacement must be in the same Unity bundle or game data directory.");
                var owner = Owner(source);
                string dependency = target.parentBundle is null ? target.name : "archive:/" + target.name;
                var external = new AssetsFileExternal { PathName = dependency, OriginalPathName = dependency };
                int member = owner.Members.IndexOf(source);
                owner.NewExternals.Add((member, external));
                existing.Add(external);
                revert = () =>
                {
                    owner.NewExternals.Remove((member, external));
                    if (owner.Members.Count > member) owner.Members[member].file.Metadata.Externals.Remove(external);
                };
                i = existing.Count - 1;
            }
            index = i + 1;
        }
        pointer["m_FileID"].AsInt = index;
        if (pointer["m_PathID"].Value.ValueType == AssetValueType.Int32) pointer["m_PathID"].AsInt = checked((int)pathId);
        else pointer["m_PathID"].AsLong = pathId;
        return revert;
    }
}

using System.Text.Json;
using Reaper.Core;

namespace Reaper.App;

public sealed partial class MainForm
{
    private AssetModder? modder;
    private readonly SemaphoreSlim editorGate = new(1, 1);
    private bool shuttingDown;
    private sealed record EditorTextDraft(string FileId, string AssetId, string Text);
    private readonly Dictionary<string, EditorTextDraft> editorDrafts = [];
    private bool HasEditorChanges => modder?.Modified == true || editorDrafts.Count > 0;
    private ModderFile[] EditorFiles() => modder!.Files.Select(f => f with { Changes = f.Changes + editorDrafts.Values.Count(d => d.FileId == f.Id && !modder.IsAssetModified(d.FileId, d.AssetId)) }).ToArray();
    private object[] EditorDraftSnapshot() => editorDrafts.Values.Select(d => (object)new { fileId = d.FileId, assetId = d.AssetId, text = d.Text }).ToArray();
    private void SetEditorDrafts(JsonElement message)
    {
        if (!message.TryGetProperty("drafts", out var drafts)) return;
        var updated = new Dictionary<string, EditorTextDraft>();
        foreach (var draft in drafts.EnumerateArray())
        {
            string file = draft.GetProperty("fileId").GetString()!, asset = draft.GetProperty("assetId").GetString()!, text = draft.GetProperty("text").GetString()!;
            if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("TextAsset exceeds the inline editor limit.");
            updated[file + ":" + asset] = new(file, asset, text);
        }
        editorDrafts.Clear(); foreach (var pair in updated) editorDrafts.Add(pair.Key, pair.Value);
    }
    private ModderSaveResult SaveEditorChanges()
    {
        foreach (var pair in editorDrafts.ToArray()) { modder!.ImportText(pair.Value.FileId, pair.Value.AssetId, pair.Value.Text); editorDrafts.Remove(pair.Key); }
        return modder!.Save();
    }

    private async Task HandleEditorMessage(JsonElement message)
    {
        if (shuttingDown) return;
        string action = message.GetProperty("type").GetString()!;
        int requestId = message.TryGetProperty("requestId", out var id) ? id.GetInt32() : 0;
        await editorGate.WaitAsync();
        try
        {
            if (shuttingDown) return;
            string Value(string key) => message.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
            string fileId = Value("fileId"), assetId = Value("assetId");
            if (modder is not null) SetEditorDrafts(message);
            switch (action)
            {
                case "editorOpen":
                    string input = Value("path");
                    if (input.Length == 0)
                    {
                        bool pickFile = message.TryGetProperty("file", out var f) && f.GetBoolean();
                        if (pickFile)
                        {
                            using var picker = new OpenFileDialog { Title = "Open Unity data", Filter = "Unity data|*.unity3d;*.assets;*.bundle;sharedassets*|All files|*.*" };
                            if (picker.ShowDialog(this) != DialogResult.OK) { Send(new { type = "editorIdle" }); return; }
                            input = picker.FileName;
                        }
                        else
                        {
                            using var picker = new FolderBrowserDialog { Description = "Open game folder", UseDescriptionForTitle = true };
                            if (picker.ShowDialog(this) != DialogResult.OK) { Send(new { type = "editorIdle" }); return; }
                            input = picker.SelectedPath;
                        }
                    }
                    if (HasEditorChanges)
                    {
                        var answer = MessageBox.Show(this, "Save your asset changes before opening another game?", "Liquid", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (answer == DialogResult.Cancel) { Send(new { type = "editorIdle" }); return; }
                        if (answer == DialogResult.Yes) await Task.Run(SaveEditorChanges);
                    }
                    string classData = Path.Combine(AppContext.BaseDirectory, "engines", "Modder", "classdata.tpk");
                    var next = await Task.Run(() => new AssetModder(input, classData));
                    modder?.Dispose(); modder = next; editorDrafts.Clear();
                    Send(new { type = "editorOpened", root = modder.Root, files = EditorFiles() });
                    break;
                case "editorAssets":
                    RequireEditor();
                    int offset = message.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
                    var assets = await Task.Run(() => modder!.GetAssets(fileId, Value("query"), Value("assetType"), offset));
                    Send(new { type = "editorAssets", requestId, value = assets, files = EditorFiles() });
                    break;
                case "editorPreview":
                    RequireEditor();
                    var preview = await Task.Run(() => modder!.Preview(fileId, assetId));
                    Send(new { type = "editorPreview", requestId, value = preview });
                    break;
                case "editorExport":
                    RequireEditor();
                    string format = Value("format");
                    string extension = await Task.Run(() => modder!.ExportExtension(fileId, assetId, format));
                    string safe = string.Concat(Value("name").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    using (var picker = new SaveFileDialog { Title = "Export asset", FileName = safe + "." + extension, Filter = $"Asset file|*.{extension}|All files|*.*", OverwritePrompt = true })
                    {
                        if (picker.ShowDialog(this) != DialogResult.OK) { Send(new { type = "editorIdle" }); return; }
                        var content = await Task.Run(() => modder!.Export(fileId, assetId, format));
                        await File.WriteAllBytesAsync(picker.FileName, content);
                        Send(new { type = "editorExported", path = picker.FileName });
                    }
                    break;
                case "editorImport":
                    RequireEditor();
                    using (var picker = new OpenFileDialog { Title = "Replace asset", Filter = Value("format") == "dump" ? "Liquid JSON dump|*.json|All files|*.*" : Value("format") == "raw" ? "Raw asset|*.bin;*.dat|All files|*.*" : Value("assetType") is "Texture2D" or "Sprite" ? "Image|*.png;*.bmp;*.jpg;*.jpeg;*.tga|All files|*.*" : "TextAsset|*.txt;*.json;*.bytes;*.xml;*.csv|All files|*.*" })
                    {
                        if (picker.ShowDialog(this) != DialogResult.OK) { Send(new { type = "editorIdle" }); return; }
                        if (new FileInfo(picker.FileName).Length > 512L * 1024 * 1024) throw new IOException("Replacement exceeds 512 MB.");
                        var bytes = await File.ReadAllBytesAsync(picker.FileName);
                        await Task.Run(() => modder!.Import(fileId, assetId, Value("format"), bytes));
                        Send(new { type = "editorChanged", files = EditorFiles() });
                    }
                    break;
                case "editorDrafts":
                    RequireEditor(); Send(new { type = "editorDraftsChanged", files = EditorFiles() }); break;
                case "editorUndo":
                    RequireEditor();
                    if (editorDrafts.Count > 0) editorDrafts.Remove(editorDrafts.Keys.Last()); else await Task.Run(() => modder!.Undo());
                    Send(new { type = "editorChanged", files = EditorFiles(), drafts = EditorDraftSnapshot() }); break;
                case "editorDiscard":
                    RequireEditor(); editorDrafts.Clear(); modder!.Discard(); Send(new { type = "editorChanged", files = EditorFiles() }); break;
                case "editorSave":
                    RequireEditor();
                    var saved = await Task.Run(SaveEditorChanges);
                    Send(new { type = "editorSaved", value = saved, files = EditorFiles() });
                    break;
            }
        }
        catch (Exception ex) { Send(new { type = "editorError", requestId, message = ex.Message }); }
        finally { editorGate.Release(); }
    }
    private void RequireEditor() { if (modder is null) throw new InvalidOperationException("Open a game folder first."); }
}

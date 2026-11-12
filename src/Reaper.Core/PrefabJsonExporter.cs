using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reaper.Core;

internal static class PrefabJsonExporter
{
    public static async Task WriteAsync(HttpClient http, AssetEntry root, List<AssetEntry> inventory, Stream output, CancellationToken token)
    {
        var local = inventory.Where(e => e.Collection == root.Collection).GroupBy(e => e.PathId).ToDictionary(g => g.Key, g => g.First());
        var pending = new Queue<AssetEntry>(); pending.Enqueue(root);
        var seen = new HashSet<string>(); var objects = new JsonArray();
        while (pending.TryDequeue(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(entry.ViewUrl)) continue;
            if (seen.Count > 10000) throw new IOException("GameObject hierarchy exceeds 10000 objects. Export its serialized file instead.");
            string json = await http.GetStringAsync(entry.ViewUrl.Replace("/Assets/View?", "/Assets/Json?", StringComparison.Ordinal), token);
            var data = JsonNode.Parse(json) ?? throw new IOException("Missing GameObject data.");
            objects.Add(new JsonObject { ["Class"] = entry.ClassName, ["Name"] = entry.Name, ["PathID"] = entry.PathId, ["Data"] = data });
            if (entry.ClassName == "GameObject")
            {
                if ((data["m_Components"] ?? data["m_Component"]) is JsonArray components)
                    foreach (var component in components) Follow(component?["m_Component"] ?? component?["component"] ?? component?["second"] ?? component);
            }
            else if (entry.ClassName is "Transform" or "RectTransform")
            {
                if (data["m_Children"] is JsonArray children) foreach (var child in children) Follow(child);
                if (entry.ViewUrl != root.ViewUrl) Follow(data["m_GameObject"]);
            }
        }
        var document = new JsonObject { ["Format"] = "Liquid.GameObjectHierarchy/1", ["Collection"] = root.Collection, ["RootPathID"] = root.PathId, ["Objects"] = objects };
        await JsonSerializer.SerializeAsync(output, document, JsonFiles.Options, token);
        void Follow(JsonNode? pointer)
        {
            if (pointer is not JsonObject p || p["m_FileID"]?.GetValue<int>() != 0 || p["m_PathID"] is null) return;
            string id = p["m_PathID"]!.ToJsonString();
            if (local.TryGetValue(id, out var target)) pending.Enqueue(target);
        }
    }
}

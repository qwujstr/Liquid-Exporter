using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Reaper.Core;

public sealed record AssetEntry(string ClassName, string Name, string ViewUrl, string Collection, string PathId);
public sealed record ExportedAsset(string ClassName, string Name, string Collection, string PathId, string? File, string? Error);
public sealed record AssetExportReport(string[] SelectedTypes, int Matched, int Exported, List<ExportedAsset> Assets, List<string> Warnings);

/// <summary>Reads the bundled engine's actual Unity class inventory; downloads only selected objects.
/// The 2.0.0 engine exposes collection tables and per-object image/text endpoints, not a JSON listing API.</summary>
public static class SpecificAssetExporter
{
    public static readonly string[] SupportedTypes = ["Texture2D", "Sprite", "TextAsset", "AudioClip", "GameObject", "Mesh", "Material", "Resources", "MonoScript"];
    public static bool IsResource(AssetEntry entry) => entry.Collection.Equals("resources.assets", StringComparison.OrdinalIgnoreCase) || entry.Collection.StartsWith("resources.assets/", StringComparison.OrdinalIgnoreCase);
    public static Dictionary<string, int> CountTypes(List<AssetEntry> entries)
    {
        var counts = SupportedTypes.ToDictionary(t => t, t => entries.Count(e => e.ClassName == t));
        counts["Resources"] = entries.Count(IsResource);
        return counts;
    }
    private static readonly Regex Links = new("<a\\b[^>]*\\bhref=\"(?<url>[^\"]+)\"[^>]*>(?<name>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Rows = new("<tr\\b[^>]*\\bdata-class=\"(?<type>[^\"]+)\"[^>]*>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));

    public static async Task<List<AssetEntry>> ReadInventoryAsync(HttpClient http, CancellationToken token)
    {
        var pending = new Queue<string>();
        pending.Enqueue("/Bundles/View?Path=" + Uri.EscapeDataString("{\"P\":[]}"));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var seenAssets = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AssetEntry>();
        while (pending.TryDequeue(out string? route))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(route)) continue;
            string html = await http.GetStringAsync(route, token);
            if (route.StartsWith("/Collections/View?", StringComparison.Ordinal))
            {
                string collection = Text(Regex.Match(html, "<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value);
                if (new[] { "unity default resources", "unity_default_resources", "unity editor resources", "unity builtin extra", "unity_builtin_extra" }.Contains(collection.ToLowerInvariant())) continue;
                foreach (Match row in Rows.Matches(html))
                {
                    string type = WebUtility.HtmlDecode(row.Groups["type"].Value);
                    Match link = Links.Match(row.Groups["row"].Value);
                    string url = WebUtility.HtmlDecode(link.Groups["url"].Value);
                    if (!url.StartsWith("/Assets/View?Path=", StringComparison.Ordinal) || !seenAssets.Add(url)) continue;
                    string id = Text(Regex.Match(row.Groups["row"].Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value);
                    result.Add(new AssetEntry(type, Text(link.Groups["name"].Value), url, collection, id));
                }
            }
            else
            {
                foreach (Match link in Links.Matches(html))
                {
                    string url = WebUtility.HtmlDecode(link.Groups["url"].Value);
                    if (url.StartsWith("/Bundles/View?Path=", StringComparison.Ordinal) || url.StartsWith("/Collections/View?Path=", StringComparison.Ordinal))
                        pending.Enqueue(url);
                }
            }
        }
        return result;
    }

    public static async Task<AssetExportReport> ExportAsync(HttpClient http, string output, string[] types, Action<string> log, CancellationToken token)
    {
        Directory.CreateDirectory(output);
        var inventory = await ReadInventoryAsync(http, token);
        var entries = inventory.Where(a => types.Contains(a.ClassName) || (types.Contains("Resources") && IsResource(a))).ToList();
        var files = new List<ExportedAsset>();
        var warnings = new List<string>();
        var report = new AssetExportReport(types.Distinct().ToArray(), entries.Count, 0, files, warnings);
        string reportPath = Path.Combine(output, "assets-report.json");
        JsonFiles.Write(reportPath, report);
        foreach (string type in types.Distinct())
            if (!entries.Any(a => type == "Resources" ? IsResource(a) : a.ClassName == type)) warnings.Add($"No {type} objects were found in the input files.");
        int done = 0, exported = 0;
        var sprites = new SpritePngExporter();
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.ViewUrl)))[..12].ToLowerInvariant();
                string extension = entry.ClassName switch { "Texture2D" or "Sprite" => ".png", "TextAsset" => ".txt", "Mesh" => ".glb", "Material" => ".mat.yaml", "GameObject" => ".prefab.json", "AudioClip" => ".audio", _ => ".json" };
                string folder = types.Contains(entry.ClassName) ? entry.ClassName : Path.Combine("Resources", SafeName(entry.ClassName));
                string relative = Path.Combine(folder, SafeName(entry.Collection), SafeName(entry.Name) + "_" + hash + extension);
                string destination = Path.Combine(output, relative);
                try
                {
                    string query = entry.ViewUrl[entry.ViewUrl.IndexOf('?')..];
                    string route = entry.ClassName switch
                    {
                        "TextAsset" => "/Assets/Text" + query,
                        "Texture2D" or "Sprite" => "/Assets/Image" + query + "&Extension=png",
                        "AudioClip" => "/Assets/Audio" + query,
                        "Mesh" => "/Assets/Model.glb" + query,
                        "Material" => "/Assets/Yaml" + query,
                        _ => "/Assets/Json" + query
                    };
                    using var response = entry.ClassName is "Sprite" or "GameObject" ? null : await http.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, token);
                    response?.EnsureSuccessStatusCode();
                    if (entry.ClassName == "Texture2D" && response?.Content.Headers.ContentType?.MediaType != "image/png")
                        throw new IOException("Engine returned no decoded PNG image.");
                    if (entry.ClassName == "AudioClip")
                    {
                        string audioType = response?.Content.Headers.ContentType?.MediaType ?? "";
                        extension = audioType switch { "audio/ogg" => ".ogg", "audio/wav" or "audio/x-wav" => ".wav", "audio/mp3" or "audio/mpeg" => ".mp3", "audio/fsb" => ".fsb", _ => throw new IOException("The engine returned an unsupported audio format: " + audioType) };
                        relative = Path.ChangeExtension(relative, extension); destination = Path.Combine(output, relative);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    // Atomic file publication keeps cancelled downloads out of the finished file list.
                    string temporary = destination + ".partial";
                    try
                    {
                        await using (var stream = File.Create(temporary))
                        {
                            if (entry.ClassName == "Sprite") await sprites.WriteAsync(http, query, stream, token);
                            else if (entry.ClassName == "GameObject") await PrefabJsonExporter.WriteAsync(http, entry, inventory, stream, token);
                            else await response!.Content.CopyToAsync(stream, token);
                        }
                        File.Move(temporary, destination, false);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    files.Add(new(entry.ClassName, entry.Name, entry.Collection, entry.PathId, relative.Replace('\\', '/'), null));
                    exported++;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    files.Add(new(entry.ClassName, entry.Name, entry.Collection, entry.PathId, null, ex.Message));
                    log($"Could not export {entry.ClassName} {entry.Name}: {ex.Message}");
                }
                done++;
                log($"ExportProgress : ({done}/{entries.Count}) {entry.ClassName} · {entry.Name}");
            }
            if (exported < entries.Count) warnings.Add($"{entries.Count - exported} selected assets could not be decoded. See Assets/assets-report.json.");
            if (exported == 0) throw new IOException("No selected assets could be exported. Check the chosen types, source files and assets-report.json.");
            return report with { Exported = exported };
        }
        finally { JsonFiles.Write(reportPath, report with { Exported = exported }); }
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).Take(80).ToArray());
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0 || name is "." or "..") name = "Unnamed";
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) name = "_" + name;
        return name;
    }
}

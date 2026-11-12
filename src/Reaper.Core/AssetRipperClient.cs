using System.Net;
using System.Net.Sockets;

namespace Reaper.Core;

public static class AssetRipperClient
{
    public static Task ExportAsync(ExportRequest request, string workPath, Action<string> log,
        Action<string> stage, CancellationToken token) => RunAsync(request, workPath, log, stage, async http =>
        {
            if (request.Mode == ExportMode.Assets)
            {
                stage("Exporting selected assets");
                var result = await SpecificAssetExporter.ExportAsync(http, Path.Combine(workPath, "selected-assets"), request.AssetTypes, log, token);
                foreach (string warning in result.Warnings) log("Asset warning: " + warning);
            }
            else
            {
                stage("Exporting Unity project");
                string engineOutput = Path.Combine(workPath, "export");
                if (Directory.Exists(engineOutput)) throw new IOException("Temporary export folder already exists.");
                await PostAsync(http, "/Export/UnityProject", new Dictionary<string, string> { ["Path"] = engineOutput }, token);
            }
        }, token);

    public static async Task<Dictionary<string, int>> ScanAsync(ExportRequest request, string workPath, CancellationToken token)
    {
        Dictionary<string, int> result = [];
        await RunAsync(request, workPath, _ => { }, _ => { }, async http =>
        {
            var entries = await SpecificAssetExporter.ReadInventoryAsync(http, token);
            JsonFiles.Write(Path.Combine(workPath, "asset-inventory.json"), entries);
            result = SpecificAssetExporter.CountTypes(entries);
        }, token);
        return result;
    }

    private static async Task RunAsync(ExportRequest request, string workPath, Action<string> log,
        Action<string> stage, Func<HttpClient, Task> export, CancellationToken token)
    {
        // A separate headless engine per job keeps loaded assets/settings isolated.
        int port;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Directory.CreateDirectory(workPath);
        string inputPath = request.GamePath;
        if (request.SkipLightingData)
        {
            stage("Filtering lighting data");
            inputPath = await LightingFilter.StageAsync(request.GamePath, Path.Combine(workPath, "filtered-input"), log, token);
        }
        await using var engine = new ChildProcess(request.AssetRipperPath,
            ["--headless", "--port", port.ToString(), "--log-path", Path.Combine(workPath, "project-engine.log")],
            Path.GetDirectoryName(request.AssetRipperPath)!, line => { string? clean = CleanEngineLine(line); if (clean is not null) log(clean); });
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };

        stage("Starting export engine");
        using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            startup.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (engine.HasExited) throw new IOException($"Export engine exited during startup (code {engine.ExitCode}). See the log.");
                try
                {
                    using var response = await http.GetAsync("/", startup.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("Export engine did not start within 90 seconds."); }
                try { await Task.Delay(250, startup.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("Export engine did not start within 90 seconds."); }
            }
        }

        // Keep script schemas for MonoBehaviour reading and preserve generated script GUIDs.
        await Post("/Settings/Update", new Dictionary<string, string>
        {
            ["ScriptContentLevel"] = request.Mode == ExportMode.Assets ? "Level0" : "Level2",
            ["ScriptExportMode"] = "Decompiled",
            ["ScriptLanguageVersion"] = "AutoSafe",
            ["IgnoreStreamingAssets"] = request.IncludeStreamingAssets ? "" : "true",
            ["StreamingAssetsMode"] = request.IncludeStreamingAssets ? "Extract" : "Ignore",
            ["ImageExportFormat"] = "Png",
            ["SpriteExportMode"] = "Yaml",
            ["ShaderExportMode"] = "Dummy",
            ["PreferOriginalTextureExtension"] = "true"
        }.Where(p => p.Key != "IgnoreStreamingAssets" || !request.IncludeStreamingAssets));

        stage("Reading Unity objects");
        await Post(Directory.Exists(inputPath) ? "/LoadFolder" : "/LoadFile",
            new Dictionary<string, string> { ["Path"] = inputPath });

        await export(http);

        Task Post(string route, IEnumerable<KeyValuePair<string, string>> form) => PostAsync(http, route, form, token);
    }

    private static async Task PostAsync(HttpClient http, string route, IEnumerable<KeyValuePair<string, string>> form, CancellationToken token)
    {
            using var response = await http.PostAsync(route, new FormUrlEncodedContent(form), token);
            if ((int)response.StatusCode >= 400)
            {
                string body = await response.Content.ReadAsStringAsync(token);
                throw new IOException($"Project engine {route}: HTTP {(int)response.StatusCode}. {body[..Math.Min(body.Length, 1500)]}");
            }
            if (response.StatusCode is not HttpStatusCode.Found && !response.IsSuccessStatusCode)
                throw new IOException($"Unexpected engine response: {response.StatusCode}");
    }

    public static string? CleanEngineLine(string line)
    {
        string value = line.Trim();
        if (value.StartsWith("[stderr] ", StringComparison.Ordinal)) value = value[9..].Trim();
        string[] categories = ["Import", "Export", "ExportProgress", "Processing", "Warning", "Error"];
        int separator = value.IndexOf(" :", StringComparison.Ordinal);
        if (separator >= 0 && categories.Contains(value[..separator]))
            return value.Replace("AssetRipper", "Project engine", StringComparison.OrdinalIgnoreCase);
        if (value.Contains("exception", StringComparison.OrdinalIgnoreCase) || value.StartsWith("fail:", StringComparison.OrdinalIgnoreCase))
            return value.Replace("AssetRipper", "Project engine", StringComparison.OrdinalIgnoreCase);
        return null;
    }

    public static void CollectProject(string engineOutput, string destination)
    {
        string source = Path.Combine(engineOutput, "ExportedProject");
        if (!Directory.Exists(Path.Combine(source, "Assets"))) source = engineOutput;
        if (!Directory.Exists(Path.Combine(source, "Assets")))
            throw new IOException("Engine did not create Assets. Check the input files and log.");
        if (!Directory.Exists(Path.Combine(source, "ProjectSettings")))
            throw new IOException("Engine did not create ProjectSettings; export is incomplete.");
        if (Directory.Exists(destination)) throw new IOException("UnityProject folder already exists.");
        Directory.CreateDirectory(destination);
        foreach (string name in new[] { "Assets", "Packages", "ProjectSettings" })
        {
            string path = Path.Combine(source, name);
            if (Directory.Exists(path)) Directory.Move(path, Path.Combine(destination, name));
        }
    }
}

namespace Reaper.Core;

public sealed class ExportPipeline
{
    public async Task<JobReport> RunAsync(ExportRequest request, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        request = InputValidation.ResolveInputs(request);
        InputValidation.Validate(request);
        string run = Path.Combine(Path.GetFullPath(request.OutputDirectory), $"Liquid_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(run);
        var report = new JobReport { Request = request, OutputPath = run };
        string reportPath = Path.Combine(run, "export-report.json");
        using var writer = new StreamWriter(Path.Combine(run, "liquid.log")) { AutoFlush = true };
        object logLock = new();
        int percent = 0;
        long lastPublished = 0;
        int lastPublishedPercent = -1;
        string lastPublishedStage = "";
        void Log(string message)
        {
            lock (logLock)
            {
                if (message.StartsWith("@liquid-progress:", StringComparison.Ordinal))
                {
                    int separator = message.IndexOf('|');
                    if (separator > 17 && int.TryParse(message.AsSpan(17, separator - 17), out int workerPercent))
                    {
                        percent = Math.Max(percent, request.NeedsAssetEngine ? 5 + Math.Clamp(workerPercent, 0, 100) / 2 : 5 + Math.Clamp(workerPercent, 0, 100) * 90 / 100);
                        message = message[(separator + 1)..];
                    }
                }
                var match = System.Text.RegularExpressions.Regex.Match(message, @"ExportProgress : \((\d+)/(\d+)\)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int current) && int.TryParse(match.Groups[2].Value, out int total) && total > 0)
                    percent = Math.Max(percent, 75 + (int)(19L * current / total));
                if (message.Contains("Could not initialize assembly manager", StringComparison.Ordinal))
                    report.Warnings.Add("The game's script metadata could not be read. Project resources were exported without recovered game script bodies.");
                if (message.Contains("Error during reading of asset type", StringComparison.Ordinal) && !report.Warnings.Any(w => w.StartsWith("Some Unity objects", StringComparison.Ordinal)))
                    report.Warnings.Add("Some Unity objects could not be read by the export engine. See liquid.log for affected types.");
                writer.WriteLine($"{DateTimeOffset.Now:O} {message}");
                long now = Environment.TickCount64;
                if (report.Stage != lastPublishedStage || percent != lastPublishedPercent || now - lastPublished >= 150)
                {
                    progress?.Report(new ProgressUpdate(report.Stage, message, percent));
                    lastPublished = now;
                    lastPublishedPercent = percent;
                    lastPublishedStage = report.Stage;
                }
            }
        }
        void Stage(string value, int valuePercent)
        {
            report.Stage = value;
            percent = Math.Max(percent, valuePercent);
            JsonFiles.Write(reportPath, report);
            Log(value);
        }
        JsonFiles.Write(reportPath, report);
        try
        {
            token.ThrowIfCancellationRequested();
            if (request.NeedsScripts)
            {
                Stage("Exporting scripts", 10);
                string scriptOutput = Path.Combine(run, "Scripts");
                Directory.CreateDirectory(scriptOutput);
                string workerRequest = Path.Combine(run, "worker-request.json");
                JsonFiles.Write(workerRequest, new WorkerRequest(request.BinaryPath, request.MetadataPath, scriptOutput,
                    request.RecoverSimpleMethods, request.IncludeEngineAssemblies, request.GenerateAnalysisFiles,
                    request.CodeRegistration, request.MetadataRegistration, request.ForceBinaryVersion, request.ExperimentalRecovery));
                await using var worker = new ChildProcess(request.WorkerPath, ["--request", workerRequest], run, Log);
                int result = await worker.WaitAsync(token);
                if (result != 0) throw new IOException($"IL2CPP export failed (code {result}). See liquid.log.");
                var scripts = JsonFiles.Read<ScriptReport>(Path.Combine(scriptOutput, "script-report.json"));
                report.Warnings.AddRange(scripts.Warnings);
                if (scripts.UnrecoveredMethods > 0)
                    report.Warnings.Add($"{scripts.UnrecoveredMethods} methods remain unsupported; {scripts.PartialMethods} are partial; {scripts.RecoveredMethods} were instruction-mapped by supported native passes.");
                report.CompletedStages.Add("scripts");
                Log($"C# files: {scripts.SourceFiles}. Recovered methods: {scripts.RecoveredMethods}.");
            }
            if (request.NeedsAssetEngine)
            {
                Stage(request.Mode == ExportMode.Assets ? "Selected assets" : "Unity project", request.NeedsScripts ? 55 : 10);
                string work = Path.Combine(run, "EngineData");
                await AssetRipperClient.ExportAsync(request, work, Log, value => Stage(value, value switch
                {
                    "Filtering lighting data" => request.NeedsScripts ? 56 : 12,
                    "Starting export engine" => request.NeedsScripts ? 60 : 20,
                    "Reading Unity objects" => request.NeedsScripts ? 65 : 35,
                    "Exporting Unity project" or "Exporting selected assets" => 75,
                    _ => percent
                }), token);
                token.ThrowIfCancellationRequested();
                if (request.Mode == ExportMode.Assets)
                {
                    string assets = Path.Combine(work, "selected-assets");
                    var selected = JsonFiles.Read<AssetExportReport>(Path.Combine(assets, "assets-report.json"));
                    report.Warnings.AddRange(selected.Warnings);
                    Directory.Move(assets, Path.Combine(run, "Assets"));
                    report.CompletedStages.Add("assets");
                }
                else
                {
                    AssetRipperClient.CollectProject(Path.Combine(work, "export"), Path.Combine(run, "UnityProject"));
                    report.CompletedStages.Add("project");
                }
                if (request.NeedsScripts && request.NeedsProject) report.Warnings.Add("UnityProject preserves the export engine's script GUIDs. Independent C# is in Scripts/Source; integrate recovered logic manually to preserve scene references.");
                Stage(request.Mode == ExportMode.Assets ? "Selected assets exported" : "Unity project exported", 95);
            }
            report.Status = report.Warnings.Count > 0 ? "completed-with-warnings" : "completed";
            Stage("Complete", 100);
            File.WriteAllText(Path.Combine(run, "READ-ME.txt"),
                "LIQUID\n\nUnityProject: essential Unity folders (when project export is selected).\n" +
                "Assets: selected Unity asset categories, decoded media and structured data (Specific Asset Only mode).\n" +
                "Scripts/Source: C# by type; Scripts/Assemblies: assemblies with recovered simple methods and explicit stubs.\n" +
                "Scripts/script-report.json: method status. NotSupportedException means the body was not recovered.\n" +
                "EngineData: engine logs and supporting files, separate from UnityProject.\n\n" + string.Join("\n", report.Warnings));
            return report;
        }
        catch (OperationCanceledException)
        {
            report.Status = "cancelled";
            report.Error = "Export cancelled. Partial output was preserved.";
            Log(report.Error);
            throw;
        }
        catch (Exception ex)
        {
            report.Status = "failed";
            report.Error = ex.Message;
            Log(ex.ToString());
            throw new IOException($"{ex.Message}\nPartial output and log: {run}", ex);
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.Now;
            JsonFiles.Write(reportPath, report);
        }
    }
}

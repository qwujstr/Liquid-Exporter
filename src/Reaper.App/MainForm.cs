using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Reaper.Core;

namespace Reaper.App;

public sealed partial class MainForm : Form
{
    private readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private readonly string dataDirectory;
    private readonly string? testDirectory;
    private readonly HashSet<string> knownOutputs = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
    private Task? runningJob;
    private CancellationTokenSource? scanCancellation;
    private Task? scanTask;
    private bool closing;
    private ExportRequest savedRequest = new();

    public MainForm(string[] args)
    {
        Text = "Liquid";
        Size = new Size(1280, 900);
        MinimumSize = new Size(960, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        Font = new Font("Arial", 9F, FontStyle.Bold);
        FormBorderStyle = FormBorderStyle.None;
        MaximizedBounds = Screen.FromControl(this).WorkingArea;
        AutoScaleMode = AutoScaleMode.Dpi;
        testDirectory = args.Length == 2 && args[0] == "--ui-test" ? Path.GetFullPath(args[1]) : null;
        dataDirectory = testDirectory is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Liquid")
            : Path.Combine(testDirectory, "profile");
        Directory.CreateDirectory(dataDirectory);
        string previousSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Il2cppLiquid", "settings.json");
        string currentSettings = Path.Combine(dataDirectory, "settings.json");
        if (testDirectory is null && !File.Exists(currentSettings) && File.Exists(previousSettings)) File.Copy(previousSettings, currentSettings);
        Controls.Add(view);
        var titleBar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.Black };
        void Drag(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } }
        titleBar.MouseDown += Drag;
        void ToggleMax() { MaximizedBounds = Screen.FromControl(this).WorkingArea; WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
        titleBar.DoubleClick += (_, _) => ToggleMax();
        foreach (var action in new (string Symbol, Action Click)[] { ("−", () => WindowState = FormWindowState.Minimized), ("□", ToggleMax), ("×", () => Close()) })
        {
            var button = new WindowButton(action.Symbol) { Dock = DockStyle.Right, Width = 62, TabStop = false };
            button.Click += (_, _) => action.Click();
            titleBar.Controls.Add(button);
        }
        Controls.Add(titleBar);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "ui", "assets", "app.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += OnClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        int rounded = 2; // DWMWCP_ROUND: compositor antialiased window corners on Windows 11.
        DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var value = base.CreateParams;
            value.Style = (value.Style & ~0x00C00000) | 0x00040000 | 0x00020000 | 0x00010000 | 0x00080000;
            return value;
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowStyle(IntPtr window, int index);
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x83) { m.Result = IntPtr.Zero; return; } // WM_NCCALCSIZE, both RECT forms
        // DefWindowProc paints a classic caption on activation when a frameless window
        // retains the resize/system-menu styles. The entire frame is our client area.
        if (m.Msg == 0x86) { m.Result = (IntPtr)1; return; } // WM_NCACTIVATE
        if (m.Msg == 0x85) { m.Result = IntPtr.Zero; return; } // WM_NCPAINT
        if (m.Msg is 0xAE or 0xAF) { m.Result = IntPtr.Zero; return; } // undocumented NC theme drawing
        base.WndProc(ref m);
        if (m.Msg != 0x84 || WindowState != FormWindowState.Normal) return;
        int raw = unchecked((int)m.LParam.ToInt64());
        var p = PointToClient(new Point((short)(raw & 0xffff), (short)(raw >> 16)));
        bool left = p.X < 6, right = p.X >= Width - 6, top = p.Y < 6, bottom = p.Y >= Height - 6;
        if (top || bottom || left || right) m.Result = (IntPtr)(top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : 11);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDirectory, "WebView"));
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.SetVirtualHostNameToFolderMapping("reaper.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            view.CoreWebView2.Settings.AreDevToolsEnabled = testDirectory is not null;
            view.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            view.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            view.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            view.CoreWebView2.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith("https://reaper.local/", StringComparison.Ordinal);
            view.CoreWebView2.WebMessageReceived += OnMessage;
            view.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                Send(new { type = "init", request = savedRequest, engines = EngineStatus(), version = "1.0.0" });
                if (testDirectory is not null) await RunUiTest();
            };
            string settings = Path.Combine(dataDirectory, "settings.json");
            if (File.Exists(settings))
            {
                try { savedRequest = JsonFiles.Read<ExportRequest>(settings); }
                catch (Exception) { /* An invalid saved preference must not prevent startup. */ }
            }
            savedRequest = savedRequest with
            {
                AssetRipperPath = FindAssetRipper(savedRequest.AssetRipperPath),
                WorkerPath = Path.Combine(AppContext.BaseDirectory, "worker", "Reaper.Worker.exe")
            };
            view.Source = new Uri("https://reaper.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Unable to start the interface. Microsoft Edge WebView2 Runtime is required.\n\n" + ex.Message,
                "Liquid", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private object EngineStatus() => new { assetRipper = File.Exists(savedRequest.AssetRipperPath), worker = File.Exists(savedRequest.WorkerPath) };
    private static string FindAssetRipper(string previous)
    {
        if (File.Exists(previous)) return previous;
        foreach (string root in new[] { AppContext.BaseDirectory, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")) })
        {
            string path = Path.Combine(root, "engines", "AssetRipper", "AssetRipper.GUI.Free.exe");
            if (File.Exists(path)) return path;
        }
        return previous;
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith("https://reaper.local/", StringComparison.Ordinal)) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            string? type = message.GetProperty("type").GetString();
            if (type?.StartsWith("editor", StringComparison.Ordinal) == true) { await HandleEditorMessage(message); return; }
            switch (type)
            {
                case "drop":
                    if (runningJob is { IsCompleted: false }) return;
                    string dropped = e.AdditionalObjects?.OfType<CoreWebView2File>().FirstOrDefault()?.Path ?? "";
                    if (message.TryGetProperty("scope", out var scope) && scope.GetString() == "editor") { await HandleEditorMessage(JsonSerializer.SerializeToElement(new { type = "editorOpen", path = dropped })); break; }
                    if (Directory.Exists(dropped)) Send(new { type = "path", field = "GamePath", path = dropped });
                    else if (File.Exists(dropped))
                    {
                        string field = Path.GetFileName(dropped).Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase) ? "MetadataPath"
                            : Path.GetExtension(dropped).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(dropped).Equals(".so", StringComparison.OrdinalIgnoreCase) ? "BinaryPath" : "GamePath";
                        Send(new { type = "path", field, path = dropped });
                    }
                    else throw new ArgumentException("Unable to read the dropped path. Use the Folder button.");
                    break;
                case "language":
                    string language = message.GetProperty("language").GetString() ?? "en";
                    savedRequest = savedRequest with { Language = language is "en" or "ru" or "de" or "es" or "uk" or "zh" or "fr" ? language : "en" };
                    JsonFiles.Write(Path.Combine(dataDirectory, "settings.json"), savedRequest);
                    break;
                case "inspect":
                    string metadataPath = message.GetProperty("path").GetString() ?? "";
                    string binaryPath = message.TryGetProperty("binary", out var nativePath) ? nativePath.GetString() ?? "" : "";
                    var inspection = await Task.Run(() => MetadataInspection.Read(metadataPath, binaryPath));
                    Send(new { type = "inspection", path = metadataPath, value = inspection });
                    break;
                case "browse":
                    if (runningJob is { IsCompleted: false }) return;
                    Browse(message.GetProperty("field").GetString()!, message.TryGetProperty("file", out var file) && file.GetBoolean());
                    break;
                case "detect":
                    string game = message.GetProperty("path").GetString() ?? "";
                    var detected = await Task.Run(() => InputValidation.DetectIl2Cpp(game));
                    var gameIcon = await Task.Run(() => GameIcon.Read(game));
                    Send(new { type = "detected", path = game, binary = detected.Binary, metadata = detected.Metadata, icon = gameIcon });
                    break;
                case "gameIcon":
                    string iconGame = message.GetProperty("path").GetString() ?? "";
                    var icon = await Task.Run(() => GameIcon.Read(iconGame));
                    Send(new { type = "gameIcon", path = iconGame, icon });
                    break;
                case "scanAssets":
                    if (runningJob is { IsCompleted: false }) return;
                    scanCancellation?.Cancel();
                    var scanRequest = message.GetProperty("request").Deserialize<ExportRequest>(JsonFiles.Options) ?? throw new ArgumentException("Empty asset settings.");
                    scanRequest = scanRequest with { Mode = ExportMode.Assets, AssetRipperPath = FindAssetRipper(scanRequest.AssetRipperPath) };
                    var scanSource = new CancellationTokenSource();
                    scanCancellation = scanSource;
                    var previousScan = scanTask;
                    scanTask = ScanAssets(scanRequest, message.GetProperty("id").GetInt32(), scanSource, previousScan);
                    break;
                case "cancelAssetScan": scanCancellation?.Cancel(); break;
                case "start":
                    if (runningJob is { IsCompleted: false }) return;
                    var request = message.GetProperty("request").Deserialize<ExportRequest>(JsonFiles.Options)
                        ?? throw new ArgumentException("Empty settings.");
                    request = request with { WorkerPath = savedRequest.WorkerPath, AssetRipperPath = FindAssetRipper(request.AssetRipperPath) };
                    request = InputValidation.ResolveInputs(request);
                    InputValidation.Validate(request);
                    savedRequest = request;
                    JsonFiles.Write(Path.Combine(dataDirectory, "settings.json"), savedRequest);
                    scanCancellation?.Cancel();
                    if (scanTask is not null) await scanTask;
                    cancellation = new CancellationTokenSource();
                    Send(new { type = "started" });
                    runningJob = RunExport(request, cancellation.Token);
                    break;
                case "cancel": cancellation?.Cancel(); break;
                case "open":
                    string path = Path.GetFullPath(message.GetProperty("path").GetString() ?? "");
                    if (knownOutputs.Contains(path) && Directory.Exists(path))
                        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex) { Send(new { type = "error", message = ex.Message }); }
    }

    private async Task ScanAssets(ExportRequest request, int id, CancellationTokenSource source, Task? previousScan)
    {
        string work = Path.Combine(dataDirectory, "AssetScans", Guid.NewGuid().ToString("N"));
        try
        {
            if (previousScan is not null) await previousScan;
            source.Token.ThrowIfCancellationRequested();
            if (!Directory.Exists(request.GamePath) && !File.Exists(request.GamePath)) throw new ArgumentException("Game folder or file not found.");
            if (!File.Exists(request.AssetRipperPath)) throw new ArgumentException("Project export engine is missing.");
            var counts = await Task.Run(() => AssetRipperClient.ScanAsync(request, work, source.Token));
            source.Token.ThrowIfCancellationRequested();
            Send(new { type = "assetInventory", id, counts });
        }
        catch (OperationCanceledException) { Send(new { type = "assetInventory", id, cancelled = true }); }
        catch (Exception ex) { Send(new { type = "assetInventory", id, error = ex.Message }); }
        finally
        {
            if (ReferenceEquals(scanCancellation, source)) scanCancellation = null;
            source.Dispose();
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Browse(string field, bool file)
    {
        if (field is not ("GamePath" or "BinaryPath" or "MetadataPath" or "OutputDirectory" or "AssetRipperPath")) return;
        string? path = null;
        if (file || field is "BinaryPath" or "MetadataPath" or "AssetRipperPath")
        {
            using var dialog = new OpenFileDialog { Title = "Select a file", CheckFileExists = true,
                Filter = field == "MetadataPath" ? "IL2CPP metadata|global-metadata.dat|All files|*.*" : "All files|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK) path = dialog.FileName;
        }
        else
        {
            using var dialog = new FolderBrowserDialog { Description = field == "OutputDirectory" ? "Export output folder" : "Unity game folder", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) path = dialog.SelectedPath;
        }
        if (path is not null) Send(new { type = "path", field, path });
    }

    private async Task RunExport(ExportRequest request, CancellationToken token)
    {
        var progress = new Progress<ProgressUpdate>(value => Send(new { type = "progress", value }));
        try
        {
            JobReport report = await Task.Run(() => new ExportPipeline().RunAsync(request, progress, token));
            knownOutputs.Add(report.OutputPath);
            Send(new { type = "completed", report });
        }
        catch (OperationCanceledException) { Send(new { type = "cancelled" }); }
        catch (Exception ex) { Send(new { type = "failed", message = ex.Message }); }
        finally { cancellation?.Dispose(); cancellation = null; }
    }

    private void Send(object value)
    {
        if (!IsDisposed && !Disposing && view.CoreWebView2 is not null)
            view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, JsonFiles.Options));
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (shuttingDown) return;
        shuttingDown = true;
        await editorGate.WaitAsync();
        try
        {
            string draftSnapshot = view.CoreWebView2 is not null ? await view.CoreWebView2.ExecuteScriptAsync("({drafts:window.liquidEditor?.drafts()??[]})") : "{}";
            using var draftDocument = JsonDocument.Parse(draftSnapshot);
            SetEditorDrafts(draftDocument.RootElement);
            if (HasEditorChanges)
            {
                var answer = MessageBox.Show(this, "Save your asset changes before closing?", "Liquid", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) { shuttingDown = false; return; }
                if (answer == DialogResult.Yes) await Task.Run(SaveEditorChanges);
            }
            cancellation?.Cancel(); scanCancellation?.Cancel();
            if (runningJob is not null) await runningJob;
            if (scanTask is not null) await scanTask;
            modder?.Dispose(); modder = null;
            closing = true;
            Close();
        }
        catch (Exception ex) { shuttingDown = false; MessageBox.Show(this, ex.Message, "Liquid", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { editorGate.Release(); }
    }

    private async Task RunUiTest()
    {
        try
        {
            Directory.CreateDirectory(testDirectory!);
            await Task.Delay(500);
            for (int n = 0; n < 20; n++)
            {
                SendMessage(Handle, 0x86, IntPtr.Zero, IntPtr.Zero);
                SendMessage(Handle, 0x85, (IntPtr)1, IntPtr.Zero);
                SendMessage(Handle, 0x86, (IntPtr)1, IntPtr.Zero);
            }
            var clientOrigin = PointToScreen(Point.Empty);
            JsonFiles.Write(Path.Combine(testDirectory!, "frame-check.json"), new
            {
                CaptionStyleAbsent = (GetWindowStyle(Handle, -16) & 0x00C00000) == 0,
                NonClientActivationHandled = SendMessage(Handle, 0x86, (IntPtr)1, IntPtr.Zero) == (IntPtr)1,
                ClientCoversFrame = clientOrigin == Location && ClientSize == Size,
                ActivationCycles = 20
            });
            if (!string.IsNullOrWhiteSpace(savedRequest.GamePath))
                Send(new { type = "gameIcon", path = savedRequest.GamePath, icon = await Task.Run(() => GameIcon.Read(savedRequest.GamePath)) });
            await Task.Delay(100);
            if (savedRequest.Mode == ExportMode.Assets)
            {
                for (int retry = 0; retry < 100; retry++)
                {
                    string state = await view.CoreWebView2.ExecuteScriptAsync("window.reaper.assetState().scanning");
                    if (state == "false") break;
                    await Task.Delay(300);
                }
                File.WriteAllText(Path.Combine(testDirectory!, "asset-selection-check.json"), await view.CoreWebView2.ExecuteScriptAsync("window.reaper.assetSelectionCheck()"));
                File.WriteAllText(Path.Combine(testDirectory!, "asset-ui-check.json"), await view.CoreWebView2.ExecuteScriptAsync("window.reaper.assetState()"));
            }
            await Capture("main.png");
            Size normalSize = Size;
            Size = MinimumSize;
            await Task.Delay(200);
            await Capture("main-small.png");
            File.WriteAllText(Path.Combine(testDirectory!, "small-layout-check.json"), await view.CoreWebView2.ExecuteScriptAsync("({overflow:document.documentElement.scrollWidth>innerWidth,assetOptionsInWindow:document.querySelector('#asset-options').getBoundingClientRect().right<=innerWidth,specificAssetRightOfProject:document.querySelector('[data-mode=Assets]').getBoundingClientRect().x>document.querySelector('[data-mode=Project]').getBoundingClientRect().x})"));
            Size = normalSize;
            await Task.Delay(100);
            await view.CoreWebView2.ExecuteScriptAsync("window.reaper.animationCheck().then(r=>window.liquidAnimationResult=r)");
            await Task.Delay(1800);
            File.WriteAllText(Path.Combine(testDirectory!, "animation-check.json"), await view.CoreWebView2.ExecuteScriptAsync("window.liquidAnimationResult"));
            await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]}");
            await view.CoreWebView2.ExecuteScriptAsync("window.reaper.reducedMotionCheck().then(r=>window.liquidReducedMotionResult=r)");
            await Task.Delay(100);
            File.WriteAllText(Path.Combine(testDirectory!, "reduced-motion-check.json"), await view.CoreWebView2.ExecuteScriptAsync("window.liquidReducedMotionResult"));
            await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[]}");
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-page=export]').click()");
            await Task.Delay(350);
            using (var frame = new Bitmap(Width, Height))
            {
                DrawToBitmap(frame, new Rectangle(Point.Empty, Size));
                frame.Save(Path.Combine(testDirectory!, "window-frame.png"));
            }
            File.WriteAllText(Path.Combine(testDirectory!, "mode-check.json"), await view.CoreWebView2.ExecuteScriptAsync("window.reaper.modeCheck()"));
            string checks = await view.CoreWebView2.ExecuteScriptAsync("window.reaper.uiCheck()");
            File.WriteAllText(Path.Combine(testDirectory!, "ui-check.json"), checks);
            await view.CoreWebView2.ExecuteScriptAsync("window.reaper.receive({type:'started'});window.reaper.receive({type:'progress',value:{Stage:'Exporting scripts',Message:'Loading screen test',Percent:38}})");
            await Task.Delay(1200);
            await Capture("loading.png");
            string video = await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({readyState:document.querySelector('#loading-video').readyState,currentTime:document.querySelector('#loading-video').currentTime,error:document.querySelector('#loading-video').error?.message,playing:!document.querySelector('#loading-video').paused})");
            File.WriteAllText(Path.Combine(testDirectory!, "video-check.json"), video);
            string before = await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('.loading-center').getBoundingClientRect().y");
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#toggle-log').click();window.reaper.receive({type:'progress',value:{Stage:'Exporting scripts',Message:'Progress must stay monotonic',Percent:12}})");
            await Task.Delay(350);
            await Capture("loading-log.png");
            string logCheck = await view.CoreWebView2.ExecuteScriptAsync($"JSON.stringify({{movedUp:document.querySelector('.loading-center').getBoundingClientRect().y<{before},logVisible:!document.querySelector('#live-log').hidden,progress:document.querySelector('.progress-track').getAttribute('aria-valuenow'),logWithinWindow:document.querySelector('#live-log').getBoundingClientRect().bottom<=innerHeight+1}})");
            File.WriteAllText(Path.Combine(testDirectory!, "log-check.json"), logCheck);
            await view.CoreWebView2.ExecuteScriptAsync("window.reaper.receive({type:'completed',report:{Status:'completed-with-warnings',OutputPath:'C:\\\\Exports\\\\Liquid_demo',Warnings:['Some methods were not recovered.'],CompletedStages:['scripts','project']}})");
            await Task.Delay(250);
            await Capture("complete.png");
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
            await Task.Delay(400);
            await Capture("settings.png");
            var persistedLanguages = new Dictionary<string, string>();
            foreach (string language in new[] { "ru", "de", "es", "uk", "zh", "fr", "en" })
            {
                await view.CoreWebView2.ExecuteScriptAsync($"document.querySelector('#Language').value='{language}';document.querySelector('#Language').dispatchEvent(new Event('change'))");
                await Task.Delay(80);
                persistedLanguages[language] = savedRequest.Language;
                await Capture($"settings-{language}.png");
            }
            JsonFiles.Write(Path.Combine(testDirectory!, "language-persistence-check.json"), persistedLanguages);
            string editorInput = Path.Combine(testDirectory!, "editor-input.txt");
            if (File.Exists(editorInput))
            {
                await view.CoreWebView2.ExecuteScriptAsync("page('editor',false)");
                await Task.Delay(450);
                File.WriteAllText(Path.Combine(testDirectory!, "editor-empty-check.json"), await view.CoreWebView2.ExecuteScriptAsync("({...window.liquidEditor.state(), centered:getComputedStyle(document.querySelector('#editor-preview-message')).textAlign==='center',title:document.querySelector('[data-page=editor] span').textContent})"));
                await Capture("asset-editor-empty.png");
                string input = File.ReadAllText(editorInput).Trim();
                await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.open(" + JsonSerializer.Serialize(input) + ")");
                for (int retry = 0; retry < 150; retry++)
                {
                    await Task.Delay(200);
                    string ready = await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state().busy");
                    if (ready == "false") break;
                }
                string preferredFile = Path.Combine(testDirectory!, "editor-preferred-file.txt");
                if (File.Exists(preferredFile))
                {
                    await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.chooseFile(" + JsonSerializer.Serialize(File.ReadAllText(preferredFile).Trim()) + ")");
                    for (int retry = 0; retry < 100; retry++) { await Task.Delay(150); if (await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state().busy") == "false") break; }
                    await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.filter('Texture2D')");
                    for (int retry = 0; retry < 100; retry++) { await Task.Delay(150); if (await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state().busy") == "false") break; }
                }
                await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.select('Texture2D')");
                await Task.Delay(900);
                await Capture("asset-editor.png");
                File.WriteAllText(Path.Combine(testDirectory!, "editor-ui-check.json"), await view.CoreWebView2.ExecuteScriptAsync("({...window.liquidEditor.check(),...window.liquidEditor.state()})"));
                await view.CoreWebView2.ExecuteScriptAsync("(() => {const wrap=document.querySelector('#editor-image-wrap'),r=wrap.getBoundingClientRect(),s=window.liquidEditor.state();wrap.dispatchEvent(new WheelEvent('wheel',{deltaY:-150,clientX:r.left+r.width/2,clientY:r.top+r.height/2,bubbles:true,cancelable:true}));const after=window.liquidEditor.state();window.previewZoomResult={before:s.zoom,after:after.zoom,beforePanX:after.panX,beforePanY:after.panY};})()");
                using (var bounds = JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("(() => {const r=document.querySelector('#editor-image-wrap').getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()")))
                {
                    double x = bounds.RootElement.GetProperty("x").GetDouble(), y = bounds.RootElement.GetProperty("y").GetDouble();
                    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 }));
                    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = x + 35, y = y + 20, button = "left", buttons = 1 }));
                    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x = x + 35, y = y + 20, button = "left", buttons = 0, clickCount = 1 }));
                }
                File.WriteAllText(Path.Combine(testDirectory!, "editor-zoom-check.json"), await view.CoreWebView2.ExecuteScriptAsync("({...window.previewZoomResult,...window.liquidEditor.state()})"));
                await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editor-image-wrap').dispatchEvent(new KeyboardEvent('keydown',{key:'f',bubbles:true}))");
                File.WriteAllText(Path.Combine(testDirectory!, "editor-fit-check.json"), await view.CoreWebView2.ExecuteScriptAsync("(() => {const r=document.querySelector('#editor-image-wrap').getBoundingClientRect(),img=document.querySelector('#editor-image'),s=window.liquidEditor.state();return {...s,viewportVisible:r.bottom<=innerHeight,imageFits:img.naturalWidth*s.zoom<=r.width&&img.naturalHeight*s.zoom<=r.height};})()"));
                await Capture("asset-editor-fit.png");
                File.WriteAllText(Path.Combine(testDirectory!, "editor-polish-check.json"), await view.CoreWebView2.ExecuteScriptAsync("(() => {const img=document.querySelector('#editor-image'),frame=document.querySelector('#editor-image-bounds'),r=img.getBoundingClientRect(),f=frame.getBoundingClientRect();return {nearest:getComputedStyle(img).imageRendering==='pixelated',noZoomControls:!document.querySelector('#editor-image-tools,#editor-image-fit,#editor-image-reset,#editor-image-zoom'),boundsMatch:Math.abs(r.width-f.width)<.1&&Math.abs(r.height-f.height)<.1&&Math.abs(r.left-f.left)<.1&&Math.abs(r.top-f.top)<.1,outline:getComputedStyle(frame).outlineColor,openFolder:document.querySelector('#editor-open').textContent.trim()==='Open Folder',fileIcon:!!document.querySelector('#editor-open-file svg use'),arialEverywhere:[...document.querySelectorAll('body *:not(svg):not(svg *)')].every(e=>getComputedStyle(e).fontFamily.startsWith('Arial')&&getComputedStyle(e).fontWeight==='700')};})()"));
                await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editor-image-wrap').dispatchEvent(new KeyboardEvent('keydown',{key:'0',bubbles:true}))");
                await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.filter('TextAsset')");
                for (int retry = 0; retry < 100; retry++) { await Task.Delay(150); if (await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state().busy") == "false") break; }
                await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.select('TextAsset')");
                await Task.Delay(700);
                File.WriteAllText(Path.Combine(testDirectory!, "editor-text-check.json"), await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state()"));
                await Capture("asset-editor-text.png");
                await view.CoreWebView2.ExecuteScriptAsync("window.editorWidthBefore=document.querySelector('.editor-preview').getBoundingClientRect().width");
                using (var splitBounds = JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("(() => {const r=document.querySelector('#editor-divider').getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+80};})()")))
                {
                    double x = splitBounds.RootElement.GetProperty("x").GetDouble(), y = splitBounds.RootElement.GetProperty("y").GetDouble();
                    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 }));
                    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = x + 100, y, button = "left", buttons = 1 }));
                    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x = x + 100, y, button = "left", buttons = 0, clickCount = 1 }));
                }
                File.WriteAllText(Path.Combine(testDirectory!, "editor-resize-check.json"), await view.CoreWebView2.ExecuteScriptAsync("(() => {const width=document.querySelector('.editor-preview').getBoundingClientRect().width,layout=document.querySelector('#editor-layout'),before=layout.clientHeight;document.querySelector('#editor-height-divider').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true}));return {widthIncreased:width>window.editorWidthBefore+80,heightIncreased:layout.clientHeight>before,noPagination:!document.querySelector('.editor-pagination'),exportsLabel:document.querySelector('[data-page=results] span').textContent==='Exports',...window.liquidEditor.check()};})()"));
                await Capture("asset-editor-resized.png");
                if (File.Exists(Path.Combine(testDirectory!, "editor-write-test.txt")))
                {
                    await view.CoreWebView2.ExecuteScriptAsync("window.inlineOriginal=document.querySelector('#editor-text-edit').value;document.querySelector('#editor-text-edit').value+='\\nTemporary draft';document.querySelector('#editor-text-edit').dispatchEvent(new Event('input'))");
                    await Task.Delay(200);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editor-undo').click()");
                    for (int retry = 0; retry < 100; retry++) { await Task.Delay(150); if (await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state().busy") == "false") break; }
                    await Task.Delay(200);
                    File.WriteAllText(Path.Combine(testDirectory!, "editor-text-undo-check.json"), await view.CoreWebView2.ExecuteScriptAsync("({restored:document.querySelector('#editor-text-edit').value===window.inlineOriginal,drafts:window.liquidEditor.drafts().length})"));
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editor-text-edit').value+='\\nLiquid inline editor test — Україна 中文 Français';document.querySelector('#editor-text-edit').dispatchEvent(new Event('input'))");
                    await Task.Delay(200);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editor-save').click()");
                    for (int retry = 0; retry < 150; retry++) { await Task.Delay(200); if (await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state().busy") == "false") break; }
                    await Task.Delay(300);
                    File.WriteAllText(Path.Combine(testDirectory!, "editor-text-save-check.json"), await view.CoreWebView2.ExecuteScriptAsync("({...window.liquidEditor.state(),savedText:document.querySelector('#editor-text-edit').value.includes('Liquid inline editor test — Україна 中文 Français'),saveDisabled:document.querySelector('#editor-save').disabled})"));
                    await Capture("asset-editor-saved.png");
                }
                await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.filter('Texture2D')");
                for (int retry = 0; retry < 100; retry++) { await Task.Delay(150); if (await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state().busy") == "false") break; }
                await view.CoreWebView2.ExecuteScriptAsync("const scroll=document.querySelector('#editor-scroll');scroll.scrollTop=scroll.scrollHeight");
                await Task.Delay(200);
                File.WriteAllText(Path.Combine(testDirectory!, "editor-all-assets-check.json"), await view.CoreWebView2.ExecuteScriptAsync("({...window.liquidEditor.state(),renderedRows:document.querySelectorAll('#editor-rows tr:not(.editor-spacer)').length,lastRow:Number(document.querySelector('#editor-rows tr:not(.editor-spacer):last-of-type')?.getAttribute('aria-rowindex')||[...document.querySelectorAll('#editor-rows tr:not(.editor-spacer)')].at(-1)?.getAttribute('aria-rowindex')),scrollTop:document.querySelector('#editor-scroll').scrollTop})"));
                await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editor-search').value='message';document.querySelector('#editor-search').dispatchEvent(new Event('input'))");
                await Task.Delay(500);
                await Capture("asset-editor-search.png");
                File.WriteAllText(Path.Combine(testDirectory!, "editor-search-check.json"), await view.CoreWebView2.ExecuteScriptAsync("window.liquidEditor.state()"));
            }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(testDirectory!, "ui-error.txt"), ex.ToString()); }
        finally { Close(); }

        async Task Capture(string name)
        {
            await using var stream = File.Create(Path.Combine(testDirectory!, name));
            await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }
    }
}

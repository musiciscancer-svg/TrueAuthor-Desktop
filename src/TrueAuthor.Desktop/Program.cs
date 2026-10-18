using System.Drawing.Drawing2D;
using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return AuditStorageTest.Run();
        if (args.Contains("--discover")) { try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(WritingProjects.Discover(), SourceInfo.Json)); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Contains("--preview-prompt"))
        {
            ApplicationConfiguration.Initialize();
            using var preview = new DocumentPromptForm(new("preview", "Research essay", "Google Docs", ""), _ => { });
            preview.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(0, 0, preview.Width, preview.Height));
            Directory.CreateDirectory("outputs"); bitmap.Save("outputs/document-tracking-prompt.png"); return 0;
        }
        if (args.Contains("--preview"))
        {
            ApplicationConfiguration.Initialize();
            using var preview = new CompanionForm(() => true, _ => { }, () => { }, () => { }, _ => Task.CompletedTask, () => null, () => []);
            preview.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height); preview.DrawToBitmap(bitmap, preview.ClientRectangle with { Width = preview.Width, Height = preview.Height });
            Directory.CreateDirectory("outputs"); bitmap.Save("outputs/desktop-companion.png"); return 0;
        }
        using var single = new Mutex(true, @"Local\TrueAuthorDesktop", out var first);
        if (!first) { MessageBox.Show("True Author is already running in the notification area.", "True Author"); return 0; }
        ApplicationConfiguration.Initialize();
        using var context = new TrayContext();
        Application.Run(context);
        return 0;
    }
}

sealed class TrayContext : ApplicationContext
{
    readonly ClipboardLedger ledger = new();
    readonly ActivityLog log = new();
    readonly ClipboardMonitor monitor;
    readonly ProjectInputMonitor? projectInput;
    readonly System.Windows.Automation.AutomationFocusChangedEventHandler accessibleFocusHandler;
    volatile bool accessibleFocus;
    readonly ForegroundWatcher watcher;
    readonly LocalApi api;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem pauseItem;
    bool disposed;
    string token = Pairing.LoadOrCreate();
    ActivityForm? activity;
    AuditEditorForm? editor;
    readonly CompanionForm companion;
    readonly System.Windows.Forms.Timer projectTimer = new() { Interval = 750 };
    WritingProject? selectedProject;
    bool observing;
    readonly Dictionary<string, WritingProject> browserProjects = [];
    readonly Dictionary<string, DateTimeOffset> browserSeen = [];
    readonly Dictionary<string, DateTimeOffset> browserSnapshots = [];
    DateTimeOffset waitingSnapshot;
    bool externalUnavailable;
    string? selectedDocs;
    bool docsFirst;
    bool docsObserving;
    bool awaitingDocument = true;
    bool wordFocusVerified;
    BrowserSample? desktopBrowser;
    volatile bool desktopBrowserInput;
    bool desktopOnlyBrowser;
    readonly DocumentPromptPolicy documentPrompts = new();
    DocumentPromptForm? documentPrompt;
    bool startingSuggested;
    bool ConsiderProject(WritingProject project, string? key = null)
    {
        key ??= project.Id;
        if (key == (selectedDocs is null ? null : "docs:" + selectedDocs) || project.Id == selectedProject?.Id || documentPrompts.Approved(key)) return true;
        if (!documentPrompts.Offer(key, monitor.Paused, editor?.SyncBusy == true || startingSuggested, DateTimeOffset.UtcNow)) return false;
        var identity = key;
        documentPrompt = new DocumentPromptForm(project, async accepted =>
        {
            documentPrompts.Close(); documentPrompt = null;
            if (!accepted) return;
            startingSuggested = true;
            try
            {
                if (monitor.Paused) throw new InvalidOperationException("Capture is paused. Resume it to start this project.");
                if (project.Id.StartsWith("docs-window:") && identity.StartsWith("docs:"))
                {
                    var current = await Task.Run(() => DesktopBrowser.ReadWindow(new IntPtr(long.Parse(project.Id[12..])), false));
                    if (current?.Id != identity[5..]) throw new InvalidOperationException("That browser tab changed. Return to the document and select it from New project.");
                }
                await StartProject(project); documentPrompts.Approve(identity); companion.SetProject(project);
            }
            catch (Exception error) { companion.SetNotice(error.Message); companion.Reveal(); }
            finally { startingSuggested = false; }
        });
        documentPrompt.Show(); return false;
    }
    bool ExtensionFresh(string id) => browserSnapshots.GetValueOrDefault(id) >= DateTimeOffset.UtcNow.AddSeconds(-20);
    AuditEditorForm ProjectEditor() { if (editor is null || editor.IsDisposed) { editor = new AuditEditorForm(ledger, () => monitor.Paused); editor.Show(); editor.Hide(); } return editor; }
    async Task StartProject(WritingProject? project)
    {
        if (project?.Id.StartsWith("docs-window:") == true)
        {
            var sample = await Task.Run(() => DesktopBrowser.ReadWindow(new IntPtr(long.Parse(project.Id[12..]))));
            if (sample is null) throw new InvalidOperationException("This browser is not exposing the document URL. Bring the Docs tab forward, or use the optional extension.");
            var desktop = ProjectEditor(); desktop.ExternalGap(true); desktop.StartDesktopBrowser(sample.Id, sample.Title);
            selectedDocs = sample.Id; selectedProject = null; docsFirst = false; desktopOnlyBrowser = true; awaitingDocument = false;
            return;
        }
        if (project?.Id == "automatic") { if (editor is { IsDisposed: false }) editor.ExternalGap(true); selectedProject = null; selectedDocs = null; awaitingDocument = true; return; }
        var audit = ProjectEditor();
        externalUnavailable = false;
        awaitingDocument = false;
        if (project is null) { selectedProject = null; selectedDocs = null; audit.StartReferenceProject(); audit.Show(); }
        else if (project.Id.StartsWith("docs:")) { audit.ExternalGap(true); selectedProject = null; selectedDocs = project.Id[5..]; docsFirst = !audit.ResumeBrowserDocument(selectedDocs); waitingSnapshot = DateTimeOffset.UtcNow; }
        else { var first = !audit.ResumeNativeDocument(project.Id); var text = WritingProjects.ReadWord(project.Id); observing = true; try { await audit.ObserveExternal(text, first, !monitor.Paused, project.Id.StartsWith("uia:") ? "desktop-accessible-events" : projectInput is null ? "desktop-word-snapshot" : "desktop-word-events"); audit.SetNativeDocument(project.Id); audit.SetDocumentTitle(project.Title); selectedDocs = null; selectedProject = project; wordFocusVerified = !project.Id.StartsWith("uia:") && WritingProjects.ActiveWord(project.Id); } finally { observing = false; } }
    }
    async Task ObserveProject()
    {
        if (observing || monitor.Paused) return;
        observing = true;
        try
        {
            var sample = await Task.Run(DesktopBrowser.ReadForeground);
            desktopBrowser = sample;
            desktopBrowserInput = sample is { EditorFocused: true } && sample.Id == selectedDocs && !ExtensionFresh(sample.Id);
            if (sample is not null && !ExtensionFresh(sample.Id) && editor?.SyncBusy != true)
            {
                desktopOnlyBrowser = true;
                if (selectedDocs != sample.Id || docsFirst)
                {
                    if (!ConsiderProject(new("docs-window:" + sample.Window.ToInt64(), sample.Title, "Google Docs", "Desktop capture · extension optional"), "docs:" + sample.Id)) return;
                    var audit = ProjectEditor(); audit.ExternalGap(true); audit.StartDesktopBrowser(sample.Id, sample.Title);
                    selectedDocs = sample.Id; selectedProject = null; docsFirst = false; awaitingDocument = false;
                    companion.SetProject(new("docs:" + sample.Id, sample.Title, "Google Docs", "Desktop capture · extension optional for precise passage mapping"));
                }
                ProjectEditor().ResumeDesktopCoverage();
                if (sample.EditorFocused)
                {
                    var readable = await Task.Run(() => DesktopBrowser.ReadWindow(sample.Window));
                    if (readable?.Id == sample.Id && readable.Text is not null) await ProjectEditor().ObserveExternal(readable.Text, false, true);
                }
                else companion.SetNotice("Desktop-only capture: full Docs text is not exposed here. The optional extension adds precise document mapping.");
                return;
            }
        }
        finally { observing = false; }
        {
            try
            {
                var window = Native.GetForegroundWindow();
                var prefix = "uia:" + window.ToInt64() + ":";
                var surface = SourceClassifier.DetectWritingSurface(Native.ProcessName(Native.ProcessIdOf(window)), Native.WindowTitle(window));
                var candidates = WritingProjects.Discover().Where(p => surface?.Name == "Microsoft Word" ? p.App == "Microsoft Word" && p.Title == surface.DocumentTitle : p.Id.StartsWith(prefix) && AccessibleWriting.Focused(p.Id)).ToList();
                if (candidates.Count == 1 && candidates[0].Id != selectedProject?.Id && ConsiderProject(candidates[0])) { await StartProject(candidates[0]); companion.SetProject(candidates[0]); }
            }
            catch (Exception error) { companion.SetNotice(error.Message); }
            if (selectedProject is null && selectedDocs is null) return;
        }
        if (selectedDocs is not null)
        {
            var seen = browserSnapshots.GetValueOrDefault(selectedDocs, waitingSnapshot);
            if (seen < DateTimeOffset.UtcNow.AddSeconds(-20) && !externalUnavailable)
            {
                externalUnavailable = true; if (!docsFirst) ProjectEditor().ExternalGap(true); companion.SetNotice(desktopOnlyBrowser ? "Desktop capture resumes when this document is foreground again." : "Browser snapshots stopped. Desktop capture continues when the document is foreground.");
            }
            return;
        }
        if (selectedProject is null) return;
        observing = true;
        try { wordFocusVerified = !selectedProject.Id.StartsWith("uia:") && WritingProjects.ActiveWord(selectedProject.Id); if (!selectedProject.Id.StartsWith("uia:") && !wordFocusVerified) ProjectEditor().ObserveUncertainInput(); await ProjectEditor().ObserveExternal(WritingProjects.ReadWord(selectedProject.Id), false, !monitor.Paused); if (externalUnavailable) { externalUnavailable = false; companion.SetNotice("Document monitoring resumed."); } }
        catch (Exception error) { if (!externalUnavailable) { externalUnavailable = true; ProjectEditor().ExternalGap(); log.Add("project", error.Message); companion.SetNotice(error.Message); } }
        finally { observing = false; }
    }

    public TrayContext()
    {
        monitor = new ClipboardMonitor(ledger, log);
        watcher = new ForegroundWatcher();
        monitor.ClipboardChanged += () => { if (editor is { IsDisposed: false }) editor.InvalidateCopy(); };
        monitor.CopyCaptured += (text, source) =>
        {
            if (editor is { IsDisposed: false } && selectedProject is not null && !monitor.Paused)
            {
                var title = Native.WindowTitle(Native.GetForegroundWindow());
                if (source.App?.Equals("winword.exe", StringComparison.OrdinalIgnoreCase) == true && SourceClassifier.DetectWritingSurface(source.App, title)?.DocumentTitle == selectedProject.Title && WritingProjects.ActiveWord(selectedProject.Id)) editor.RememberInternalCopy(text);
                else if (selectedProject.Id.StartsWith("uia:") && accessibleFocus && source.App?.Equals(selectedProject.App, StringComparison.OrdinalIgnoreCase) == true && selectedProject.Id.StartsWith("uia:" + Native.GetForegroundWindow().ToInt64() + ":")) editor.RememberInternalCopy(text);
            }
        };
        accessibleFocusHandler = (_, _) =>
        {
            accessibleFocus = false;
            var chosen = selectedProject;
            if (chosen?.Id.StartsWith("uia:") == true) _ = Task.Run(() => { var focused = AccessibleWriting.Focused(chosen.Id); if (selectedProject?.Id == chosen.Id) accessibleFocus = focused; });
        };
        System.Windows.Automation.Automation.AddAutomationFocusChangedEventHandler(accessibleFocusHandler);
        companion = new CompanionForm(() => !monitor.Paused, SetPaused, ShowEditor, ShowActivity, StartProject, () => editor is { IsDisposed: false, HasCheckpoint: true } && !docsFirst ? "/audits/" + editor.DocumentId : null, () => browserProjects.Where(p => browserSeen[p.Key] >= DateTimeOffset.UtcNow.AddSeconds(-20)).Select(p => p.Value).ToList());
        if (Directory.Exists(AuditLocalStore.DirectoryPath)) foreach (var path in Directory.GetFiles(AuditLocalStore.DirectoryPath, "*.taudit"))
        {
            try
            {
                var saved = AuditLocalStore.Load(path);
                if (saved.ExternalId?.StartsWith("docs:") == true) documentPrompts.Approve(saved.ExternalId);
                else if (saved.ExternalId?.StartsWith("native:") == true && !saved.ExternalId.StartsWith("native:uia:")) documentPrompts.Approve(saved.ExternalId[7..]);
            }
            catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or IOException or FormatException) { }
        }
        try
        {
            projectInput = new ProjectInputMonitor(() =>
            {
                if (monitor.Paused) return false;
                if (desktopBrowserInput && desktopBrowser is { } browser) return Native.GetForegroundWindow() == browser.Window;
                if (selectedProject is null) return false;
                if (selectedProject.Id.StartsWith("uia:")) return accessibleFocus && selectedProject.Id.StartsWith("uia:" + Native.GetForegroundWindow().ToInt64() + ":", StringComparison.Ordinal);
                var window = Native.GetForegroundWindow();
                var process = Native.ProcessName(Native.ProcessIdOf(window));
                var surface = SourceClassifier.DetectWritingSurface(process, Native.WindowTitle(window));
                return wordFocusVerified && string.Equals(process, "winword.exe", StringComparison.OrdinalIgnoreCase) && string.Equals(surface?.DocumentTitle, selectedProject.Title, StringComparison.OrdinalIgnoreCase);
            }, (kind, units) => { var projectId = selectedProject?.Id; var browserId = selectedDocs; companion.BeginInvoke((Action)(() =>
            {
                if (!monitor.Paused && browserId is not null && selectedDocs == browserId && desktopBrowser?.Id == browserId && !ExtensionFresh(browserId))
                {
                    string? copied = null; SourceInfo? source = null;
                    if (kind == "paste") try { copied = Clipboard.GetText(); if (copied.Length > 1_000_000) copied = null; else source = ledger.Find(SourceClassifier.HashText(copied))?.Source; } catch (System.Runtime.InteropServices.ExternalException) { }
                    ProjectEditor().RecordDesktopObservation(kind, units, copied, source); return;
                }
                if (monitor.Paused || selectedProject is null || selectedProject.Id != projectId) return;
                var audit = ProjectEditor();
                if (kind == "typing") audit.ObserveTyping(units);
                else if (kind == "paste")
                {
                    try { var text = Clipboard.GetText(); audit.ObservePaste(text, ledger.Find(SourceClassifier.HashText(text))?.Source); }
                    catch (System.Runtime.InteropServices.ExternalException) { audit.ObserveUncertainInput(); }
                }
                else audit.ObserveUncertainInput();
            })); });
        }
        catch (Exception error) { log.Add("capture", error.Message); }
        projectTimer.Tick += async (_, _) => await ObserveProject(); projectTimer.Start();
        api = new LocalApi(ledger, log, () => !monitor.Paused, () => token, command =>
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            companion.BeginInvoke((Action)(() => { try { completion.SetResult(command == "preview-metadata" ? editor is { IsDisposed: false } ? editor.OutgoingMetadata() : new { error = "Select a project first." } : command is null ? companion.Status() : companion.Control(command)); } catch (Exception error) { completion.SetException(error); } }));
            return completion.Task;
        }, report =>
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            companion.BeginInvoke((Action)(async () =>
            {
                try
                {
                    browserSeen[report.Id] = DateTimeOffset.UtcNow;
                    if ((report.PasteText is null && report.Title != "Google Docs") || !browserProjects.ContainsKey(report.Id)) browserProjects[report.Id] = new("docs:" + report.Id, report.Title, report.App, "Input audit · typing, observed pastes and revisions");
                    if (LocalApi.ShouldFollow(report, selectedDocs, awaitingDocument, monitor.Paused, editor?.SyncBusy == true) && ConsiderProject(browserProjects[report.Id])) { var chosen = browserProjects[report.Id]; await StartProject(chosen); companion.SetProject(chosen); }
                    var selected = selectedDocs == report.Id;
                    if (selected && !docsFirst && !monitor.Paused && report.CopyText is not null) ProjectEditor().RememberInternalCopy(report.CopyText);
                    if (selected && !docsFirst && !monitor.Paused && report.PasteText is not null) ProjectEditor().ObservePaste(report.PasteText, report.Source);
                    if (selected && !docsFirst && !monitor.Paused && report.Typed > 0) ProjectEditor().ObserveTyping(report.Typed);
                    if (selected && !docsFirst && !monitor.Paused && report.Uncertain) ProjectEditor().ObserveUncertainInput();
                    if (selected && report.Text is not null && !monitor.Paused && !docsObserving)
                    {
                        docsObserving = true;
                        try { desktopOnlyBrowser = false; if (!docsFirst) ProjectEditor().BrowserPrecision(report.App == "Browser editor" ? "desktop-browser-events" : "desktop-google-docs-events"); await ProjectEditor().ObserveExternal(report.Text, docsFirst, true, report.App == "Browser editor" ? "desktop-browser-events" : "desktop-google-docs-events"); ProjectEditor().BrowserPrecision(report.App == "Browser editor" ? "desktop-browser-events" : "desktop-google-docs-events"); ProjectEditor().SetBrowserDocument(report.Id); ProjectEditor().SetDocumentTitle(report.Title); docsFirst = false; browserSnapshots[report.Id] = DateTimeOffset.UtcNow; if (externalUnavailable) { externalUnavailable = false; companion.SetNotice("Document monitoring resumed."); } }
                        finally { docsObserving = false; }
                    }
                    completion.SetResult(new { selected = selected && !monitor.Paused });
                }
                catch (Exception error) { companion.SetNotice("Capture unavailable: " + error.Message); completion.SetException(error); }
            }));
            return completion.Task;
        }, () => selectedDocs) { ApproveConnection = origin =>
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            companion.BeginInvoke((Action)(() =>
            {
                try { completion.SetResult(MessageBox.Show(companion, "Allow the True Author Chrome extension to connect?\n\nSelected document text and copy-source details will be shared with this app on your PC. Only allow this if you just enabled recording or clicked Connect in True Author.\n\nExtension: " + origin, "True Author — Allow connection", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes); }
                catch (Exception error) { completion.SetException(error); }
            }));
            return completion.Task;
        } };

        pauseItem = new ToolStripMenuItem("Pause capture", null, (_, _) => SetPaused(!monitor.Paused));
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open True Author", null, (_, _) => companion.Reveal());
        menu.Items.Add("Test writing editor", null, (_, _) => ShowEditor());
        menu.Items.Add("Show what True Author holds…", null, (_, _) => ShowActivity());
        menu.Items.Add("Copy browser pairing code", null, (_, _) => { Clipboard.SetText(token); Notify("Pairing code copied. Paste it into the True Author browser extension."); });
        menu.Items.Add(pauseItem);
        menu.Items.Add("Forget recent copies", null, (_, _) => { ledger.Clear(); log.Add("cleared", "Recent copies forgotten."); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reset browser connections", null, (_, _) => { token = Pairing.Reset(); Notify("Browser connections reset. Approve the extension again when it reconnects."); });
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        tray = new NotifyIcon { Icon = MakeIcon(false), Text = "True Author: capturing copy sources", ContextMenuStrip = menu, Visible = true };
        tray.Click += (_, e) => { if (e is MouseEventArgs { Button: MouseButtons.Left }) companion.Reveal(); };

        watcher.SurfaceEntered += async (surface, _) =>
        {
            log.Add("writing", $"Writing in {surface.Name}: \"{surface.DocumentTitle}\"");
            if (surface.Name == "Microsoft Word" && !monitor.Paused)
            {
                try { var matches = WritingProjects.Discover().Where(p => p.App == "Microsoft Word" && p.Title == surface.DocumentTitle).ToList(); if (matches.Count == 1 && matches[0].Id != selectedProject?.Id && ConsiderProject(matches[0])) { await StartProject(matches[0]); companion.SetProject(matches[0]); } }
                catch (Exception error) { companion.SetNotice(error.Message); }
            }
            // Foreground changes happen repeatedly while writing. Guidance belongs in the companion,
            // never in an automatic balloon notification.
            if (surface.Name == "Google Docs" && selectedDocs is null)
                companion.SetNotice("Google Docs detected. A tracking prompt appears when its document identity is available.");
        };

        _ = StartApiAsync();
        log.Add("started", "Capturing copy sources. Copies stay in memory for 30 minutes.");
        companion.Reveal();
    }

    void ShowEditor()
    {
        if (editor is { IsDisposed: false }) { editor.Activate(); return; }
        editor = new AuditEditorForm(ledger, () => monitor.Paused);
        editor.Show();
    }

    async Task StartApiAsync()
    {
        try { await api.StartAsync(); log.Add("api", $"Browser connection ready on 127.0.0.1:{LocalApi.Port}."); }
        catch (Exception error) { log.Add("error", $"Browser connection unavailable: {error.Message}"); Notify("Could not open the browser connection. Is another copy running?"); }
    }

    void SetPaused(bool paused)
    {
        monitor.Paused = paused;
        companion.RefreshState();
        if (editor is { IsDisposed: false }) editor.CaptureStateChanged();
        pauseItem.Text = paused ? "Resume capture" : "Pause capture";
        tray.Icon = MakeIcon(paused);
        tray.Text = paused ? "True Author: paused" : "True Author: capturing copy sources";
        log.Add(paused ? "paused" : "resumed", paused ? "Capture paused by you." : "Capture resumed.");
    }

    void ShowActivity()
    {
        if (activity is { IsDisposed: false }) { activity.Activate(); return; }
        activity = new ActivityForm(ledger, log);
        activity.Show();
    }

    void Notify(string message) => tray.ShowBalloonTip(4000, "True Author", message, ToolTipIcon.Info);

    static Icon MakeIcon(bool paused) => paused ? Branding.PausedIcon : Branding.AppIcon;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            documentPrompt?.Dispose();
            tray.Visible = false;
            tray.Dispose();
            companion.Dispose();
            projectTimer.Stop(); projectTimer.Dispose();
            watcher.Dispose();
            monitor.Dispose();
            projectInput?.Dispose();
            System.Windows.Automation.Automation.RemoveAutomationFocusChangedEventHandler(accessibleFocusHandler);
            api.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}


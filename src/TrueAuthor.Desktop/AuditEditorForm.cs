using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

sealed class AuditTextBox : RichTextBox
{
    public Action<AuditOrigin, int, int>? Intent;
    public Func<AuditOrigin>? PasteOrigin;
    public Action<int, int>? CopyIntent;
    void PasteIntent() => Intent?.Invoke(PasteOrigin?.Invoke() ?? new("paste", "unknown", "none", PassageId: Guid.NewGuid().ToString()), SelectionStart, SelectionLength);
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert)) { PasteIntent(); Paste(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg is 0x300 or 0x301) CopyIntent?.Invoke(SelectionStart, SelectionLength);
        if (m.Msg == 0x302) PasteIntent();
        else if (m.Msg is 0x100 or 0x102) Intent?.Invoke(new("unobserved", "unknown", "none"), SelectionStart, SelectionLength);
        if (m.Msg == 0x102 && (!char.IsControl((char)m.WParam.ToInt32()) || m.WParam.ToInt32() is 13 or 9)) Intent?.Invoke(new("typed", "none", "keyboard"), SelectionStart, SelectionLength);
        base.WndProc(ref m);
    }
}

sealed class AuditEditorForm : Form
{
    DocumentAudit audit = new();
    readonly AuditTextBox editor = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 12), AcceptsTab = true, MaxLength = 1_000_000, DetectUrls = false };
    readonly TextBox endpoint = new() { Text = Environment.GetEnvironmentVariable("TRUEAUTHOR_SERVER_URL") ?? "https://trueauthor.io", Width = 240, AccessibleName = "True Author server address" };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(10), AutoEllipsis = true };
    readonly Label summary = new() { Dock = DockStyle.Top, Height = 52, Padding = new Padding(10) };
    readonly CheckBox autoSync = new() { Text = "Sync metadata every 15 seconds", Checked = true, AutoSize = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15_000 };
    readonly CancellationTokenSource lifetime = new();
    readonly Func<bool> capturePaused;
    readonly ClipboardLedger copyLedger;
    readonly ListView sources = new() { Dock = DockStyle.Right, Width = 340, View = View.Details, FullRowSelect = true };
    Guid documentId = Guid.NewGuid();
    DateTimeOffset started = DateTimeOffset.UtcNow;
    AuditOrigin intent = new("unobserved", "unknown", "none");
    int? anchor;
    int anchorLength;
    bool paused, restoring, painting, busy, lastGlobalPaused;
    int sequence, revision, lastQueuedRevision = -1;
    string previous = new('0', 64);
    List<string> pending = [];
    AuditConnection? connection;
    string adapter = "desktop-reference-editor";
    readonly InputReconciler externalInput = new();
    uint lastPasteSequence;
    string internalCopy = "";
    IReadOnlyList<AuditSpan>? internalLineage;
    uint internalSequence;
    IReadOnlyList<AuditSpan>? referenceCopyPaste;
    string referenceCopyText = "";
    IReadOnlyList<AuditSpan>? InternalPaste(string text)
    {
        InvalidateCopy();
        if (text != internalCopy || internalLineage is null) return null;
        return internalLineage.Select(s => s with { Origin = s.Origin.Kind == "paste" ? s.Origin with { Detection = "local-lineage", PassageId = Guid.NewGuid().ToString() } : new("paste", s.Origin.Kind == "unobserved" ? "unknown" : "document", "local-lineage", "Tracked document", PassageId: Guid.NewGuid().ToString()) }).ToList();
    }
    public void InvalidateCopy() { if (internalSequence != Native.GetClipboardSequenceNumber()) { internalCopy = ""; internalLineage = null; } }
    public void RememberInternalCopy(string text, int? start = null)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        try { if (Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n') != normalized) return; }
        catch (System.Runtime.InteropServices.ExternalException) { return; }
        internalCopy = normalized; internalLineage = audit.CopyLineage(internalCopy, start); internalSequence = Native.GetClipboardSequenceNumber();
    }
    public void ObserveTyping(int units) => externalInput.Typed(units);
    public void ObserveUncertainInput() => externalInput.Uncertain();
    public void ObservePaste(string text, SourceInfo? source)
    {
        lastPasteSequence = Native.GetClipboardSequenceNumber();
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var local = InternalPaste(text);
        if (local is not null)
        {
            externalInput.Paste(text, new("paste", "document", "local-lineage", "Tracked document", PassageId: Guid.NewGuid().ToString()), DateTimeOffset.UtcNow, local); return;
        }
        var safe = SourceInfo.Sanitize(source);
        if (safe?.Url is not null && SourceClassifier.ClassifyUrl(safe.Url) is Classification.Blocked) return;
        var host = safe?.Domain?.ToLowerInvariant() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(host, @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$") || host.Length > 253) host = "";
        externalInput.Paste(text, new("paste", safe?.Category ?? "unknown", safe?.Detection ?? "none", safe?.Name ?? "unknown", host, Guid.NewGuid().ToString(), safe?.Url ?? ""), DateTimeOffset.UtcNow);
    }
    public Guid DocumentId => documentId;
    public bool HasCheckpoint => sequence > 0;
    public bool SyncBusy => busy;
    string? externalId;
    string captureMode = "desktop-only";
    DesktopObservations observations = new(0, 0, 0, []);
    public void StartDesktopBrowser(string id, string title)
    {
        if (!ResumeBrowserDocument(id)) { ResetDocument(true); adapter = "desktop-google-docs-snapshot"; editor.ReadOnly = true; externalId = "docs:" + id; audit.Gap(); }
        documentTitle = title; captureMode = "desktop-only"; paused = false; Save();
    }
    public void BrowserPrecision(string adapterName = "desktop-google-docs-events") { captureMode = "desktop+extension"; adapter = adapterName; Save(); }
    public void ResumeDesktopCoverage() { captureMode = "desktop-only"; if (paused) { paused = false; audit.Gap(); Save(); } }
    public void RecordDesktopObservation(string kind, int units, string? text = null, SourceInfo? source = null)
    {
        observations = observations with { TypedKeys = observations.TypedKeys + (kind == "typing" ? units : 0), PasteAttempts = observations.PasteAttempts + (kind == "paste" ? 1 : 0), UncertainEvents = observations.UncertainEvents + (kind == "uncertain" ? 1 : 0) };
        if (kind != "typing") { observations.Recent.Add(new(DateTimeOffset.UtcNow, kind, text?.Length ?? units, text, SourceInfo.Sanitize(source))); while (observations.Recent.Count > 200 || observations.Recent.Sum(o => o.Text?.Length ?? 0) > 2_000_000) observations.Recent.RemoveAt(0); }
        Save();
    }
    public bool ResumeBrowserDocument(string id)
    {
        var saved = AuditLocalStore.FindExternal("docs:" + id);
        if (saved is null) return false;
        Restore(saved); Connect(); return true;
    }
    public void SetBrowserDocument(string id) { externalId = "docs:" + id; Save(); }
    public bool ResumeNativeDocument(string id)
    {
        var saved = AuditLocalStore.FindExternal("native:" + id); if (saved is null) return false;
        Restore(saved); Connect(); return true;
    }
    public void SetNativeDocument(string id) { externalId = "native:" + id; Save(); }
    public void ExternalGap(bool pause = false) { audit.Gap(); if (pause) paused = true; externalInput.Clear(); revision++; Save(); RefreshStatus(); }
    public object OutgoingMetadata() => Metadata();
    public string ServerAddress => endpoint.Text;
    public async Task ObserveExternal(string text, bool first, bool capturing, string adapterName = "desktop-word-snapshot")
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > 1_000_000) throw new InvalidOperationException("Document exceeds the capture limit.");
        if (first) { ResetDocument(true); adapter = adapterName; editor.ReadOnly = true; endpoint.Text = Environment.GetEnvironmentVariable("TRUEAUTHOR_SERVER_URL") ?? "https://trueauthor.io"; Save(); }
        if (!first && !capturing) { if (!paused) { paused = true; audit.Gap(); revision++; Save(); } return; }
        if (paused) { paused = false; audit.Gap(); revision++; }
        if (audit.Text != text)
        {
            externalInput.RefreshSources((copied, origin) =>
            {
                if (lastPasteSequence != Native.GetClipboardSequenceNumber()) return origin;
                var source = copyLedger.Find(SourceClassifier.HashText(copied))?.Source;
                if (source is null) return origin;
                var minimum = origin.Detection switch { "extension" => 35, "clipboard-html" => 34, "window-title" => 23, "process" => 22, _ => 0 };
                if (source.Strength < minimum) return origin;
                var host = source.Domain?.ToLowerInvariant() ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(host, @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$") || host.Length > 253) host = "";
                return origin with { Category = source.Category, Detection = source.Detection, Source = source.Name, Host = host, LocalUrl = source.Url ?? "" };
            });
            var origin = new AuditOrigin(first ? "preexisting" : "unobserved", "unknown", first ? "file" : "none");
            if (!first) audit.UpdateFromInput(text, externalInput.Reconcile(audit.Text, text, DateTimeOffset.UtcNow));
            else { externalInput.Clear(); audit.Update(text, origin); }
            restoring = true; try { editor.Text = text; } finally { restoring = false; }
            revision++; Save(); RefreshStatus();
        }
        else if (externalInput.HasPending) { externalInput.Clear(); audit.Gap(); revision++; Save(); }
        if (first) _ = Sync();
        await Task.CompletedTask;
    }
    public void StartReferenceProject() { NewDocument(); adapter = "desktop-reference-editor"; endpoint.Text = Environment.GetEnvironmentVariable("TRUEAUTHOR_SERVER_URL") ?? "https://trueauthor.io"; editor.ReadOnly = false; Save(); }

    public AuditEditorForm(ClipboardLedger ledger, Func<bool> capturePaused)
    {
        this.capturePaused = capturePaused;
        copyLedger = ledger;
        Text = "True Author — private document creation audit"; Width = 1250; Height = 850; MinimumSize = new Size(900, 600);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 110, Padding = new Padding(10) };
        bar.Controls.Add(new Label { Text = "Audit server", AutoSize = true }); bar.Controls.Add(endpoint);
        void Button(string label, Action action) { var b = new Button { Text = label, AutoSize = true }; b.Click += (_, _) => { try { action(); } catch (Exception e) { status.Text = e.Message; } }; bar.Controls.Add(b); }
        var link = new Button { Text = "Link this device", AutoSize = true };
        link.Click += async (_, _) => { if (busy) return; busy = true; try { Connect(); await connection!.Link(message => status.Text = message, lifetime.Token); Save(); } catch (Exception e) { status.Text = e.Message; } finally { busy = false; } }; bar.Controls.Add(link);
        Button("Pause / resume", () => { paused = !paused; audit.Gap(); revision++; Save(); RefreshStatus(); });
        Button("New document", NewDocument);
        Button("Open saved audit", OpenSaved);
        Button("Delete local audit", DeleteLocal);
        Button("Import text", Import);
        Button("Highlight origins", Highlight);
        Button("Preview outgoing data", Preview);
        Button("Export local report", Export);
        Button("Save document text", SaveText);
        Button("Open server reports", () => { Connect(); Process.Start(new ProcessStartInfo(new Uri(connection!.Origin, "/audits").AbsoluteUri) { UseShellExecute = true }); });
        var sync = new Button { Text = "Sync now", AutoSize = true }; sync.Click += async (_, _) => await Sync(); bar.Controls.Add(sync); bar.Controls.Add(autoSync);
        sources.Columns.Add("Origin / source", 210); sources.Columns.Add("Units", 65); sources.Columns.Add("Start", 55);
        sources.SelectedIndexChanged += (_, _) => { if (sources.SelectedItems.Count > 0 && sources.SelectedItems[0].Tag is AuditSpan s) { editor.Select(s.Start, s.Length); editor.Focus(); } };
        Controls.Add(editor); Controls.Add(sources); Controls.Add(summary); Controls.Add(bar); Controls.Add(status);
        editor.Intent = (origin, start, length) => { intent = origin; anchor = start; anchorLength = length; };
        editor.CopyIntent = (start, length) =>
        {
            if (paused || capturePaused() || length == 0) return;
            var text = audit.Text.Substring(start, length); var lineage = audit.CopyLineage(text, start);
            BeginInvoke((Action)(() => { if (Native.ProcessIdOf(Native.GetClipboardOwner()) == (uint)Environment.ProcessId) { internalCopy = text; internalLineage = lineage; internalSequence = Native.GetClipboardSequenceNumber(); } }));
        };
        editor.PasteOrigin = () =>
        {
            referenceCopyPaste = null;
            var id = Guid.NewGuid().ToString();
            try
            {
                if (paused || capturePaused()) return new("unobserved", "unknown", "none");
                referenceCopyText = Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'); referenceCopyPaste = InternalPaste(referenceCopyText);
                var source = ledger.Find(SourceClassifier.HashText(Clipboard.GetText()))?.Source;
                // Only the domain crosses the server boundary. Full URL/name remains local.
                var host = source?.Domain?.ToLowerInvariant() ?? "";
                if (host == "" && Uri.TryCreate(source?.Url, UriKind.Absolute, out var uri)) host = uri.Host.ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(host, @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$") || host.Length > 253) host = "";
                return new("paste", source?.Category ?? "unknown", source?.Detection ?? "none", source?.Name ?? "unknown", host, id, source?.Url ?? "");
            }
            catch (System.Runtime.InteropServices.ExternalException) { return new("paste", "unknown", "none", PassageId: id); }
        };
        editor.TextChanged += (_, _) =>
        {
            if (painting || restoring) return;
            if (!paused && !capturePaused() && intent.Kind == "paste" && referenceCopyPaste is not null)
            {
                var input = new InputReconciler(); input.Paste(referenceCopyText, intent, DateTimeOffset.UtcNow, referenceCopyPaste);
                audit.UpdateFromInput(editor.Text, input.Reconcile(audit.Text, editor.Text, DateTimeOffset.UtcNow, anchor, anchorLength), anchor, anchorLength);
            }
            else audit.Update(editor.Text, paused || capturePaused() ? new("unobserved", "unknown", "none") : intent, anchor, anchorLength);
            referenceCopyPaste = null;
            intent = new("unobserved", "unknown", "none"); anchor = null; anchorLength = 0; revision++;
            try { Save(); } catch (Exception e) { status.Text = "Local recovery could not be saved: " + e.Message; }
            RefreshStatus();
        };
        timer.Tick += async (_, _) => { if (capturePaused() != lastGlobalPaused) { lastGlobalPaused = capturePaused(); audit.Gap(); revision++; Save(); } RefreshStatus(); if (autoSync.Checked && !busy && connection?.Linked == true) await Sync(); };
        FormClosing += (_, e) => { try { Save(); } catch (Exception error) { if (MessageBox.Show("Local recovery failed: " + error.Message + "\nClose and lose unsaved changes?", "True Author", MessageBoxButtons.YesNo) != DialogResult.Yes) e.Cancel = true; } };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); lifetime.Cancel(); lifetime.Dispose(); connection?.Dispose(); };
        Load += (_, _) =>
        {
            try
            {
                if (Directory.Exists(AuditLocalStore.DirectoryPath))
                {
                    var latest = Directory.GetFiles(AuditLocalStore.DirectoryPath, "*.taudit").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (latest is not null) Restore(AuditLocalStore.Load(latest));
                }
                Connect(); RefreshStatus();
            }
            catch (Exception e) { status.Text = "Recovery/server setup: " + e.Message; }
            timer.Start();
        };
        RefreshStatus();
    }
    void Connect()
    {
        var next = new Uri(endpoint.Text.Trim()).GetLeftPart(UriPartial.Authority);
        if (connection is not null && connection.Origin.GetLeftPart(UriPartial.Authority) == next) return;
        if (connection is not null && (sequence > 0 || pending.Count > 0)) throw new InvalidOperationException("This audit belongs to its current server. Create a new document to use a different server.");
        var replacement = new AuditConnection(endpoint.Text); connection?.Dispose(); connection = replacement;
    }
    string documentTitle = "Untitled writing project";
    public void SetDocumentTitle(string title) { documentTitle = title; Save(); }
    void Save() => AuditLocalStore.Save(new(documentId, started, endpoint.Text.Trim(), sequence, previous, paused, audit.Snapshot(), pending, adapter, documentTitle, externalId, captureMode, observations));
    void Restore(SavedAudit saved)
    {
        var restored = DocumentAudit.Restore(saved.Document); adapter = saved.Adapter; editor.ReadOnly = adapter != "desktop-reference-editor";
        documentTitle = saved.Title ?? "Writing project · " + saved.DocumentId.ToString()[..8];
        externalId = saved.ExternalId;
        captureMode = saved.CaptureMode; observations = saved.Observations ?? new(0, 0, 0, []);
        connection?.Dispose(); connection = null; documentId = saved.DocumentId; started = saved.StartedAt; endpoint.Text = saved.ServerOrigin; sequence = saved.Sequence; previous = saved.Previous; pending = saved.Pending; paused = true; audit = restored;
        audit.Gap(); revision++; lastQueuedRevision = -1;
        restoring = true; try { editor.Text = audit.Text; } finally { restoring = false; }
        Save(); RefreshStatus(); status.Text = "Recovered encrypted local audit, paused. A restart interruption was recorded. Choose Pause / resume to continue.";
    }
    void NewDocument()
    {
        ResetDocument(true);
    }
    void ResetDocument(bool saveOld)
    {
        if (busy) throw new InvalidOperationException("Wait for device linking/sync to finish.");
        if (saveOld) Save(); documentTitle = "Untitled writing project"; externalId = null; captureMode = "desktop-only"; observations = new(0, 0, 0, []); externalInput.Clear(); documentId = Guid.NewGuid(); started = DateTimeOffset.UtcNow; audit = new(); sequence = 0; previous = new('0', 64); pending = []; paused = false; revision = 0; lastQueuedRevision = -1;
        restoring = true; try { editor.Clear(); } finally { restoring = false; } Save(); RefreshStatus();
    }
    void DeleteLocal()
    {
        if (busy) throw new InvalidOperationException("Wait for device linking/sync to finish.");
        if (MessageBox.Show("Permanently delete this local audit, original pasted passages and pending uploads? Cloud checkpoints and exported files are unaffected.", "Delete local audit", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        File.Delete(AuditLocalStore.PathFor(documentId)); ResetDocument(false); status.Text = "Local audit deleted. A new blank document is open. Manage cloud evidence in the web dashboard.";
    }
    public void CaptureStateChanged()
    {
        externalInput.Clear();
        lastGlobalPaused = capturePaused(); audit.Gap(); revision++; Save(); RefreshStatus();
    }
    void OpenSaved()
    {
        if (busy) throw new InvalidOperationException("Wait for device linking/sync to finish.");
        Save(); using var d = new OpenFileDialog { Filter = "Encrypted True Author audits|*.taudit", InitialDirectory = AuditLocalStore.DirectoryPath }; if (d.ShowDialog() == DialogResult.OK) { Restore(AuditLocalStore.Load(d.FileName)); Connect(); }
    }
    void Import()
    {
        using var d = new OpenFileDialog { Filter = "Text files|*.txt" }; if (d.ShowDialog() != DialogResult.OK) return;
        var text = File.ReadAllText(d.FileName); if (text.Length > 1_000_000) throw new InvalidOperationException("This test editor supports up to one million UTF-16 units.");
        NewDocument();
        intent = new("preexisting", "unknown", "file"); anchor = 0; editor.Text = text;
    }
    static Color ColorOf(AuditOrigin o) => o.Kind switch { "typed" => Color.Honeydew, "paste" when o.Category == "ai" => Color.MistyRose, "paste" => Color.LightYellow, "preexisting" => Color.LightGray, _ => Color.LightBlue };
    void RefreshStatus()
    {
        summary.Text = $"{(paused || capturePaused() ? "PAUSED — changes are unobserved" : "RECORDING THIS EDITOR")} · {audit.Text.Length:N0} UTF-16 units · added {audit.Inserted:N0} · deleted {audit.Deleted:N0} · interruptions {audit.Gaps}\nDocument {documentId.ToString()[..8]} · server checkpoints {sequence} · queued {pending.Count} · {(connection?.Linked == true ? "device linked" : "device not linked")} · Content and full source URLs remain on this PC.";
        sources.BeginUpdate(); sources.Items.Clear();
        foreach (var span in audit.Spans()) sources.Items.Add(new ListViewItem([span.Origin.Kind + (span.Origin.Kind == "paste" ? ": " + span.Origin.Source : ""), span.Length.ToString(), span.Start.ToString()]) { Tag = span, BackColor = ColorOf(span.Origin) });
        sources.EndUpdate();
    }
    void Highlight()
    {
        painting = true;
        try { int start = editor.SelectionStart, length = editor.SelectionLength; foreach (var span in audit.Spans()) { editor.Select(span.Start, span.Length); editor.SelectionBackColor = ColorOf(span.Origin); } editor.Select(start, length); }
        finally { painting = false; }
    }
    static string SafeSource(string source) => new[] { "ChatGPT", "Claude", "Gemini", "Microsoft Copilot", "Perplexity", "Microsoft Word", "Google Docs", "Notepad", "LibreOffice Writer", "Microsoft PowerPoint", "Microsoft Excel", "OneNote", "Notion", "Obsidian", "Adobe Acrobat", "Adobe Acrobat Reader", "SumatraPDF", "Foxit PDF Reader", "Zotero", "Mendeley", "Visual Studio Code", "Outlook", "Microsoft Teams", "Slack", "Discord", "Tracked document" }.FirstOrDefault(name => source == name || source == name + " desktop") ?? (source == "unknown" ? "unknown" : "other");
    static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    object Metadata() => new { version = 4, activity = audit.Activity, documentId, sequence = sequence + pending.Count + 1, previous = pending.Count > 0 ? Digest(pending[^1]) : previous, recordedAt = DateTimeOffset.UtcNow.ToString("O"), startedAt = started.ToString("O"), contentHash = Digest(audit.Text), total = audit.Text.Length, inserted = audit.Inserted, deleted = audit.Deleted, gaps = audit.Gaps, recordingState = paused || capturePaused() ? "paused" : "recording", client = new { version = "0.5.0", adapter }, spans = audit.Spans().Select(s => new { s.Start, s.Length, kind = s.Origin.Kind, category = s.Origin.Category, detection = s.Origin.Detection, source = s.Origin.Kind == "paste" ? SafeSource(s.Origin.Source) : "unknown", host = s.Origin.Kind == "paste" ? s.Origin.Host : "", passageId = s.Origin.Kind == "paste" ? s.Origin.PassageId : "" }), passages = audit.Passages.Select(p => new { id = p.Id, characters = p.Characters, recordedAt = p.RecordedAt.ToString("O"), category = p.Origin.Category, detection = p.Origin.Detection, source = SafeSource(p.Origin.Source), host = p.Origin.Host }) };
    void Preview()
    {
        var preview = new Form { Text = "Outgoing metadata — no manuscript or clipboard text", Width = 850, Height = 650 };
        preview.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Text = pending.FirstOrDefault() ?? JsonSerializer.Serialize(Metadata(), new JsonSerializerOptions(SourceInfo.Json) { WriteIndented = true }) }); preview.Show(this);
    }
    async Task Sync()
    {
        if (busy) return; busy = true;
        try
        {
            Connect();
            if (!connection!.Linked) { connection.Dispose(); connection = new AuditConnection(endpoint.Text); }
            if (!connection.Linked) throw new InvalidOperationException("Link this device to your account first.");
            if (revision != lastQueuedRevision)
            {
                if (pending.Count >= 100) throw new InvalidOperationException("Local retry queue is full. Restore the server connection; your document remains saved locally.");
                var body = JsonSerializer.Serialize(Metadata(), SourceInfo.Json);
                if (Encoding.UTF8.GetByteCount(body) > 500_000 || (audit.Spans().Count > 5000 || audit.Passages.Count > 5000)) throw new InvalidOperationException("Passage map exceeds this adapter's upload limit. Export a local report; the server has only earlier checkpoints.");
                pending.Add(body); lastQueuedRevision = revision; Save();
            }
            while (pending.Count > 0)
            {
                var body = pending[0]; var digest = await connection.Upload(body, lifetime.Token);
                if (digest != Digest(body)) throw new InvalidOperationException("Server digest mismatch. Local evidence was retained.");
                previous = digest; sequence++; pending.RemoveAt(0); Save();
            }
            status.Text = $"Checkpoint {sequence} synced with a device signature. Server reports contain metadata only.";
        }
        catch (Exception e) { status.Text = "Sync: " + e.Message + " Local recovery and queued checkpoints are preserved."; }
        finally { busy = false; if (!IsDisposed) RefreshStatus(); }
    }
    void SaveText()
    {
        using var d = new SaveFileDialog { Filter = "UTF-8 text|*.txt", FileName = "document.txt" }; if (d.ShowDialog() == DialogResult.OK) { File.WriteAllText(d.FileName, audit.Text, new UTF8Encoding(false)); status.Text = "Saved exact document text locally. Open this file in a server report to verify its fingerprint and view highlights locally."; }
    }
    void Export()
    {
        using var d = new SaveFileDialog { Filter = "HTML report|*.html", FileName = "trueauthor-local-audit.html" }; if (d.ShowDialog() != DialogResult.OK) return;
        var html = new StringBuilder("<!doctype html><meta charset='utf-8'><title>Local document audit</title><style>body{font:16px system-ui;max-width:1000px;margin:40px auto}article{white-space:pre-wrap}mark{color:inherit}table{border-collapse:collapse}td,th{padding:8px;border:1px solid #ddd}</style><h1>Document creation audit</h1><p>Generated locally. Contains manuscript and detailed sources: share deliberately. Client observations do not prove independent authorship.</p><p>Green: typed; red: observed AI-source paste; yellow: other paste; grey: pre-existing; blue: unobserved.</p>");
        html.Append($"<p>Document {documentId} · interruptions {audit.Gaps} · server checkpoints {sequence} · queued {pending.Count}</p><p>Exact text SHA-256: {Digest(audit.Text)}</p><article>");
        foreach (var s in audit.Spans()) html.Append($"<mark style='background:{ColorTranslator.ToHtml(ColorOf(s.Origin))}' title='{WebUtility.HtmlEncode(s.Origin.Kind + ": " + s.Origin.Source + " " + s.Origin.LocalUrl)}'>{WebUtility.HtmlEncode(audit.Text.Substring(s.Start, s.Length))}</mark>");
        html.Append("</article><h2>Passage sources</h2><table><tr><th>Position</th><th>Units</th><th>Origin</th><th>Source (local)</th></tr>");
        foreach (var s in audit.Spans()) html.Append($"<tr><td>{s.Start}</td><td>{s.Length}</td><td>{WebUtility.HtmlEncode(s.Origin.Kind)}</td><td>{WebUtility.HtmlEncode(s.Origin.Source + " " + s.Origin.LocalUrl)}</td></tr>");
        html.Append("</table><h2>Imported passage history (including removed material)</h2>");
        foreach (var p in audit.Passages) { var surviving = audit.Spans().Where(s => s.Origin.PassageId == p.Id).Sum(s => s.Length); html.Append($"<details><summary>{WebUtility.HtmlEncode(p.Origin.Source)}: {p.Characters} imported units, {surviving} surviving</summary><p>{WebUtility.HtmlEncode(p.Origin.LocalUrl)}</p><pre>{WebUtility.HtmlEncode(p.OriginalText)}</pre></details>"); }
        html.Append("<p>Counts/positions are UTF-16 units. This local report is not a server-signed receipt. Unobserved and pre-existing text remain unknown. Internal copy/paste and complex edits may lose precise source lineage.</p>");
        File.WriteAllText(d.FileName, html.ToString()); status.Text = "Detailed report saved locally. No content was uploaded.";
    }
}


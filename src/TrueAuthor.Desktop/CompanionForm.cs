using System.Diagnostics;

namespace TrueAuthor.Desktop;

/// <summary>Small tray companion; document review lives on the website.</summary>
sealed class CompanionForm : Form
{
    readonly AuditConnection connection = new(Environment.GetEnvironmentVariable("TRUEAUTHOR_SERVER_URL") ?? "https://trueauthor.io");
    readonly string installationId = DeviceIdentity.InstallationId();
    readonly CancellationTokenSource lifetime = new();
    readonly Label state = new() { AutoSize = false, Height = 26, Dock = DockStyle.Top };
    readonly Label message = new() { Height = 44, Dock = DockStyle.Top, ForeColor = Color.FromArgb(100, 116, 139) };
    readonly Button connect;
    readonly Button pause;
    readonly Func<bool> capturing;
    readonly Action<bool> setPaused;
    bool linking;
    CancellationTokenSource? linkAttempt;
    readonly Label projectLabel = new() { Dock = DockStyle.Top, Height = 48, AutoEllipsis = true, Padding = new Padding(8), BackColor = Color.FromArgb(238, 244, 241), Font = new Font("Segoe UI", 13, FontStyle.Bold), Text = "No project selected" };
    readonly Func<WritingProject?, Task> startProject;
    readonly Func<string?> reportPath;
    readonly Func<List<WritingProject>> browserProjects;
    WritingProject? currentProject;
    public CompanionForm(Func<bool> capturing, Action<bool> setPaused, Action openEditor, Action openActivity, Func<WritingProject?, Task> startProject, Func<string?> reportPath, Func<List<WritingProject>> browserProjects)
    {
        Icon = Branding.AppIcon;
        this.capturing = capturing; this.setPaused = setPaused; this.startProject = startProject; this.reportPath = reportPath; this.browserProjects = browserProjects;
        Text = "True Author"; ClientSize = new Size(364, 470); FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 10); StartPosition = FormStartPosition.Manual;
        var stack = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
        var title = new Label { Text = "true author.", Font = new Font("Segoe UI", 23, FontStyle.Bold), Height = 50, Dock = DockStyle.Top, ForeColor = Color.FromArgb(30, 41, 59) };
        var subtitle = new Label { Text = "Your private writing companion", Height = 26, Dock = DockStyle.Top, ForeColor = Color.FromArgb(100, 116, 139) };
        Button Make(string text, Action action, bool primary = false)
        {
            var b = new CompanionButton { Text = text, Height = 42, Dock = DockStyle.Top, FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(47, 93, 80) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(30, 41, 59), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240); b.Click += (_, _) => action(); return b;
        }
        connect = Make("Link account (optional)", () => { if (linking) linkAttempt?.Cancel(); else _ = Link(true); });
        pause = Make("Pause capture", () => { setPaused(capturing()); RefreshState(); });
        var website = Make("Open in browser ↗", OpenBrowser, true);
        var newProject = Make("＋ New project", PickProject);
        var advanced = Make("More ···", () => { var menu = new ContextMenuStrip(); menu.Items.Add("Test writing editor", null, (_, _) => openEditor()); menu.Items.Add("Local activity", null, (_, _) => openActivity()); menu.Items.Add("Copy device ID", null, (_, _) => { Clipboard.SetText(installationId); SetNotice("Device ID copied."); }); menu.Items.Add("Cloud audits", null, (_, _) => Process.Start(new ProcessStartInfo(new Uri(connection.Origin, reportPath() ?? "/audits").AbsoluteUri) { UseShellExecute = true })); menu.Show(this, new Point(24, 400)); });
        var privacy = new Label { Text = "Works without an account · stored on this PC", Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 12, 0, 0), ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 9) };
        foreach (var control in new Control[] { privacy, advanced, pause, newProject, website, connect, message, projectLabel, state, subtitle, title }) stack.Controls.Add(control);
        Controls.Add(stack); RefreshState();
        var close = new Button { Text = "×", FlatStyle = FlatStyle.Flat, Size = new Size(32, 28), Location = new Point(ClientSize.Width - 40, 8), Anchor = AnchorStyles.Top | AnchorStyles.Right, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 14), TabStop = false };
        close.FlatAppearance.BorderSize = 0; close.Click += (_, _) => Hide(); Controls.Add(close); close.BringToFront();
        KeyPreview = true; KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    public void Reveal() { var area = Screen.PrimaryScreen!.WorkingArea; Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16); Show(); Activate(); RefreshState(); }
    public object Status() => new { deviceId = installationId, project = currentProject, report = reportPath(), server = connection.Origin.AbsoluteUri, linked = connection.Linked, linking, capturing = capturing(), message = message.Text };
    public void SetNotice(string text) { message.Text = text; RefreshState(); }
    void OpenBrowser() { Process.Start(new ProcessStartInfo((linking && connection.LinkUrl is not null ? connection.LinkUrl : new Uri(LocalApi.BrowserUrl)).AbsoluteUri) { UseShellExecute = true }); Hide(); }
    async void PickProject()
    {
        using var picker = new Form { Text = "Start a project", ClientSize = new Size(440, 320), StartPosition = FormStartPosition.CenterParent, Font = Font, BackColor = BackColor, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var items = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = Font };
        var hint = new Label { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(12), Text = "Choose an open document. Only the selected project is monitored." };
        var start = new Button { Dock = DockStyle.Bottom, Height = 44, Text = "Start project", DialogResult = DialogResult.OK };
        items.Items.Add(new WritingProject("reference", "New writing project", "True Author editor", "Detailed edit capture"));
        items.Items.Add(new WritingProject("automatic", "Follow my next document", "Automatic", "Attach to the next focused supported writing surface"));
        try { foreach (var project in browserProjects().Concat(WritingProjects.Discover()).DistinctBy(p => p.Title + p.App)) items.Items.Add(project); }
        catch (Exception error) { hint.Text = error.Message; }
        items.SelectedIndex = 0;
        items.SelectedIndexChanged += (_, _) => hint.Text = (items.SelectedItem as WritingProject)?.Capability;
        picker.Controls.Add(items); picker.Controls.Add(hint); picker.Controls.Add(start); picker.AcceptButton = start;
        if (picker.ShowDialog(this) != DialogResult.OK || items.SelectedItem is not WritingProject chosen) return;
        await ChooseProject(chosen);
    }
    async Task ChooseProject(WritingProject chosen)
    {
        try { await startProject(chosen.Id == "reference" ? null : chosen); currentProject = chosen; projectLabel.Text = chosen.Title; message.Text = chosen.Capability; }
        catch (Exception error) { message.Text = error.Message; }
        finally { RefreshState(); }
    }
    public void SetProject(WritingProject chosen) { currentProject = chosen; projectLabel.Text = chosen.Title; SetNotice(chosen.Capability); }
    public void RefreshState() { state.Text = connection.Linked ? (capturing() ? "● Connected · capture active" : "● Connected · capture paused") : linking ? "◌ Waiting for browser confirmation" : (capturing() ? "● Local capture active" : "● Local capture paused"); state.ForeColor = Color.FromArgb(47, 93, 80); pause.Text = capturing() ? "Pause capture" : "Resume capture"; connect.Text = connection.Linked ? "Account connected" : linking ? "Cancel connection" : "Link account (optional)"; connect.Enabled = !connection.Linked; message.Visible = message.Text.Length > 0; ClientSize = new Size(364, message.Visible ? 514 : 470); }
    public async Task Link(bool openBrowser)
    {
        if (linking || connection.Linked) return; linking = true; message.Text = "Opening a secure connection…"; RefreshState();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        linkAttempt = attempt;
        try { await connection.Link(text => { message.Text = text; RefreshState(); }, attempt.Token, openBrowser); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) message.Text = "Connection cancelled. Connect again when ready."; }
        catch (Exception error) { message.Text = error.Message; }
        finally { linkAttempt = null; linking = false; if (!IsDisposed) RefreshState(); }
    }
    public object Control(string command)
    {
        if (command.StartsWith("start-project:"))
        {
            var id = command[14..];
            var chosen = id == "reference" ? new WritingProject("reference", "New writing project", "True Author editor", "Detailed edit capture") : id == "automatic" ? new WritingProject("automatic", "Follow my next document", "Automatic", "Waiting for the next supported writing surface") : browserProjects().Concat(WritingProjects.Discover()).FirstOrDefault(p => p.Id == id);
            if (chosen is null) throw new ArgumentException("This document is no longer available.");
            _ = ChooseProject(chosen); return Status();
        }
        switch (command)
        {
            case "show": Reveal(); break;
            case "hide": Hide(); break;
            case "pause": setPaused(true); break;
            case "resume": setPaused(false); break;
            case "connect": _ = Link(false); break;
            case "projects": return browserProjects().Concat(WritingProjects.Discover()).DistinctBy(p => p.Title + p.App).ToList();
            case "new-project": PickProject(); break;
            case "open-browser": OpenBrowser(); break;
            default: throw new ArgumentException("Unknown companion command.");
        }
        RefreshState(); return Status();
    }
    protected override void Dispose(bool disposing) { if (disposing && !IsDisposed) { lifetime.Cancel(); lifetime.Dispose(); connection.Dispose(); } base.Dispose(disposing); }
}

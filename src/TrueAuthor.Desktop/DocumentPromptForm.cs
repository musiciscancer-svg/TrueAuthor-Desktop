namespace TrueAuthor.Desktop;

/// <summary>A non-modal prompt that does not take keyboard focus away from the writing program.</summary>
sealed class DocumentPromptForm : Form
{
    bool answered;
    readonly Action<bool> answer;
    readonly System.Windows.Forms.Timer dismissTimer = new() { Interval = 30000 };
    protected override bool ShowWithoutActivation => true;
    public DocumentPromptForm(WritingProject project, Action<bool> answer)
    {
        Icon = Branding.AppIcon;
        this.answer = answer;
        Text = "True Author · document detected"; ClientSize = new Size(370, 220);
        FormBorderStyle = FormBorderStyle.FixedSingle; ControlBox = false;
        ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(248, 250, 248); Font = new Font("Segoe UI", 10);
        var title = new Label { Text = "Start tracking this document?", Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(20, 18), Size = new Size(330, 30), ForeColor = Color.FromArgb(31, 61, 47) };
        var document = new Label { Text = project.Title, AutoEllipsis = true, Location = new Point(20, 59), Size = new Size(330, 32), Font = new Font("Segoe UI", 11, FontStyle.Bold) };
        var detail = new Label { Text = project.App + " · Save a local writing audit.\nCapture depends on what this editor exposes.", Location = new Point(20, 94), Size = new Size(330, 48), ForeColor = Color.FromArgb(100, 116, 105) };
        var start = new CompanionButton { Text = "Start tracking", Location = new Point(20, 160), Size = new Size(190, 40), BackColor = Color.FromArgb(47, 93, 80), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var dismiss = new CompanionButton { Text = "Not now", Location = new Point(222, 160), Size = new Size(128, 40), BackColor = Color.White, FlatStyle = FlatStyle.Flat };
        start.Click += (_, _) => Answer(true); dismiss.Click += (_, _) => Answer(false);
        Controls.AddRange([title, document, detail, start, dismiss]);
        var area = Screen.PrimaryScreen!.WorkingArea; Location = new Point(area.Right - Width - 18, area.Bottom - Height - 18);
        dismissTimer.Tick += (_, _) => Answer(false); Shown += (_, _) => dismissTimer.Start();
        FormClosed += (_, _) => { dismissTimer.Stop(); if (!answered) { answered = true; answer(false); } };
    }
    void Answer(bool accepted) { if (answered) return; answered = true; Close(); answer(accepted); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) dismissTimer.Dispose();
        base.Dispose(disposing);
    }
}

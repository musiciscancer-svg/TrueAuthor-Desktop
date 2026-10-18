using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

/// <summary>Shows the writer everything the app currently holds. Nothing here is uploaded in this prototype.</summary>
sealed class ActivityForm : Form
{
    readonly ClipboardLedger ledger;
    readonly ActivityLog log;
    readonly ListView copies = MakeList(("Time", 70), ("Source", 150), ("Type", 90), ("Found via", 100), ("Chars", 55), ("Excerpt (stays on this PC)", 330));
    readonly ListView events = MakeList(("Time", 70), ("Event", 70), ("Detail", 655));

    public ActivityForm(ClipboardLedger ledger, ActivityLog log)
    {
        Icon = Branding.AppIcon;
        this.ledger = ledger;
        this.log = log;
        Text = "True Author: what this app holds";
        Width = 840;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        var note = new Label
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(8),
            Text = "Recent copies are kept in memory for 30 minutes so pastes into tracked documents can be traced to their source. " +
                   "Only a hash and the source details are shared with the browser extension. Nothing is uploaded by this prototype.",
        };
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 270 };
        split.Panel1.Controls.Add(copies);
        split.Panel1.Controls.Add(new Label { Text = "Recent copies", Dock = DockStyle.Top, Height = 20, Font = new Font(Font, FontStyle.Bold) });
        split.Panel2.Controls.Add(events);
        split.Panel2.Controls.Add(new Label { Text = "Activity", Dock = DockStyle.Top, Height = 20, Font = new Font(Font, FontStyle.Bold) });
        Controls.Add(split);
        Controls.Add(note);
        ledger.Changed += Refresh;
        log.Changed += Refresh;
        FormClosed += (_, _) => { ledger.Changed -= Refresh; log.Changed -= Refresh; };
        Load += (_, _) => Refresh();
    }

    static ListView MakeList(params (string Name, int Width)[] columns)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        foreach (var (name, width) in columns) list.Columns.Add(name, width);
        return list;
    }

    new void Refresh()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(Refresh); return; }
        copies.BeginUpdate();
        copies.Items.Clear();
        foreach (var entry in ledger.Snapshot().Reverse())
            copies.Items.Add(new ListViewItem([entry.CapturedAt.ToLocalTime().ToString("HH:mm:ss"), entry.Source.Name, entry.Source.Category, entry.Source.Detection, entry.Length.ToString(), entry.Excerpt ?? ""]));
        copies.EndUpdate();
        events.BeginUpdate();
        events.Items.Clear();
        foreach (var item in log.Snapshot().Reverse())
            events.Items.Add(new ListViewItem([item.At.ToString("HH:mm:ss"), item.Kind, item.Detail]));
        events.EndUpdate();
    }
}

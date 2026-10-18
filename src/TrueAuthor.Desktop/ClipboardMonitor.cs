using System.Runtime.InteropServices;
using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

/// <summary>
/// Listens for clipboard changes with AddClipboardFormatListener (no keyboard hooks) and records where each
/// text copy came from. The copied text is hashed and discarded; only a short excerpt stays in memory for the
/// writer's own activity window. Copies marked private by password managers, and copies from sensitive
/// apps or sites, are dropped before anything is kept.
/// </summary>
sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    static readonly IntPtr HWND_MESSAGE = new(-3);
    readonly ClipboardLedger ledger;
    readonly ActivityLog log;
    readonly uint ownProcess = (uint)Environment.ProcessId;
    public bool Paused { get; set; }
    public event Action? ClipboardChanged;
    public event Action<string, SourceInfo>? CopyCaptured;

    public ClipboardMonitor(ClipboardLedger ledger, ActivityLog log)
    {
        this.ledger = ledger;
        this.log = log;
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        if (!Native.AddClipboardFormatListener(Handle)) throw new InvalidOperationException($"Clipboard listener failed ({Marshal.GetLastWin32Error()})");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) ClipboardChanged?.Invoke();
        if (m.Msg == Native.WM_CLIPBOARDUPDATE && !Paused)
        {
            try { Capture(); }
            catch (Exception error) { log.Add("error", $"Clipboard read failed: {error.Message}"); }
        }
        base.WndProc(ref m);
    }

    static T Retry<T>(Func<T> read)
    {
        // Another process may still hold the clipboard open right after it changes.
        for (var attempt = 0; ; attempt++)
        {
            try { return read(); }
            catch (ExternalException) when (attempt < 4) { Thread.Sleep(40); }
        }
    }

    static bool OptedOut(IDataObject data)
    {
        var formats = data.GetFormats(false);
        if (SourceClassifier.ClipboardExclusionFormats.Any(formats.Contains)) return true;
        foreach (var format in SourceClassifier.ClipboardHistoryOptOutFormats.Where(formats.Contains))
            if (data.GetData(format) is MemoryStream { Length: >= 4 } stream && BitConverter.ToInt32(stream.ToArray(), 0) == 0) return true;
        return false;
    }

    void Capture()
    {
        var owner = Native.GetClipboardOwner();
        var foreground = Native.GetForegroundWindow();
        var ownerPid = Native.ProcessIdOf(owner);
        var foregroundPid = Native.ProcessIdOf(foreground);
        var pid = ownerPid != 0 ? ownerPid : foregroundPid;
        if (pid == ownProcess) return;

        var data = Retry(Clipboard.GetDataObject);
        if (data is null) return;
        if (OptedOut(data)) { log.Add("skipped", "A copy was marked private by the app that made it (for example a password manager)."); return; }
        if (!data.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = data.GetData(DataFormats.UnicodeText) as string;
        if (string.IsNullOrWhiteSpace(text)) return;
        var cfHtml = data.GetDataPresent(DataFormats.Html) ? data.GetData(DataFormats.Html) as string : null;

        var processName = Native.ProcessName(pid);
        // The clipboard owner is often a hidden window; the visible title comes from the foreground window when it
        // belongs to the same process, otherwise from the process's main window.
        var title = foregroundPid == pid ? Native.WindowTitle(foreground) : Native.MainWindowTitle(pid);

        switch (SourceClassifier.ResolveCopySource(processName, title, cfHtml))
        {
            case Classification.Blocked blocked:
                log.Add("skipped", $"Copy from a sensitive {(blocked.Reason == "sensitive-site" ? "site" : "app or window")} ignored.");
                break;
            case Classification.Allowed { Source: var source }:
                var entry = ledger.Record(SourceClassifier.HashText(text), text.Length, source, "clipboard", ClipboardLedger.MakeExcerpt(text));
                CopyCaptured?.Invoke(text, source);
                log.Add("copy", $"{text.Length} characters from {entry.Source.Name} ({entry.Source.Category}, via {entry.Source.Detection})");
                break;
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) Native.RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}

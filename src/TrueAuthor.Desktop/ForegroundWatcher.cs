using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

/// <summary>
/// Notices when the writer switches to a supported editor (Word, a Google Docs tab) using WinEvent
/// notifications for foreground and title changes. It reads window titles only, never window contents.
/// </summary>
sealed class ForegroundWatcher : IDisposable
{
    readonly Native.WinEventProc callback;
    readonly IntPtr foregroundHook, titleHook;
    WritingSurface? current;

    public event Action<WritingSurface, string>? SurfaceEntered;

    public ForegroundWatcher()
    {
        callback = OnEvent; // Keep the delegate alive for the lifetime of the hooks.
        foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        titleHook = Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Check(Native.GetForegroundWindow());
    }

    void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != 0) return;
        if (eventType == Native.EVENT_OBJECT_NAMECHANGE && hwnd != Native.GetForegroundWindow()) return;
        Check(hwnd);
    }

    void Check(IntPtr hwnd)
    {
        var process = Native.ProcessName(Native.ProcessIdOf(hwnd));
        var surface = SourceClassifier.DetectWritingSurface(process, Native.WindowTitle(hwnd));
        if (surface is not null && surface != current) SurfaceEntered?.Invoke(surface, process ?? "");
        current = surface;
    }

    public void Dispose()
    {
        if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
        if (titleHook != IntPtr.Zero) Native.UnhookWinEvent(titleHook);
    }
}

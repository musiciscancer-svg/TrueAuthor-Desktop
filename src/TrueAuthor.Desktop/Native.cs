using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TrueAuthor.Desktop;

static partial class Native
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_NAMECHANGE = 0x800C, WINEVENT_OUTOFCONTEXT = 0;
    public const int OBJID_WINDOW = 0;

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetClipboardOwner();

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(IntPtr hook);

    public static string WindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var buffer = new StringBuilder(512);
        return GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>Executable file name, for example "chrome.exe", or null when the process has exited or is protected.</summary>
    public static string? ProcessName(uint pid)
    {
        if (pid == 0) return null;
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName + ".exe"; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public static string MainWindowTitle(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return process.MainWindowTitle; }
        catch (ArgumentException) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }
}

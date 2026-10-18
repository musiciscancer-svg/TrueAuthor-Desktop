using System.Runtime.InteropServices;
using System.Text;
namespace TrueAuthor.Desktop;

/// <summary>Observes approved foreground writing only. Text translation is evidence, not a claim of document insertion.</summary>
sealed class ProjectInputMonitor : IDisposable
{
    delegate IntPtr KeyboardCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct KeyboardData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int hook, KeyboardCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    readonly KeyboardCallback callback;
    IntPtr hook;
    readonly Thread worker;
    uint workerId;
    readonly Func<bool> selected;
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int length, uint flags, IntPtr layout);
    readonly Action<string, int, string?, uint> input;
    string? Translate(KeyboardData key)
    {
        var state = new byte[256];
        foreach (var modifier in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }) if (GetAsyncKeyState(modifier) < 0) state[modifier] = 0x80;
        foreach (var toggle in new[] { 0x14, 0x90, 0x91 }) state[toggle] = (byte)(GetKeyState(toggle) & 1);
        state[key.Key] = 0x80;
        var window = Native.GetForegroundWindow();
        var thread = Native.GetWindowThreadProcessId(window, out _);
        var text = new StringBuilder(16);
        // Flag 4 leaves the target keyboard's dead-key state unchanged.
        var length = ToUnicodeEx(key.Key, key.Scan, state, text, text.Capacity, 4, GetKeyboardLayout(thread));
        return length > 0 ? text.ToString().Replace("\r", "\n") : null;
    }
    public ProjectInputMonitor(Func<bool> selected, Action<string, int, string?, uint> input)
    {
        this.selected = selected; this.input = input; callback = Observe;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = new Thread(() =>
        {
            workerId = GetCurrentThreadId();
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { started.SetException(new InvalidOperationException("Selected-document input capture is unavailable.")); return; }
            using var context = new ApplicationContext();
            started.SetResult();
            try { Application.Run(context); }
            finally { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }) { IsBackground = true, Name = "True Author selected-document input" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); started.Task.GetAwaiter().GetResult();
    }
    IntPtr Observe(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && message.ToInt64() is 0x100 or 0x104 && selected())
            {
                var key = Marshal.PtrToStructure<KeyboardData>(data);
                bool ctrl = GetAsyncKeyState(0x11) < 0, shift = GetAsyncKeyState(0x10) < 0, alt = GetAsyncKeyState(0x12) < 0;
                var sequence = Native.GetClipboardSequenceNumber();
                if ((key.Flags & 0x10) != 0) input("uncertain", 0, null, sequence);
                else if ((ctrl && key.Key == 0x56) || (shift && key.Key == 0x2D)) input("paste", 0, null, sequence);
                else if (key.Key == 0x08) input("backspace", 0, null, sequence);
                else if (key.Key == 0x2E) input("delete", 0, null, sequence);
                else if (ctrl && key.Key is 0x58 or 0x5A or 0x59) input(key.Key == 0x58 ? "cut" : key.Key == 0x5A ? "undo" : "redo", 0, null, sequence);
                else if ((!ctrl && !alt) || (ctrl && GetAsyncKeyState(0xA5) < 0))
                {
                    var text = Translate(key);
                    if (!string.IsNullOrEmpty(text) && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) input("typing", text.Length, text, sequence);
                    else if (key.Key is not (0x10 or 0x11 or 0x12 or 0x14 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)) input("uncertain", 0, null, sequence);
                }
                else if (!(ctrl && key.Key is 0x43 or 0x11 or 0x10)) input("uncertain", 0, null, sequence);
            }
        }
        catch { /* Observation must never interfere with the user's input. */ }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose() { if (workerId != 0) PostThreadMessage(workerId, 0x12, IntPtr.Zero, IntPtr.Zero); if (worker.IsAlive) worker.Join(1000); }
}

using System.Runtime.InteropServices;
namespace TrueAuthor.Desktop;

/// <summary>Observes input intent only while the selected Word document is foreground. Never stores keys.</summary>
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
    readonly Action<string, int> input;
    public ProjectInputMonitor(Func<bool> selected, Action<string, int> input)
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
                if ((key.Flags & 0x10) != 0) input("uncertain", 0);
                else if ((ctrl && key.Key == 0x56) || (shift && key.Key == 0x2D)) input("paste", 0);
                else if (ctrl && key.Key is 0x58 or 0x5A or 0x59) input("uncertain", 0);
                else if (!ctrl && !alt && (key.Key is >= 0x30 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xE2 or 0x20 or 0x0D or 0x09)) input("typing", 1);
                else if (key.Key is 0x08 or 0x2E or 0xE5 || (ctrl && key.Key is not (0x43 or 0x11 or 0x10))) input("uncertain", 0);
                else if (key.Key is >= 0x21 and <= 0x28) input("uncertain", 0);
            }
        }
        catch { /* Observation must never interfere with the user's input. */ }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose() { if (workerId != 0) PostThreadMessage(workerId, 0x12, IntPtr.Zero, IntPtr.Zero); if (worker.IsAlive) worker.Join(1000); }
}

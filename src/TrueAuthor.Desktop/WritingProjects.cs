using System.Runtime.InteropServices;
using TrueAuthor.Core;
namespace TrueAuthor.Desktop;
sealed record WritingProject(string Id, string Title, string App, string Capability)
{
    public override string ToString() => $"{Title}  ·  {App}";
}
static class WritingProjects
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] static extern int CLSIDFromProgID(string id, out Guid clsid);
    [DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object app);
    delegate bool WindowCallback(IntPtr window, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    static object Word() { Marshal.ThrowExceptionForHR(CLSIDFromProgID("Word.Application", out var id)); Marshal.ThrowExceptionForHR(GetActiveObject(ref id, IntPtr.Zero, out var app)); return app; }
    public static List<WritingProject> Discover()
    {
        var result = new List<WritingProject>(); object? app = null;
        try
        {
            app = Word(); dynamic word = app;
            foreach (dynamic document in word.Documents)
            {
                try { result.Add(new((string)document.FullName, (string)document.Name, "Microsoft Word", "Snapshot audit · changes marked unobserved")); }
                finally { Marshal.ReleaseComObject(document); }
            }
        }
        catch (COMException) { }
        finally { if (app is not null) Marshal.ReleaseComObject(app); }
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var surface = SourceClassifier.DetectWritingSurface(Native.ProcessName(Native.ProcessIdOf(window)), Native.WindowTitle(window));
            if (surface?.Name == "Google Docs") result.Add(new("docs-window:" + window, surface.DocumentTitle, "Google Docs", "Desktop capture · extension optional for precise mapping"));
            var process = Native.ProcessName(Native.ProcessIdOf(window)) ?? "";
            var title = Native.WindowTitle(window);
            if (SourceClassifier.ClassifyWindow(process, title) is not Classification.Blocked) result.AddRange(AccessibleWriting.Discover(window, process, title));
            return true;
        }, IntPtr.Zero);
        return result.DistinctBy(p => p.Id).ToList();
    }
    public static string ReadWord(string id)
    {
        if (id.StartsWith("uia:")) return AccessibleWriting.Read(id);
        var app = Word();
        try
        {
            dynamic word = app;
            foreach (dynamic document in word.Documents)
            {
                try
                {
                    if ((string)document.FullName != id) continue;
                    object range = document.Content;
                    try { return ((string)((dynamic)range).Text).Replace("\r", "\n"); }
                    finally { Marshal.ReleaseComObject(range); }
                }
                finally { Marshal.ReleaseComObject(document); }
            }
            throw new InvalidOperationException("Selected Word document is closed. Reopen it to continue.");
        }
        finally { Marshal.ReleaseComObject(app); }
    }
    public static bool ActiveWord(string id)
    {
        object? app = null, document = null;
        try { app = Word(); document = ((dynamic)app).ActiveDocument; return string.Equals((string)((dynamic)document).FullName, id, StringComparison.OrdinalIgnoreCase); }
        catch (COMException) { return false; }
        finally { if (document is not null) Marshal.ReleaseComObject(document); if (app is not null) Marshal.ReleaseComObject(app); }
    }
}

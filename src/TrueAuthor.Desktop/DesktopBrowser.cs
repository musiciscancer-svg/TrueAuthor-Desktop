using System.Windows.Automation;
using System.Text.RegularExpressions;
namespace TrueAuthor.Desktop;

sealed record BrowserSample(string Id, string Title, IntPtr Window, bool EditorFocused, string? Text);
/// <summary>Read-only Windows accessibility fallback. No browser debugging port, cookies or injected scripts.</summary>
static class DesktopBrowser
{
    public static string? DocumentId(string address)
    {
        if (!Uri.TryCreate(address.StartsWith("https://") ? address : "https://" + address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "docs.google.com" || uri.UserInfo.Length > 0) return null;
        var match = Regex.Match(uri.AbsolutePath, @"^/document/(?:u/\d+/)?d/([A-Za-z0-9_-]+)(?:/|$)");
        return match.Success ? match.Groups[1].Value : null;
    }
    public static BrowserSample? ReadForeground() => ReadWindow(Native.GetForegroundWindow(), false);
    public static BrowserSample? ReadWindow(IntPtr window, bool readText = true)
    {
        var process = Native.ProcessName(Native.ProcessIdOf(window));
        if (process is not ("chrome.exe" or "msedge.exe" or "brave.exe")) return null;
        var surface = TrueAuthor.Core.SourceClassifier.DetectWritingSurface(process, Native.WindowTitle(window));
        if (surface?.Name != "Google Docs") return null;
        try
        {
            var root = AutomationElement.FromHandle(window);
            var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            string? id = null;
            foreach (AutomationElement edit in edits)
            {
                if (edit.Current.IsPassword || edit.Current.IsOffscreen) continue;
                if (edit.Current.Name is not ("Address and search bar" or "Search or enter address" or "Address bar")) continue;
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) id = DocumentId(((ValuePattern)value).Current.Value);
                if (id is not null) break;
            }
            if (id is null) return null; // Never merge projects by title alone.
            var focus = AutomationElement.FocusedElement;
            bool focused = false, belongs = false; string? text = null; AutomationElement? editor = null;
            for (int depth = 0; focus is not null && depth < 24; depth++, focus = TreeWalker.ControlViewWalker.GetParent(focus))
            {
                if (focus.Current.IsPassword) break;
                if (focus.GetRuntimeId().SequenceEqual(root.GetRuntimeId())) { belongs = true; break; }
                // Do not read address bars, comments, searches, dialogs or the entire browser page.
                if (focus.Current.Name is not ("Document content" or "Document text" or "Document editor" or "Text Editor")) continue;
                editor ??= focus;
            }
            if (belongs && editor is not null) { focused = true; if (readText && editor.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) text = ((TextPattern)pattern).DocumentRange.GetText(1_000_001); }
            return new(id, surface.DocumentTitle, window, focused, text);
        }
        catch (Exception error) when (error is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }
}

using System.Windows.Automation;
namespace TrueAuthor.Desktop;

/// <summary>Read-only adapter for explicitly selected desktop editor controls.</summary>
static class AccessibleWriting
{
    static readonly string[] Apps = ["notepad.exe", "wordpad.exe", "soffice.bin", "soffice.bin.exe", "soffice.exe", "scrivener.exe", "typora.exe", "obsidian.exe", "notion.exe"];
    static string Key(AutomationElement element) => string.Join(',', element.GetRuntimeId());
    public static IReadOnlyList<WritingProject> Discover(IntPtr window, string process, string title)
    {
        if (!Apps.Contains(process.ToLowerInvariant())) return [];
        var result = new List<WritingProject>();
        try
        {
            var root = AutomationElement.FromHandle(window);
            var controls = root.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
            foreach (AutomationElement control in controls)
            {
                if (control.Current.IsPassword || !control.Current.IsEnabled || control.Current.IsOffscreen) continue;
                if (!control.TryGetCurrentPattern(TextPattern.Pattern, out _) && !control.TryGetCurrentPattern(ValuePattern.Pattern, out _)) continue;
                var name = control.Current.Name;
                if (name.Contains("search", StringComparison.OrdinalIgnoreCase) || name.Contains("find", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new("uia:" + window.ToInt64() + ":" + Key(control), title + (name.Length == 0 ? "" : " · " + name), process, "Accessible editor · input and revision capture"));
            }
        }
        catch (ElementNotAvailableException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return result;
    }
    static (IntPtr Window, string RuntimeId) Parse(string id)
    {
        var parts = id.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "uia" || !long.TryParse(parts[1], out var window)) throw new ArgumentException("Invalid editor identity.");
        return (new IntPtr(window), parts[2]);
    }
    static AutomationElement Selected(string id)
    {
        var parsed = Parse(id);
        var root = AutomationElement.FromHandle(parsed.Window);
        var controls = root.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        foreach (AutomationElement control in controls) if (Key(control) == parsed.RuntimeId && !control.Current.IsPassword) return control;
        throw new InvalidOperationException("The selected editor closed or its document control changed. Select it again.");
    }
    public static string Read(string id)
    {
        var element = Selected(id);
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(1_000_001);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        throw new InvalidOperationException("The editor no longer exposes readable document text.");
    }
    public static bool Focused(string id)
    {
        try
        {
            var parsed = Parse(id);
            if (Native.GetForegroundWindow() != parsed.Window) return false;
            var focus = AutomationElement.FocusedElement;
            if (focus is null || focus.Current.IsPassword) return false;
            for (var depth = 0; focus is not null && depth < 12; depth++, focus = TreeWalker.ControlViewWalker.GetParent(focus)) if (Key(focus) == parsed.RuntimeId) return true;
        }
        catch (ElementNotAvailableException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return false;
    }
}

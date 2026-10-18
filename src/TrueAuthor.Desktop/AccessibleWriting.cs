using System.Windows.Automation;
using TrueAuthor.Core;
namespace TrueAuthor.Desktop;

/// <summary>Discovers writable controls in the foreground app; recording still requires project approval.</summary>
static class AccessibleWriting
{
    static readonly string[] KnownEditors = ["notepad.exe", "wordpad.exe", "soffice.bin", "soffice.bin.exe", "soffice.exe", "scrivener.exe", "typora.exe", "obsidian.exe", "notion.exe"];
    static readonly string[] Browsers = ["chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe", "vivaldi.exe"];
    static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IReadOnlyList<WritingProject>> recent = new();
    static string Key(AutomationElement element) => string.Join(',', element.GetRuntimeId());
    public static bool ExcludedName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
        @"password|passcode|security code|verification code|credit card|card number|address and search|address bar|search or enter|\bsearch\b|\bfind\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static bool Writable(AutomationElement control, bool known)
    {
        var c = control.Current;
        if (c.IsPassword || !c.IsEnabled || c.IsOffscreen || ExcludedName(c.Name)) return false;
        if (c.ControlType != ControlType.Edit && c.ControlType != ControlType.Document) return false;
        if (control.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return !((ValuePattern)value).Current.IsReadOnly;
        if (!control.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return false;
        var readOnly = ((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
        return readOnly is bool b ? !b : known;
    }
    public static string? BrowserAddress(AutomationElement root)
    {
        var controls = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        foreach (AutomationElement control in controls)
        {
            if (control.Current.IsPassword || control.Current.IsOffscreen) continue;
            var name = control.Current.Name;
            if (name is not ("Address and search bar" or "Search or enter address" or "Address bar" or "Search with Google or enter address") && control.Current.AutomationId is not ("addressEditBox" or "urlbar-input")) continue;
            if (!control.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) continue;
            var address = ((ValuePattern)pattern).Current.Value;
            if (!address.Contains("://")) address = "https://" + address;
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && SourceClassifier.ClassifyUrl(uri.AbsoluteUri) is not Classification.Blocked) return uri.AbsoluteUri;
        }
        return null;
    }
    public static bool CurrentIsAutomaticSource()
    {
        try
        {
            var window = Native.GetForegroundWindow();
            var process = Native.ProcessName(Native.ProcessIdOf(window)) ?? "";
            var title = Native.WindowTitle(window);
            if (WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyWindow(process, title))) return true;
            if (!Browsers.Contains(process.ToLowerInvariant())) return false;
            var address = BrowserAddress(AutomationElement.FromHandle(window));
            return address is not null && WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyUrl(address, title));
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }
    public static IReadOnlyList<WritingProject> Discover(IntPtr window, string process, string title)
    {
        process = process.ToLowerInvariant();
        if (process == "trueauthor.exe" || process == "winword.exe" || SourceClassifier.ClassifyWindow(process, title) is Classification.Blocked) return [];
        if (WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyWindow(process, title))) { recent.TryRemove(window, out _); return []; }
        if (window != Native.GetForegroundWindow()) return recent.GetValueOrDefault(window) ?? [];
        var result = new List<WritingProject>();
        try
        {
            var root = AutomationElement.FromHandle(window);
            var browser = Browsers.Contains(process);
            var address = browser ? BrowserAddress(root) : null;
            if (browser && address is null) return [];
            if (address is not null && WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyUrl(address, title))) { recent.TryRemove(window, out _); return []; }
            // A browser's Document element can contain the whole webpage. Only the focused writable field is eligible.
            var focus = AutomationElement.FocusedElement;
            var candidates = new List<AutomationElement>();
            bool belongs = false;
            for (int depth = 0; focus is not null && depth < 24; depth++, focus = TreeWalker.ControlViewWalker.GetParent(focus))
            {
                if (focus.Current.IsPassword || ExcludedName(focus.Current.Name)) return [];
                if (focus.GetRuntimeId().SequenceEqual(root.GetRuntimeId())) { belongs = true; break; }
                if (focus.Current.ControlType == ControlType.ToolBar || focus.Current.ControlType == ControlType.Menu) return [];
                if (candidates.Count == 0 && Writable(focus, KnownEditors.Contains(process))) candidates.Add(focus);
            }
            if (!belongs) return [];
            if (candidates.Count == 0 && !browser && KnownEditors.Contains(process))
            {
                var controls = root.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                foreach (AutomationElement control in controls) if (Writable(control, true)) candidates.Add(control);
            }
            foreach (var control in candidates)
            {
                var name = control.Current.Name;
                var context = address is null ? "" : ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(address)))[..24];
                result.Add(new("uia:" + window.ToInt64() + ":" + Key(control) + context, title + (name.Length == 0 ? "" : " · " + name), process, "Desktop capture · focused writing field · no extension required"));
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        if (recent.Count > 128) recent.Clear();
        recent[window] = result;
        return result;
    }
    static (IntPtr Window, string RuntimeId, string? Page) Parse(string id)
    {
        var parts = id.Split(':', 4);
        if (parts.Length < 3 || parts[0] != "uia" || !long.TryParse(parts[1], out var window)) throw new ArgumentException("Invalid editor identity.");
        return (new IntPtr(window), parts[2], parts.Length == 4 ? parts[3] : null);
    }
    static AutomationElement Selected(string id)
    {
        var parsed = Parse(id);
        var root = AutomationElement.FromHandle(parsed.Window);
        if (parsed.Page is not null)
        {
            var address = BrowserAddress(root);
            if (address is null || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(address)))[..24] != parsed.Page) throw new InvalidOperationException("The browser page changed. Select the writing field again.");
        }
        var controls = root.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        foreach (AutomationElement control in controls) if (Key(control) == parsed.RuntimeId && Writable(control, parsed.Page is null)) return control;
        throw new InvalidOperationException("The selected writing field closed or changed. Select it again.");
    }
    public static bool Valid(string id)
    {
        try { _ = Selected(id); return true; }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }
    public static string Read(string id)
    {
        if (!Focused(id)) throw new InvalidOperationException("Capture is waiting for the selected writing field.");
        var element = Selected(id);
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(1_000_001);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        throw new InvalidOperationException("The editor no longer exposes readable document text.");
    }
    public static bool SafeForegroundInput(IntPtr window)
    {
        try
        {
            if (window != Native.GetForegroundWindow()) return false;
            var focus = AutomationElement.FocusedElement;
            if (focus is null || focus.Current.ProcessId != Native.ProcessIdOf(window)) return false;
            for (int depth = 0; focus is not null && depth < 12; depth++, focus = TreeWalker.ControlViewWalker.GetParent(focus))
            {
                if (focus.Current.IsPassword || ExcludedName(focus.Current.Name) || focus.Current.ControlType == ControlType.Menu || focus.Current.ControlType == ControlType.ToolBar) return false;
                if (focus.Current.NativeWindowHandle == window.ToInt64()) return true;
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return false;
    }
    public static bool Focused(string id)
    {
        try
        {
            var parsed = Parse(id);
            if (Native.GetForegroundWindow() != parsed.Window) return false;
            var focus = AutomationElement.FocusedElement;
            if (focus is null || focus.Current.IsPassword || ExcludedName(focus.Current.Name)) return false;
            if (parsed.Page is not null) _ = Selected(id);
            for (var depth = 0; focus is not null && depth < 24; depth++, focus = TreeWalker.ControlViewWalker.GetParent(focus)) if (focus.Current.IsPassword || ExcludedName(focus.Current.Name)) return false; else if (Key(focus) == parsed.RuntimeId) return true;
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return false;
    }
}

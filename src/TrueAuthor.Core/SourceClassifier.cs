using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrueAuthor.Core;

/// <summary>
/// Port of packages/source-map/src/index.ts. Both read known-sources.json and are checked against the same
/// fixtures (source-detection.json, hash-vectors.json), so a copy is classified identically on every client.
/// </summary>
public static class SourceClassifier
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    sealed record Entry(string Key, string Category, string Name, Regex? Pattern = null);

    static readonly JsonElement Known = LoadKnown();
    static readonly Entry[] Domains = Known.GetProperty("domains").EnumerateArray()
        .Select(e => new Entry(e.GetProperty("domain").GetString()!, e.GetProperty("category").GetString()!, e.GetProperty("name").GetString()!))
        .OrderByDescending(e => e.Key.Length).ToArray();
    static readonly Dictionary<string, Entry> Processes = Known.GetProperty("processes").EnumerateArray()
        .Select(e => new Entry(e.GetProperty("process").GetString()!, e.GetProperty("category").GetString()!, e.GetProperty("name").GetString()!))
        .ToDictionary(e => e.Key);
    static readonly HashSet<string> Browsers = Strings("browsers").ToHashSet();
    static readonly Regex BrowserSuffix = new(Known.GetProperty("browserTitleSuffix").GetString()!, Options);
    static readonly Regex BrowserNoise = new(Known.GetProperty("browserTitleNoise").GetString()!, Options);
    static readonly Regex ProfileSegment = new(" (?:-|—|–) [^-—–]+$", Options);
    static readonly Entry[] Titles = Known.GetProperty("windowTitles").EnumerateArray()
        .Select(e => new Entry(e.GetProperty("pattern").GetString()!, e.GetProperty("category").GetString()!, e.GetProperty("name").GetString()!, new Regex(e.GetProperty("pattern").GetString()!, Options))).ToArray();
    static readonly JsonElement Blocked = Known.GetProperty("blocked");
    static readonly string[] BlockedDomains = Blocked.GetProperty("domains").EnumerateArray().Select(e => e.GetString()!).ToArray();
    static readonly Regex[] BlockedHosts = Blocked.GetProperty("hostPatterns").EnumerateArray().Select(e => new Regex(e.GetString()!, Options)).ToArray();
    static readonly Regex[] BlockedTitles = Blocked.GetProperty("titlePatterns").EnumerateArray().Select(e => new Regex(e.GetString()!, Options)).ToArray();
    static readonly HashSet<string> BlockedProcesses = Blocked.GetProperty("processes").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
    static readonly (string Process, string Name, Regex Suffix)[] SurfaceProcesses = Known.GetProperty("writingSurfaces").GetProperty("processes").EnumerateArray()
        .Select(e => (e.GetProperty("process").GetString()!, e.GetProperty("name").GetString()!, new Regex(e.GetProperty("titleSuffix").GetString()!, Options))).ToArray();
    static readonly (string Name, Regex Pattern)[] SurfaceTitles = Known.GetProperty("writingSurfaces").GetProperty("browserTitles").EnumerateArray()
        .Select(e => (e.GetProperty("name").GetString()!, new Regex(e.GetProperty("pattern").GetString()!, Options))).ToArray();

    public static IReadOnlyList<string> ClipboardExclusionFormats { get; } = Strings("clipboardExclusionFormats");
    public static IReadOnlyList<string> ClipboardHistoryOptOutFormats { get; } = Strings("clipboardHistoryOptOutFormats");

    static JsonElement LoadKnown()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("known-sources.json") ?? throw new InvalidOperationException("known-sources.json resource missing");
        return JsonDocument.Parse(stream).RootElement.Clone();
    }
    static string[] Strings(string property) => Known.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    static readonly Regex LineEndings = new("\r\n?"), Invisible = new("[\uFEFF\u200B]");
    static readonly char[] Trimmed = [' ', '\t', '\n', '\v', '\f'];

    public static string NormalizeText(string text) => LineEndings.Replace(Invisible.Replace(text, ""), "\n").Replace('\u00A0', ' ').Trim(Trimmed);

    public static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeText(text))));

    static string? HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
    static bool HostMatches(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    public static bool IsBlockedHost(string host) => BlockedDomains.Any(d => HostMatches(host, d)) || BlockedHosts.Any(r => r.IsMatch(host));

    public static Classification ClassifyUrl(string url, string? title = null)
    {
        var host = HostOf(url);
        if (host is null) return new Classification.Allowed(SourceInfo.Unknown);
        if (IsBlockedHost(host)) return new Classification.Blocked("sensitive-site");
        var match = Domains.FirstOrDefault(e => HostMatches(host, e.Key));
        return new Classification.Allowed(new SourceInfo { Category = match?.Category ?? "web", Name = match?.Name ?? host, Detection = "extension", Confidence = "high", Url = url, Domain = host, Title = title });
    }

    public static string StripBrowserTitle(string title) => BrowserNoise.Replace(BrowserSuffix.Replace(title, ""), "", 1).Trim();

    static Entry? MatchTitle(string title)
    {
        var direct = Titles.FirstOrDefault(e => e.Pattern!.IsMatch(title));
        if (direct is not null) return direct;
        var withoutProfile = ProfileSegment.Replace(title, "");
        return withoutProfile != title ? Titles.FirstOrDefault(e => e.Pattern!.IsMatch(withoutProfile)) : null;
    }

    static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    public static Classification ClassifyWindow(string? processName, string? rawTitle)
    {
        var process = (processName ?? "").ToLowerInvariant();
        if (process.Length > 0 && BlockedProcesses.Contains(process)) return new Classification.Blocked("sensitive-app");
        var isBrowser = Browsers.Contains(process);
        var title = isBrowser ? StripBrowserTitle(rawTitle ?? "") : (rawTitle ?? "").Trim();
        if (title.Length > 0 && BlockedTitles.Any(r => r.IsMatch(title))) return new Classification.Blocked("sensitive-window");
        var app = NullIfEmpty(process);
        var byTitle = title.Length > 0 ? MatchTitle(title) : null;
        if (byTitle is not null && (isBrowser || !Processes.ContainsKey(process)))
            return new Classification.Allowed(new SourceInfo { Category = byTitle.Category, Name = byTitle.Name, Detection = "window-title", Confidence = isBrowser ? "medium" : "low", Title = title, App = app });
        if (isBrowser) return new Classification.Allowed(new SourceInfo { Category = "web", Name = "Web page", Detection = "window-title", Confidence = "low", Title = NullIfEmpty(title), App = app });
        if (Processes.TryGetValue(process, out var byProcess))
            return new Classification.Allowed(new SourceInfo { Category = byProcess.Category, Name = byProcess.Name, Detection = "process", Confidence = "medium", Title = NullIfEmpty(title), App = app });
        if (process.Length > 0)
            return new Classification.Allowed(new SourceInfo { Category = "app", Name = process.EndsWith(".exe", StringComparison.Ordinal) ? process[..^4] : process, Detection = "process", Confidence = "low", Title = NullIfEmpty(title), App = app });
        return new Classification.Allowed(SourceInfo.Unknown);
    }

    /// <summary>Reads the SourceURL header Chromium browsers write into the "HTML Format" clipboard entry.</summary>
    public static string? ParseCfHtmlSourceUrl(string cfHtml)
    {
        var tagStart = cfHtml.IndexOf('<');
        var header = tagStart > 0 ? cfHtml[..tagStart] : cfHtml;
        var line = header.Split(["\r\n", "\r", "\n"], StringSplitOptions.None).FirstOrDefault(l => l.StartsWith("SourceURL:", StringComparison.OrdinalIgnoreCase));
        var url = line?["SourceURL:".Length..].Trim();
        return url is not null && HostOf(url) is not null ? url : null;
    }

    public static Classification ResolveCopySource(string? processName, string? title, string? cfHtml, string? extensionUrl = null, string? extensionTitle = null)
    {
        var window = ClassifyWindow(processName, title);
        if (window is not Classification.Allowed { Source: var windowSource }) return window;
        if (!string.IsNullOrEmpty(extensionUrl))
        {
            var result = ClassifyUrl(extensionUrl, extensionTitle);
            return result is Classification.Allowed allowed && windowSource.App is not null ? new Classification.Allowed(allowed.Source with { App = windowSource.App }) : result;
        }
        var sourceUrl = cfHtml is null ? null : ParseCfHtmlSourceUrl(cfHtml);
        if (sourceUrl is not null)
        {
            var result = ClassifyUrl(sourceUrl, windowSource.Title);
            return result is Classification.Allowed allowed
                ? new Classification.Allowed(allowed.Source with { Detection = "clipboard-html", Confidence = "high", App = windowSource.App ?? allowed.Source.App })
                : result;
        }
        return window;
    }

    public static WritingSurface? DetectWritingSurface(string? processName, string? title)
    {
        var process = (processName ?? "").ToLowerInvariant();
        title ??= "";
        foreach (var surface in SurfaceProcesses)
            if (surface.Process == process) return new WritingSurface(surface.Name, surface.Suffix.Replace(title, "").Trim());
        if (!Browsers.Contains(process)) return null;
        var stripped = StripBrowserTitle(title);
        foreach (var surface in SurfaceTitles)
            if (surface.Pattern.IsMatch(stripped)) return new WritingSurface(surface.Name, surface.Pattern.Replace(stripped, "").Trim());
        return null;
    }
}

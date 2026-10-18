using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

/// <summary>
/// Local connection capability, exchanged after desktop approval. Stored in the user's local
/// app-data folder, readable only by their account.
/// </summary>
static class Pairing
{
    public static string TokenPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueAuthor", "pairing-token");

    public static string LoadOrCreate()
    {
        if (File.Exists(TokenPath))
        {
            var existing = File.ReadAllText(TokenPath).Trim();
            if (existing.Length >= 32) return existing;
        }
        return Reset();
    }

    public static string Reset()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(TokenPath, token);
        return token;
    }
}

/// <summary>
/// Loopback-only API for the browser extension. Selected document snapshots stay on this PC. Requests must
/// carry the pairing token, target the loopback host name (blocks DNS rebinding) and, when a browser sends an
/// Origin, come from an extension rather than a web page.
/// </summary>
sealed partial class LocalApi(ClipboardLedger ledger, ActivityLog log, Func<bool> capturing, Func<string> token, Func<string?, Task<object>>? control = null, Func<LocalApi.DocsReport, Task<object>>? docs = null, Func<string?>? browserDocument = null) : IAsyncDisposable
{
    public Func<string, Task<bool>>? ApproveConnection { get; init; }
    readonly SemaphoreSlim connectionGate = new(1, 1);
    public string ConnectionsPath { get; init; } = Pairing.TokenPath + ".connections.json";
    bool ApprovedConnection(string origin, string currentToken)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConnectionsPath))?.GetValueOrDefault(origin) == currentToken; }
        catch { return false; }
    }
    void RememberConnection(string origin, string currentToken)
    {
        Dictionary<string, string> approved;
        try { approved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConnectionsPath)) ?? []; }
        catch { approved = []; }
        approved[origin] = currentToken;
        File.WriteAllText(ConnectionsPath, System.Text.Json.JsonSerializer.Serialize(approved));
    }
    public const int Port = 47821;
    // Separate, process-scoped capability: never expose the extension's control token in a URL.
    static readonly string browserPath = "/local/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string BrowserUrl => $"http://127.0.0.1:{Port}{browserPath}";
    WebApplication? app;

    sealed record CopyReport(string? Hash, int Length, SourceInfo? Source);
    sealed record ResolveRequest(string? Hash, int Length);
    sealed record ControlRequest(string? Command, string? ProjectId = null);
    public sealed record DocsReport(string Id, string Title, string? Text, string? PasteText = null, SourceInfo? Source = null, int Typed = 0, bool Uncertain = false, string App = "Google Docs", bool Active = false, string? CopyText = null);
    public static bool ShouldFollow(DocsReport report, string? selected, bool awaiting, bool paused, bool busy) =>
        !paused && !busy && report.Active && (awaiting || report.App == "Google Docs" && selected != report.Id);

    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex HashPattern();

    static bool TokenMatches(string? header, string expected)
    {
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..].Trim()), Encoding.UTF8.GetBytes(expected));
    }

    static bool ExtensionOrigin(string origin) => origin.StartsWith("chrome-extension://", StringComparison.Ordinal) || origin.StartsWith("moz-extension://", StringComparison.Ordinal);

    public async Task StartAsync(int port = Port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
        });
        app = builder.Build();

        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Host.Host is not ("127.0.0.1" or "localhost")) { context.Response.StatusCode = 421; return; }
            if (HttpMethods.IsGet(request.Method) && request.Path == browserPath)
            {
                if (request.Headers.Origin.Count > 0 || request.Headers["Sec-Fetch-Site"] == "cross-site") { context.Response.StatusCode = 403; return; }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                var scriptHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(LocalDashboard.LiveScript)));
                context.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; style-src 'unsafe-inline'; img-src data:; script-src 'sha256-{scriptHash}'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await next(); return;
            }
            var origin = request.Headers.Origin.ToString();
            if (origin.Length > 0)
            {
                if (!ExtensionOrigin(origin)) { context.Response.StatusCode = 403; return; }
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
            }
            if (HttpMethods.IsOptions(request.Method))
            {
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, X-TrueAuthor-Connect";
                context.Response.Headers.AccessControlAllowMethods = "GET, POST";
                context.Response.StatusCode = 204;
                return;
            }
            if (HttpMethods.IsPost(request.Method) && request.Path == "/v1/connect")
            {
                // A browser-controlled extension origin and a custom header are mandatory.
                // Never release a capability to a website or an origin-less request.
                if (!Regex.IsMatch(origin, "^chrome-extension://[a-p]{32}$") || request.Headers["X-TrueAuthor-Connect"] != "1") { context.Response.StatusCode = 403; return; }
                context.Response.Headers.CacheControl = "no-store";
                await next(); return;
            }
            if (!TokenMatches(request.Headers.Authorization, token())) { context.Response.StatusCode = 401; return; }
            await next();
        });

        app.MapGet("/v1/status", () => Results.Json(new { app = "True Author Desktop", version = Program.Version, capturing = capturing(), selectedDocument = browserDocument?.Invoke(), ledgerSize = ledger.Count }, SourceInfo.Json));
        app.MapPost("/v1/connect", async (HttpContext context) =>
        {
            if (!await connectionGate.WaitAsync(0)) return Results.StatusCode(429);
            try
            {
                var origin = context.Request.Headers.Origin.ToString();
                var currentToken = token();
                if (!ApprovedConnection(origin, currentToken))
                {
                    if (ApproveConnection is null || !await ApproveConnection(origin).WaitAsync(TimeSpan.FromSeconds(50))) return Results.StatusCode(403);
                    if (currentToken != token()) return Results.StatusCode(409);
                    RememberConnection(origin, currentToken);
                }
                return Results.Json(new { app = "True Author Desktop", token = currentToken }, SourceInfo.Json);
            }
            catch (TimeoutException) { return Results.StatusCode(408); }
            finally { connectionGate.Release(); }
        });
        app.MapGet(browserPath, (HttpContext context) =>
        {
            var document = context.Request.Query["document"].ToString();
            if (context.Request.Query["export"] == "inputs")
            {
                if (!Guid.TryParse(document, out var id) || !File.Exists(AuditLocalStore.PathFor(id))) return Results.NotFound();
                try { return Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(InputJournal.Read(id), SourceInfo.Json), "application/json", "TrueAuthor-input-history-" + id + ".json"); }
                catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException) { return Results.Problem("The encrypted input history could not be read. Its file has been kept."); }
            }
            return Results.Content(LocalDashboard.Render(browserPath, document), "text/html; charset=utf-8");
        });
        app.MapPost("/v1/projects/docs", async (DocsReport report) =>
        {
            if (!Regex.IsMatch(report.Id ?? "", "^[A-Za-z0-9_-]{1,200}$") || report.Title is null || report.Title.Length > 512 || report.Text?.Length > 1_000_000 || report.PasteText?.Length > 1_000_000 || report.CopyText?.Length > 1_000_000 || report.Typed is < 0 or > 1_000_000 || report.App is not ("Google Docs" or "Browser editor")) return Results.BadRequest();
            return docs is null ? Results.StatusCode(503) : Results.Json(await docs(report), SourceInfo.Json);
        });

        // Control is for local tools only: extension origins cannot operate the app.
        app.MapGet("/v1/companion", async (HttpContext context) =>
        {
            if (context.Request.Headers.Origin.Count > 0) return Results.StatusCode(403);
            return control is null ? Results.StatusCode(503) : Results.Json(await control(null), SourceInfo.Json);
        });
        app.MapPost("/v1/companion", async (HttpContext context, ControlRequest request) =>
        {
            if (context.Request.Headers.Origin.Count > 0) return Results.StatusCode(403);
            if (request.Command == "start-project")
            {
                if (string.IsNullOrEmpty(request.ProjectId) || request.ProjectId.Length > 2048) return Results.BadRequest();
                return control is null ? Results.StatusCode(503) : Results.Json(await control("start-project:" + request.ProjectId), SourceInfo.Json);
            }
            if (request.Command is not ("show" or "hide" or "pause" or "resume" or "connect" or "projects" or "new-project" or "open-browser" or "preview-metadata")) return Results.BadRequest();
            return control is null ? Results.StatusCode(503) : Results.Json(await control(request.Command), SourceInfo.Json);
        });

        app.MapPost("/v1/copies", (CopyReport report) =>
        {
            if (!capturing()) return Results.Json(new { accepted = false }, SourceInfo.Json);
            var source = SourceInfo.Sanitize(report.Source);
            if (report.Hash is null || !HashPattern().IsMatch(report.Hash) || source is null || report.Length is < 1 or > 5_000_000) return Results.BadRequest();
            if (source.Url is not null && SourceClassifier.ClassifyUrl(source.Url) is Classification.Blocked) return Results.Json(new { accepted = false }, SourceInfo.Json);
            var entry = ledger.Record(report.Hash, report.Length, source, "extension");
            log.Add("copy", $"Browser extension reported {report.Length} characters from {entry.Source.Name}");
            return Results.Json(new { accepted = true }, SourceInfo.Json);
        });

        app.MapPost("/v1/pastes/resolve", (ResolveRequest request) =>
        {
            if (!capturing()) return Results.Json(new { match = (object?)null }, SourceInfo.Json);
            if (request.Hash is null || !HashPattern().IsMatch(request.Hash)) return Results.BadRequest();
            var entry = ledger.Find(request.Hash);
            log.Add("paste", entry is null ? "A paste in a tracked document had no matching recent copy." : $"Paste matched a copy from {entry.Source.Name}");
            return Results.Json(new { match = entry is null ? null : new { source = entry.Source, capturedAt = entry.CapturedAt, via = entry.Via } }, SourceInfo.Json);
        });

        await app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
    }
}

using System.Text;
using TrueAuthor.Core;

namespace TrueAuthor.Desktop;

static class AuditStorageTest
{
    public static int Run()
    {
        var directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tmp", "audit-storage-test-" + Guid.NewGuid()));
        var id = Guid.NewGuid(); var path = Path.Combine(directory, id + ".taudit");
        try
        {
            var doc = new DocumentAudit(); doc.Update("PRIVATE SAMPLE", new("typed", "none", "keyboard"));
            var promptPolicy = new DocumentPromptPolicy(); var promptTime = DateTimeOffset.UtcNow;
            if (promptPolicy.Offer("docs:a", true, false, promptTime) || promptPolicy.Offer("docs:a", false, true, promptTime)) throw new InvalidOperationException("A paused or busy app offered tracking.");
            if (!promptPolicy.Offer("docs:a", false, false, promptTime) || promptPolicy.Offer("docs:b", false, false, promptTime.AddSeconds(30))) throw new InvalidOperationException("Prompt visibility did not suppress competing offers.");
            promptPolicy.Close();
            if (promptPolicy.Offer("docs:a", false, false, promptTime.AddMinutes(1)) || promptPolicy.Offer("docs:b", false, false, promptTime.AddSeconds(10))) throw new InvalidOperationException("Prompt dismissal or cooldown allowed notification spam.");
            if (!promptPolicy.Offer("docs:b", false, false, promptTime.AddSeconds(21))) throw new InvalidOperationException("A different document was never offered.");
            promptPolicy.Approve("docs:b");
            if (!promptPolicy.Approved("docs:b") || promptPolicy.Offer("docs:b", false, false, promptTime.AddMinutes(2))) throw new InvalidOperationException("An approved document was prompted again.");
            Console.WriteLine("Document prompts: pause, busy state, duplicate detection, competing offers, cooldown and approval passed.");
            var saved = new SavedAudit(id, DateTimeOffset.UtcNow, "http://localhost:3000", 0, new('0', 64), true, doc.Snapshot(), ["{\"safeCounts\":3}"]);
            AuditLocalStore.Save(saved, directory);
            if (Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("PRIVATE SAMPLE")) throw new InvalidOperationException("Recovery is not encrypted.");
            var restored = AuditLocalStore.Load(path);
            if (restored.Document.Text != doc.Text || restored.Pending.Single() != saved.Pending.Single() || restored.DocumentId != id || !restored.Paused) throw new InvalidOperationException("Recovery did not preserve the audit and retry queue.");
            if (DocumentAudit.Restore(restored.Document).Spans().Count != 1) throw new InvalidOperationException("Recovered lineage changed.");
            AuditLocalStore.Save(saved with { Sequence = 1, Pending = [] }, directory);
            if (AuditLocalStore.Load(path).Sequence != 1) throw new InvalidOperationException("Atomic replacement failed.");
            if (DeviceIdentity.InstallationId() != DeviceIdentity.InstallationId()) throw new InvalidOperationException("Installation identity changed between loads.");
            if (DesktopBrowser.DocumentId("https://docs.google.com/document/d/unique-doc/edit") != "unique-doc" || DesktopBrowser.DocumentId("docs.google.com/document/u/1/d/unique-doc/edit") != "unique-doc" || DesktopBrowser.DocumentId("https://docs.google.com.evil.example/document/d/unique-doc/edit") is not null || DesktopBrowser.DocumentId("https://user@docs.google.com/document/d/unique-doc/edit") is not null) throw new InvalidOperationException("Desktop browser identity accepted an unrelated site.");
            var focused = new LocalApi.DocsReport("unique-doc", "My document", null, Active: true);
            if (!LocalApi.ShouldFollow(focused, null, false, false, false) || !LocalApi.ShouldFollow(focused, "different-doc", false, false, false) || LocalApi.ShouldFollow(focused, "unique-doc", false, false, false) || LocalApi.ShouldFollow(focused, null, false, true, false) || LocalApi.ShouldFollow(focused with { Active = false }, null, false, false, false) || LocalApi.ShouldFollow(focused with { App = "Browser editor" }, null, false, false, false)) throw new InvalidOperationException("Automatic Docs selection ignored focus, identity or pause boundaries.");
            doc.Update("<script>PRIVATE SAMPLE</script>", new("typed", "none", "keyboard"));
            AuditLocalStore.Save(saved with { Document = doc.Snapshot() }, directory);
            var dashboard = LocalDashboard.Render("/local/test", id.ToString(), directory);
            if (dashboard.Contains("<script>PRIVATE") || !dashboard.Contains("&lt;script&gt;") || !dashboard.Contains("typed units")) throw new InvalidOperationException("Local report did not escape document content or display counters.");
            var sourceDoc = new DocumentAudit();
            sourceDoc.Update("My introduction.\n", new("typed", "none", "keyboard"));
            sourceDoc.Update(sourceDoc.Text + "Research excerpt.\n", new("paste", "web", "extension", "Research site", "example.org", "research", "https://example.org/article?a=1&b=2"));
            sourceDoc.Update(sourceDoc.Text + "An AI draft.\n", new("paste", "ai", "extension", "ChatGPT", "chatgpt.com", "ai-draft", "https://chatgpt.com/"));
            sourceDoc.Update(sourceDoc.Text.Replace("An AI draft.\n", ""), new("typed", "none", "keyboard"));
            var second = saved with { DocumentId = Guid.NewGuid(), Document = sourceDoc.Snapshot(), Title = "Source mapping fixture <test>", ExternalId = "docs:unique-doc-fixture" };
            AuditLocalStore.Save(second, directory);
            var observed = second with { CaptureMode = "desktop-only", Observations = new(7, 1, 0, [new(DateTimeOffset.UtcNow, "paste", 4, "TEST", SourceInfo.Unknown)]) };
            AuditLocalStore.Save(observed, directory);
            var observedHtml = LocalDashboard.Render("/local/test", second.DocumentId.ToString(), directory);
            if (!observedHtml.Contains("7 typing-key observations") || !observedHtml.Contains("not confirmed document insertions") || AuditLocalStore.Load(Path.Combine(directory, second.DocumentId + ".taudit")).Observations?.Recent.Single().Text != "TEST") throw new InvalidOperationException("Desktop-only observations were lost or presented as proven insertions.");
            AuditLocalStore.Save(second, directory);
            if (AuditLocalStore.FindExternal(second.ExternalId, directory)?.DocumentId != second.DocumentId || AuditLocalStore.FindExternal("docs:different-doc", directory) is not null) throw new InvalidOperationException("Docs identity lookup mixed distinct documents.");
            AuditLocalStore.Save(second with { Title = "Renamed document" }, directory);
            if (AuditLocalStore.FindExternal(second.ExternalId, directory)?.DocumentId != second.DocumentId) throw new InvalidOperationException("Renaming a Docs document changed its project identity.");
            AuditLocalStore.Save(second, directory);
            var library = LocalDashboard.Render("/local/test", "", directory);
            var detail = LocalDashboard.Render("/local/test", second.DocumentId.ToString(), directory);
            if (!library.Contains(id.ToString()) || !library.Contains(second.DocumentId.ToString()) || !library.Contains("Source mapping fixture &lt;test&gt;")) throw new InvalidOperationException("Document library omitted a saved document or escaped title.");
            if (!detail.Contains("href='#source-") || !detail.Contains("Research excerpt.") || !detail.Contains("An AI draft.") || !detail.Contains("removed from current text") || !detail.Contains("article?a=1&amp;b=2")) throw new InvalidOperationException("Full source map, URL escaping or removed paste history failed.");
            Directory.CreateDirectory("outputs");
            File.WriteAllText("outputs/document-library-fictional-preview.html", detail.Replace("Source mapping fixture &lt;test&gt;", "Fictional source mapping preview"));
            File.Delete(Path.Combine(directory, second.DocumentId + ".taudit"));
            Console.WriteLine("Document library: multiple documents, full preview, source navigation, removed pastes and escaped titles passed.");
            TestBrowserBoundary().GetAwaiter().GetResult();
            Console.WriteLine("Desktop storage: Windows encryption, document recovery, retry recovery and atomic save passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }
    static async Task TestBrowserBoundary()
    {
        var connectionFile = Path.Combine(Environment.CurrentDirectory, "tmp", "connection-test-" + Guid.NewGuid() + ".json");
        var approvals = 0; var allow = false; var token = "test-private-token";
        await using var api = new LocalApi(new ClipboardLedger(), new ActivityLog(), () => true, () => token) { ConnectionsPath = connectionFile, ApproveConnection = _ => { approvals++; return Task.FromResult(allow); } };
        await api.StartAsync(47822);
        using var client = new System.Net.Http.HttpClient();
        var url = new UriBuilder(LocalApi.BrowserUrl) { Port = 47822 }.Uri;
        using var page = await client.GetAsync(url);
            if (!page.IsSuccessStatusCode || page.Headers.CacheControl?.NoStore != true || !page.Headers.GetValues("Content-Security-Policy").Single().Contains("script-src 'sha256-")) throw new InvalidOperationException("Local dashboard access or live-script privacy headers failed.");
        using var anonymous = await client.GetAsync(new Uri(url, "/v1/companion"));
        if (anonymous.StatusCode != System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("Local dashboard weakened control authentication.");
        using var wrong = await client.GetAsync(new Uri(url, "/local/wrong"));
        if (wrong.StatusCode != System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("Invalid browser capability accepted.");
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await client.SendAsync(request);
        if (crossSite.StatusCode != System.Net.HttpStatusCode.Forbidden) throw new InvalidOperationException("Cross-site local report access accepted.");
        async Task<System.Net.Http.HttpResponseMessage> Connect(string? origin, bool header = true)
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, new Uri(url, "/v1/connect"));
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (header) request.Headers.Add("X-TrueAuthor-Connect", "1");
            return await client.SendAsync(request);
        }
        try
        {
            foreach (var origin in new string?[] { null, "https://example.com", "chrome-extension://invalid" })
            {
                using var rejected = await Connect(origin);
                if (rejected.StatusCode != System.Net.HttpStatusCode.Forbidden) throw new InvalidOperationException("Untrusted discovery origin accepted.");
            }
            var extension = "chrome-extension://" + new string('a', 32);
            using var missingHeader = await Connect(extension, false);
            if (missingHeader.StatusCode != System.Net.HttpStatusCode.Forbidden || approvals != 0) throw new InvalidOperationException("Untrusted discovery prompted for approval.");
            using var denied = await Connect(extension);
            if (denied.StatusCode != System.Net.HttpStatusCode.Forbidden || File.Exists(connectionFile)) throw new InvalidOperationException("Declined connection was stored.");
            allow = true;
            using var accepted = await Connect(extension);
            if (!accepted.IsSuccessStatusCode || !(await accepted.Content.ReadAsStringAsync()).Contains(token) || accepted.Headers.CacheControl?.NoStore != true) throw new InvalidOperationException("Approved connection failed.");
            using var remembered = await Connect(extension);
            if (!remembered.IsSuccessStatusCode || approvals != 2) throw new InvalidOperationException("Remembered connection prompted again.");
            token = "reset-private-token"; allow = false;
            using var reset = await Connect(extension);
            if (reset.StatusCode != System.Net.HttpStatusCode.Forbidden || approvals != 3) throw new InvalidOperationException("Reset did not revoke remembered connection.");
        }
        finally { if (File.Exists(connectionFile)) File.Delete(connectionFile); }
        Console.WriteLine("Automatic connection: origin checks, desktop approval, persistence and reset revocation passed.");
        Console.WriteLine("Local dashboard: stable installation identity, HTML escaping, private navigation and control isolation passed.");
    }
}

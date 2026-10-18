// Dependency-free test runner: checks the C# port against the same fixtures as the TypeScript packages,
// so both clients classify sources and hash text identically. Exit code 1 on any failure.
using System.Text.Json;
using TrueAuthor.Core;

var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "source-map"));
if (args.Length > 0) fixtures = args[0];
int passed = 0, failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) passed++; else { failed++; Console.WriteLine($"FAIL {name} {detail}"); }
}

static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

foreach (var testCase in JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "source-detection.json"))).RootElement.EnumerateArray())
{
    var name = Str(testCase, "name")!;
    var input = testCase.GetProperty("input");
    var expect = testCase.GetProperty("expect");
    var kind = Str(input, "kind");
    if (kind == "surface")
    {
        var surface = SourceClassifier.DetectWritingSurface(Str(input, "processName"), Str(input, "title"));
        var ok = expect.ValueKind == JsonValueKind.Null ? surface is null : surface is not null && surface.Name == Str(expect, "name") && surface.DocumentTitle == Str(expect, "documentTitle");
        Check(name, ok, $"got {surface}");
        continue;
    }
    string? extensionUrl = null, extensionTitle = null;
    if (input.TryGetProperty("extension", out var extension) && extension.ValueKind == JsonValueKind.Object) { extensionUrl = Str(extension, "url"); extensionTitle = Str(extension, "title"); }
    var result = kind switch
    {
        "url" => SourceClassifier.ClassifyUrl(Str(input, "url")!, Str(input, "title")),
        "window" => SourceClassifier.ClassifyWindow(Str(input, "processName"), Str(input, "title")),
        "copy" => SourceClassifier.ResolveCopySource(Str(input, "processName"), Str(input, "title"), Str(input, "cfHtml"), extensionUrl, extensionTitle),
        _ => throw new InvalidOperationException($"Unknown fixture kind {kind}"),
    };
    if (expect.TryGetProperty("blocked", out var blocked) && blocked.GetBoolean()) { Check(name, result is Classification.Blocked, $"got {result}"); continue; }
    if (result is not Classification.Allowed { Source: var source }) { Check(name, false, $"got {result}"); continue; }
    var json = JsonSerializer.SerializeToElement(source, SourceInfo.Json);
    var mismatches = expect.EnumerateObject().Where(p => Str(json, p.Name) != p.Value.GetString()).Select(p => $"{p.Name}={Str(json, p.Name)}").ToArray();
    Check(name, mismatches.Length == 0, string.Join(", ", mismatches));
}

foreach (var vector in JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "hash-vectors.json"))).RootElement.EnumerateArray())
{
    var input = Str(vector, "input")!;
    Check($"normalize {JsonSerializer.Serialize(input)}", SourceClassifier.NormalizeText(input) == Str(vector, "normalized"), JsonSerializer.Serialize(SourceClassifier.NormalizeText(input)));
    Check($"hash {JsonSerializer.Serialize(input)}", SourceClassifier.HashText(input) == Str(vector, "sha256"));
}

// Ledger behaviour.
var clock = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
var ledger = new ClipboardLedger(() => clock);
var web = new SourceInfo { Category = "web", Name = "Web page", Detection = "window-title", Confidence = "low", App = "chrome.exe" };
var fromExtension = new SourceInfo { Category = "ai", Name = "ChatGPT", Detection = "extension", Confidence = "high", Url = "https://chatgpt.com/c/1" };
var hash = SourceClassifier.HashText("Copied answer");
ledger.Record(hash, 13, web, "clipboard", "Copied answer");
clock += TimeSpan.FromSeconds(2);
var merged = ledger.Record(hash, 13, fromExtension, "extension");
Check("ledger upgrades a weak source with the extension's report", merged.Source.Name == "ChatGPT" && merged.Source.App == "chrome.exe" && merged.Via == "clipboard+extension" && ledger.Count == 1, merged.ToString());
var weaker = ledger.Record(hash, 13, web, "clipboard");
Check("ledger keeps the stronger source", weaker.Source.Name == "ChatGPT");
Check("ledger finds by hash", ledger.Find(hash)?.Source.Name == "ChatGPT");
clock += ClipboardLedger.Ttl + TimeSpan.FromSeconds(1);
Check("ledger expires entries", ledger.Find(hash) is null && ledger.Count == 0);
for (var i = 0; i < ClipboardLedger.MaxEntries + 5; i++) ledger.Record(SourceClassifier.HashText($"entry {i}"), 7, web, "clipboard");
Check("ledger is bounded", ledger.Count == ClipboardLedger.MaxEntries);
Check("excerpt is flattened and bounded", ClipboardLedger.MakeExcerpt("a\n\n b   c") == "a b c" && ClipboardLedger.MakeExcerpt(new string('x', 200))!.Length == ClipboardLedger.ExcerptLength + 1);

// Local API input validation.
Check("sanitize rejects unknown categories", SourceInfo.Sanitize(web with { Category = "evil" }) is null);
Check("sanitize drops non-web URLs and clips strings", SourceInfo.Sanitize(web with { Url = "javascript:alert(1)", Title = new string('t', 1000) }) is { Url: null, Title.Length: 300 });

// Device request signing: verify vectors produced by the TypeScript protocol package, then round-trip our own.
var deviceVectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "..", "protocol", "device-signature-vectors.json"))).RootElement;
var vectorKey = Str(deviceVectors, "publicKey")!;
foreach (var vector in deviceVectors.GetProperty("vectors").EnumerateArray())
{
    var canonical = DeviceSigning.CanonicalRequest(Str(vector, "method")!, Str(vector, "path")!, Str(vector, "timestamp")!, Str(vector, "body")!);
    Check($"canonical request {Str(vector, "path")}", canonical == Str(vector, "canonical"), canonical);
    Check($"verifies TypeScript signature {Str(vector, "path")}", DeviceSigning.Verify(vectorKey, canonical, Str(vector, "signature")!));
    Check($"rejects altered body {Str(vector, "path")}", !DeviceSigning.Verify(vectorKey, DeviceSigning.CanonicalRequest(Str(vector, "method")!, Str(vector, "path")!, Str(vector, "timestamp")!, Str(vector, "body")! + " "), Str(vector, "signature")!));
}
using (var deviceKey = DeviceSigning.NewKey())
{
    var headers = DeviceSigning.SignRequest(deviceKey, "0d0d0d0d-0000-4000-8000-000000000001", "POST", "/api/v2/ingest", "{}", DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000));
    Check("own signature verifies", DeviceSigning.Verify(DeviceSigning.PublicKey(deviceKey), DeviceSigning.CanonicalRequest("POST", "/api/v2/ingest", "1790000000000", "{}"), headers[DeviceSigning.SignatureHeader]));
    Check("public key is 91-byte P-256 SPKI", Convert.FromBase64String(DeviceSigning.PublicKey(deviceKey)).Length == 91);
    Check("garbage signature is rejected, not thrown", !DeviceSigning.Verify(DeviceSigning.PublicKey(deviceKey), "x", "%%%"));
}

var documentAudit = new DocumentAudit();
var typedOrigin = new AuditOrigin("typed", "none", "keyboard");
var aiOrigin = new AuditOrigin("paste", "ai", "extension", "ChatGPT", "chatgpt.com", Guid.NewGuid().ToString());
documentAudit.Update("hello", typedOrigin);
documentAudit.Update("hello AI", aiOrigin);
Check("audit preserves typed and pasted spans", documentAudit.Spans().Count == 2 && documentAudit.Spans()[1].Origin == aiOrigin);
documentAudit.Update("hello A", new("unobserved", "unknown", "none"));
Check("audit deletes imported characters without relabeling", documentAudit.Spans()[1].Length == 2 && documentAudit.Deleted == 1);
documentAudit.Update("hello Ax", new("unobserved", "unknown", "none"));
Check("audit unexplained insertion stays unobserved", documentAudit.Spans()[^1].Origin.Kind == "unobserved");
documentAudit.Gap();
Check("audit accounting and interruptions", documentAudit.Inserted - documentAudit.Deleted == documentAudit.Text.Length && documentAudit.Gaps == 1);
var repeated = new DocumentAudit();
repeated.Update("aaa", typedOrigin);
repeated.Update("aaaa", aiOrigin, 0);
Check("selection anchor preserves paste attribution in repeated text", repeated.Spans()[0].Origin == aiOrigin && repeated.Spans()[0].Length == 1 && repeated.Spans()[1].Origin == typedOrigin);
var recovered = DocumentAudit.Restore(repeated.Snapshot());
Check("saved local lineage restores exactly", recovered.Text == repeated.Text && recovered.Spans().SequenceEqual(repeated.Spans()) && recovered.Inserted == repeated.Inserted);
try { DocumentAudit.Restore(repeated.Snapshot() with { Inserted = 0 }); Check("corrupt recovery rejected", false); } catch (InvalidDataException) { Check("corrupt recovery rejected", true); }
var replaced = new DocumentAudit();
replaced.Update("same text", typedOrigin);
replaced.Update("same text", aiOrigin, 0, 9);
Check("identical selected text pasted over typing retains paste origin", replaced.Spans().Count == 1 && replaced.Spans()[0].Origin == aiOrigin && replaced.Inserted == 18 && replaced.Deleted == 9);
replaced.Update("", typedOrigin);
Check("removed passage keeps original source history locally", replaced.Passages.Count == 1 && replaced.Passages[0].Characters == 9 && replaced.Passages[0].OriginalText == "same text" && replaced.Spans().Count == 0);
Check("removed source history survives recovery", DocumentAudit.Restore(replaced.Snapshot()).Passages.Count == 1);
var inputNow = DateTimeOffset.UtcNow;
var reconciler = new InputReconciler();
reconciler.Typed(3); Check("external typing matches exact added unit count", reconciler.Resolve("", "abc", inputNow).Kind == "typed");
reconciler.Typed(2); Check("auto-completion mismatch remains unobserved", reconciler.Resolve("", "automatic", inputNow).Kind == "unobserved");
reconciler.Paste("AI TEXT", aiOrigin, inputNow); Check("external paste matches exact source event", reconciler.Resolve("draft ", "draft AI TEXT", inputNow) == aiOrigin);
reconciler.Paste("AI TEXT", aiOrigin, inputNow); reconciler.Typed(1); Check("mixed typing and paste remain ambiguous", reconciler.Resolve("", "AI TEXTx", inputNow).Kind == "unobserved");
reconciler.Paste("AI TEXT", aiOrigin, inputNow.AddSeconds(-21)); Check("stale paste cannot explain a later insertion", reconciler.Resolve("", "AI TEXT", inputNow).Kind == "unobserved");
reconciler.Typed(3); reconciler.Uncertain(); Check("undo or navigation cancels typing attribution", reconciler.Resolve("", "abc", inputNow).Kind == "unobserved");
reconciler.Typed(3); reconciler.Clear(); Check("pause clears pending input", reconciler.Resolve("", "abc", inputNow).Kind == "unobserved");
reconciler.Paste("same", aiOrigin, inputNow); reconciler.Paste("same", aiOrigin, inputNow); Check("duplicate paste intent remains ambiguous", reconciler.Resolve("", "same", inputNow).Kind == "unobserved");
var mixedAudit = new DocumentAudit();
mixedAudit.Update("existing ", new("preexisting", "unknown", "file"));
reconciler.Typed(4); reconciler.Paste("AI TEXT", aiOrigin, inputNow);
var mixedSpans = reconciler.Reconcile(mixedAudit.Text, "existing new AI TEXT", inputNow);
mixedAudit.UpdateFromInput("existing new AI TEXT", mixedSpans);
Check("mixed edit retains typing and matched source separately", mixedAudit.Spans().Count == 3 && mixedAudit.Spans()[1].Origin.Kind == "typed" && mixedAudit.Spans()[2].Origin == aiOrigin);
Check("mixed capture preserves original pasted history", mixedAudit.Passages.Single().OriginalText == "AI TEXT" && mixedAudit.Inserted - mixedAudit.Deleted == mixedAudit.Text.Length);
try { mixedAudit.UpdateFromInput("changed", [new(0, 1, typedOrigin)]); Check("incomplete external attribution rejected", false); } catch (InvalidDataException) { Check("incomplete external attribution rejected", true); }
Check("activity preserves typing and paste totals", mixedAudit.Activity.TypedInserted == 4 && mixedAudit.Activity.PasteInserted == 7 && mixedAudit.Activity.PreexistingInserted == 9 && mixedAudit.Activity.PasteEvents == 1);
mixedAudit.Update("", new("unobserved", "unknown", "none"));
Check("deleting text preserves lifetime origin counts", mixedAudit.Activity.TypedInserted == 4 && mixedAudit.Activity.PasteInserted == 7 && mixedAudit.Activity.DeleteEvents == 1 && mixedAudit.Passages.Count == 1);
var activityRecovery = DocumentAudit.Restore(mixedAudit.Snapshot());
Check("activity totals survive encrypted-recovery payload round trip", activityRecovery.Activity == mixedAudit.Activity);
var moveAudit = new DocumentAudit(); moveAudit.Update("AI TEXT", aiOrigin);
var copiedLineage = moveAudit.CopyLineage("AI TEXT")!;
Check("internal copy remembers original AI lineage", copiedLineage.Single().Origin == aiOrigin);
moveAudit.Update("", new("unobserved", "unknown", "none"));
var movedOrigin = aiOrigin with { PassageId = Guid.NewGuid().ToString(), Detection = "local-lineage" };
reconciler.Paste("AI TEXT", movedOrigin, inputNow, [copiedLineage.Single() with { Origin = movedOrigin }]);
moveAudit.UpdateFromInput("AI TEXT", reconciler.Reconcile("", "AI TEXT", inputNow));
Check("cut and paste keeps original AI source", moveAudit.Spans().Single().Origin.Category == "ai" && moveAudit.Spans().Single().Origin.Detection == "local-lineage" && moveAudit.Passages.Count == 2);
Check("internal paste history and activity remain consistent", moveAudit.Activity.PasteInserted == moveAudit.Passages.Sum(p => p.Characters) && moveAudit.Activity.PasteEvents == moveAudit.Passages.Count);
var ambiguousCopy = new DocumentAudit(); ambiguousCopy.Update("same same", typedOrigin);
Check("ambiguous internal copies are not guessed", ambiguousCopy.CopyLineage("same") is null && ambiguousCopy.CopyLineage("same", 0) is not null);
reconciler.Typed("abc"); Check("translated typing requires matching characters not equal length", reconciler.Resolve("", "xyz", inputNow).Kind == "unobserved");
reconciler.Typed("a\n\tZ"); Check("translated Enter and Tab reconcile exactly", reconciler.Resolve("", "a\n\tZ", inputNow).Kind == "typed");
reconciler.Typed("é😀"); Check("Unicode typing uses UTF16 units and exact text", reconciler.Resolve("", "é😀", inputNow).Kind == "typed");
reconciler.Typed("draft "); reconciler.Paste("AI TEXT", aiOrigin, inputNow); var exactMixed = reconciler.Reconcile("", "draft AI TEXT", inputNow);
Check("exact typed prefix and paste preserve separate origins", exactMixed.Count == 2 && exactMixed[0].Origin.Kind == "typed" && exactMixed[1].Origin == aiOrigin);
reconciler.Typed("wrong "); reconciler.Paste("AI TEXT", aiOrigin, inputNow); var mismatchMixed = reconciler.Reconcile("", "draft AI TEXT", inputNow);
Check("paste can match while unrelated equal length typing remains unknown", mismatchMixed.Count == 2 && mismatchMixed[0].Origin.Kind == "unobserved" && mismatchMixed[1].Origin == aiOrigin);
reconciler.Typed("secret"); reconciler.Clear(); Check("clearing focus discards translated text", reconciler.Resolve("", "secret", inputNow).Kind == "unobserved");
reconciler.Typed("abc"); reconciler.Uncertain(); Check("navigation cannot misattribute captured characters", reconciler.Resolve("", "abc", inputNow).Kind == "unobserved");

reconciler.Typed("late"); Check("stale translated input cannot explain a future change", reconciler.Resolve("", "late", DateTimeOffset.UtcNow.AddSeconds(21)).Kind == "unobserved");
reconciler.Uncertain(); reconciler.Paste("AI TEXT", aiOrigin, inputNow); Check("fresh paste action after menu navigation can match exact text", reconciler.Resolve("", "AI TEXT", inputNow) == aiOrigin);
reconciler.Paste("AI TEXT", aiOrigin, inputNow); reconciler.Uncertain(); Check("focus loss invalidates earlier paste intent", reconciler.Resolve("", "AI TEXT", inputNow).Kind == "unobserved");
var delayedAudit = new DocumentAudit(); delayedAudit.Update("draft ", new("preexisting", "unknown", "file"));
var beforeDelayed = delayedAudit.Snapshot() with { Passages = delayedAudit.Passages.ToArray() };
delayedAudit.Update("draft AI TEXT", new("unobserved", "unknown", "none"));
var insertedBeforeCorrection = delayedAudit.Inserted; var editsBeforeCorrection = delayedAudit.Activity.EditEvents;
reconciler.Paste("AI TEXT", aiOrigin, inputNow);
Check("delayed paste notification can explain the latest exact change", delayedAudit.AttributeRecentChange(beforeDelayed, reconciler.Reconcile(beforeDelayed.Text, delayedAudit.Text, inputNow)));
Check("delayed attribution preserves edit and insertion counters", delayedAudit.Inserted == insertedBeforeCorrection && delayedAudit.Activity.EditEvents == editsBeforeCorrection && delayedAudit.Activity.PasteInserted == 7 && delayedAudit.Passages.Count == 1);
Check("delayed paste cannot overwrite already attributed text", !delayedAudit.AttributeRecentChange(beforeDelayed, [new(6,7,aiOrigin)]));
var intervening = DocumentAudit.Restore(beforeDelayed); intervening.Update("draft AI TEXT", new("unobserved", "unknown", "none")); intervening.Update("draft AI TEXT!", new("unobserved", "unknown", "none"));
Check("delayed evidence cannot roll back intervening edits", !intervening.AttributeRecentChange(beforeDelayed, [new(6,7,aiOrigin),new(13,1,new("unobserved","unknown","none"))]));
Check("ChatGPT is an automatic source, not a document prompt", WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyUrl("https://chatgpt.com/c/sample")));
Check("Claude is an automatic source, not a document prompt", WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyUrl("https://claude.ai/chat/sample")));
Check("research pages are automatic sources", WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyUrl("https://arxiv.org/abs/example")));
Check("Google Docs remains a writing destination", !WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyUrl("https://docs.google.com/document/d/example/edit")));
Check("Word remains a writing destination", !WritingRolePolicy.IsAutomaticSource(SourceClassifier.ClassifyWindow("winword.exe", "Test - Word")));
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;




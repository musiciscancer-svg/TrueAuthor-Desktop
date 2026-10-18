using System.Net;
using System.Text;
using TrueAuthor.Core;
namespace TrueAuthor.Desktop;
static class LocalDashboard
{
 public const string LiveScript = "let live=true,loading=false;const toggle=document.getElementById('live-toggle');toggle.addEventListener('click',()=>{live=!live;toggle.textContent=live?'Live updates on':'Live updates paused';});setInterval(async()=>{if(!live||loading||document.hidden)return;loading=true;try{const r=await fetch(location.href,{cache:'no-store'});if(!r.ok)throw Error();const next=new DOMParser().parseFromString(await r.text(),'text/html').querySelector('main');const main=document.querySelector('main');if(next&&main.innerHTML!==next.innerHTML){const open=[...main.querySelectorAll('details[open]')].map(d=>d.querySelector('summary')?.textContent);const y=scrollY;main.replaceWith(next);for(const d of next.querySelectorAll('details'))if(open.includes(d.querySelector('summary')?.textContent))d.open=true;scrollTo(0,y);}toggle.textContent='Live updates on';}catch{toggle.textContent='App disconnected · retrying';}finally{loading=false;}},3000);";
 static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
 static string Key(AuditOrigin o) => o.Kind == "paste" ? string.Join('\u001f',o.Kind,o.Source,o.Host,o.LocalUrl) : o.Kind;
 static string Label(AuditOrigin o) => o.Kind switch { "typed" => "Directly typed", "preexisting" => "Already in document", "unobserved" => "Unobserved changes", _ => o.Source == "unknown" ? "Paste · unknown source" : o.Source };
 static readonly string[] Colors = ["#ffe4ad","#dcd5ff","#c7eaff","#ffd6de","#bceee0","#ecd5b9"];
 static string Color(AuditOrigin o,int i) => o.Kind switch { "typed" => "#ddf2e3", "preexisting" => "#e6e9ed", "unobserved" => "#f7ced0", _ => Colors[i % Colors.Length] };
 static string Title(SavedAudit s) => string.IsNullOrWhiteSpace(s.Title) ? "Writing project · " + s.DocumentId.ToString()[..8] : s.Title;
 public static string Render(string basePath,string document,string? directory=null)
 {
  directory ??= AuditLocalStore.DirectoryPath;
  var entries = new List<(SavedAudit saved,DocumentAudit audit,DateTime updated)>(); int unreadable=0;
  foreach(var path in (Directory.Exists(directory)?Directory.GetFiles(directory,"*.taudit"):[]).OrderByDescending(File.GetLastWriteTimeUtc))
   try { var s=AuditLocalStore.Load(path); entries.Add((s,DocumentAudit.Restore(s.Document),File.GetLastWriteTime(path))); }
   catch(Exception error) when(error is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or IOException or FormatException or ArgumentException) { unreadable++; }
  var html=new StringBuilder("<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Your documents · True Author</title><style>*{box-sizing:border-box}body{margin:0;background:#f5f7f6;color:#203c33;font:15px system-ui}a{color:#246b55;text-decoration:none}a:hover{text-decoration:underline}header{padding:24px 5%;background:white;border-bottom:1px solid #dfe7e2;display:flex;justify-content:space-between}main{max-width:1440px;margin:40px auto;padding:0 32px}h1{font-size:38px;letter-spacing:-1.3px}p,small{color:#63786e;line-height:1.7}section{background:white;border:1px solid #dfe7e2;border-radius:18px;padding:24px;margin-bottom:22px}.layout{display:grid;grid-template-columns:240px minmax(0,1fr);gap:28px}nav a{display:block;padding:14px;margin-bottom:6px;border-radius:12px;overflow-wrap:anywhere}nav a.active{background:#e2eee7}nav small{display:block}.stats{display:flex;gap:28px;flex-wrap:wrap}.stats b{display:block;font-size:27px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:18px}.pill{display:inline-block;border:1px solid #dfe7e2;border-radius:24px;padding:6px 12px;margin:4px}.swatch{display:inline-block;width:12px;height:12px;border-radius:4px;margin-right:7px}.paper{background:white;box-shadow:0 10px 40px #203c3310;border-radius:4px;padding:52px;max-width:900px;margin:26px auto;white-space:pre-wrap;overflow-wrap:anywhere;min-height:300px;font:18px/2 Georgia,serif}.paper a{color:inherit;text-decoration:none}.paper a:hover{outline:2px solid #456754}mark{color:inherit;border-radius:3px;padding:2px 0}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:14px/1.8 system-ui;background:#f6f8f7;padding:18px;border-radius:10px}summary{cursor:pointer;font-weight:600;padding:12px 0}code,dd{overflow-wrap:anywhere}dl{display:grid;grid-template-columns:150px minmax(0,1fr);gap:12px}dt{color:#63786e}dd{margin:0}.source:target{outline:2px solid #4a7c62}footer{margin:32px 0}@media(max-width:800px){main{padding:0 16px}.layout{grid-template-columns:1fr}nav{display:flex;overflow:auto}nav a{min-width:180px}.paper{padding:24px;font-size:16px}h1{font-size:30px}dl{grid-template-columns:1fr}}</style>");
  html.Append($"<header><a href='{E(basePath)}'><b>true author.</b></a><span>Private document library · on this PC <button id='live-toggle' type='button'>Live updates on</button></span></header><main><div class='layout'><nav aria-label='Document library'><a href='{E(basePath)}'>All documents<small>{entries.Count} saved projects</small></a>");
  foreach(var e in entries) html.Append($"<a class='{(document==e.saved.DocumentId.ToString()?"active":"")}' href='{E(basePath)}?document={e.saved.DocumentId}'>{E(Title(e.saved))}<small>{e.audit.Text.Length:N0} text units · {E(e.updated.ToString("MMM d, HH:mm"))}</small></a>");
  html.Append("</nav><div>");
  var current=entries.Where(e=>e.saved.DocumentId.ToString()==document).ToList();
  if(current.Count==0)
  {
   html.Append("<small>YOUR LOCAL DOCUMENTS</small><h1>Every document. Its history.</h1><p>Open a document to explore the full text, highlighted origins and original paste history. Refresh for the latest saved changes.</p>");
   if(document.Length>0) html.Append("<section role='alert'>This document was not found. Choose one from the library.</section>");
   if(entries.Count==0) html.Append("<section><h2>Your first project starts in the app.</h2><p>Choose New project in the desktop companion and select a writing surface.</p></section>");
   html.Append("<div class='grid'>");
   foreach(var e in entries) html.Append($"<section><small>{E(e.updated.ToString("MMM d, yyyy · HH:mm"))}</small><h2><a href='{E(basePath)}?document={e.saved.DocumentId}'>{E(Title(e.saved))}</a></h2><p>{e.audit.Text.Length:N0} text units · {e.audit.Passages.Count} paste segments · {e.audit.Gaps} gaps</p><pre>{E(e.audit.Text.Length==0?"No document text captured yet.":e.audit.Text[..Math.Min(180,e.audit.Text.Length)])}</pre><a href='{E(basePath)}?document={e.saved.DocumentId}'>Explore document →</a></section>");
   html.Append("</div>");
  }
  else Detail(html,current[0].saved,current[0].audit,current[0].updated);
  if(unreadable>0) html.Append($"<p>{unreadable} audits could not be opened. Their files have been kept.</p>");
  html.Append($"<details><summary>Device identity</summary><code>{E(DeviceIdentity.InstallationId())}</code><p>No account needed. This ID is an identifier, not an access password.</p></details><footer><small>Document text and full source details remain on this PC. Cloud sync sends audit metadata only. This is current saved text and paste history; full revision snapshots are not recorded by this adapter.</small></footer></div></div></main></html>");
  html.Append("<script>" + LiveScript + "</script>"); return html.ToString();
 }
 static void ObservationReport(StringBuilder h, SavedAudit saved)
 {
  h.Append($"<section><h2>Capture coverage</h2><p>Latest capture mode: <b>{E(saved.CaptureMode)}</b>. Desktop-only browser text depends on Windows accessibility. Extension-assisted snapshots add document-level mapping; neither mode proves authorship.</p>");
  if (saved.Observations is { } o)
  {
   h.Append($"<p>{o.TypedKeys:N0} typing-key observations · {o.PasteAttempts:N0} paste shortcut observations · {o.UncertainEvents:N0} uncertain input events</p><p>These are desktop observations, not confirmed document insertions. They are not included in the highlighted passage totals or attributed to document positions. This recent observation log remains local.</p>");
   foreach(var input in o.Recent)
   {
    h.Append($"<details><summary>{E(input.At.ToLocalTime().ToString("g"))} · {E(input.Kind)} · {input.Units:N0} clipboard units</summary><p>{E(input.Source?.Name ?? "Unknown source")} · {E(input.Source?.Url)} · {E(input.Source?.Detection ?? "none")}</p>");
    if(input.Text is not null) h.Append($"<pre>{E(input.Text)}</pre>");
    h.Append("</details>");
   }
  }
  h.Append("</section>");
 }
 static void Detail(StringBuilder h,SavedAudit s,DocumentAudit d,DateTime updated)
 {
  var a=d.Activity;var spans=d.Spans();var origins=spans.Select(p=>p.Origin).Concat(d.Passages.Select(p=>p.Origin)).DistinctBy(Key).ToList();
  h.Append($"<small>DOCUMENT AUDIT</small><h1>{E(Title(s))}</h1><p>Saved {E(updated.ToString("g"))} · {d.Text.Length:N0} UTF-16 text units</p><section class='stats'><span><b>{a.TypedInserted:N0}</b>typed units</span><span><b>{a.PasteInserted:N0}</b>pasted units</span><span><b>{d.Deleted:N0}</b>deleted units</span><span><b>{d.Gaps:N0}</b>recording gaps</span></section><a class='pill' href='#preview'>Full document</a><a class='pill' href='#sources'>Sources &amp; paste history</a><a class='pill' href='#details'>Audit details</a><h2 id='preview'>Full document preview</h2><p>Click a highlighted passage to see its source. Colors identify recorded origins, not an AI prediction.</p>");
  for(int i=0;i<origins.Count;i++) h.Append($"<a class='pill' href='#source-{i}'><span class='swatch' style='background:{Color(origins[i],i)}'></span>{E(Label(origins[i]))}</a>");
  h.Append("<article class='paper' aria-label='Full highlighted document'>");
  foreach(var p in spans) {int i=origins.FindIndex(o=>Key(o)==Key(p.Origin));h.Append($"<a id='passage-{p.Start}' href='#source-{i}' aria-label='{E(Label(p.Origin))}, text position {p.Start}'><mark style='background:{Color(p.Origin,i)}' title='{E(Label(p.Origin)+" · "+p.Origin.LocalUrl)}'>{E(d.Text.Substring(p.Start,p.Length))}</mark></a>");}
  if(d.Text.Length==0)h.Append("No document text captured yet. Start recording a selected writing surface in the desktop app.");
  h.Append("</article><h2 id='sources'>Sources &amp; paste history</h2><p>Introduced totals include deleted text. Remaining totals describe the current document. Unknown sources stay unknown.</p>");
  for(int i=0;i<origins.Count;i++)
  {
   var o=origins[i];var matching=spans.Where(p=>Key(p.Origin)==Key(o)).ToList();var pastes=d.Passages.Where(p=>Key(p.Origin)==Key(o)).ToList();int remaining=matching.Sum(p=>p.Length);
   h.Append($"<section class='source' id='source-{i}'><h3><span class='swatch' style='background:{Color(o,i)}'></span>{E(Label(o))}</h3><p>{remaining:N0} remaining units · {(d.Text.Length>0?100.0*remaining/d.Text.Length:0):0.0}% of current document");
   if(o.Kind=="paste") h.Append($" · {pastes.Sum(p=>p.Characters):N0} introduced units · {pastes.Count} paste segments");
   h.Append("</p>");
   if(o.Kind=="paste") h.Append($"<dl><dt>Source location</dt><dd>{E(string.IsNullOrEmpty(o.LocalUrl)?string.IsNullOrEmpty(o.Host)?"No source URL observed":o.Host:o.LocalUrl)}</dd><dt>Evidence</dt><dd>{E(string.Join(", ",matching.Select(p=>p.Origin.Detection).Concat(pastes.Select(p=>p.Origin.Detection)).Distinct()))}</dd><dt>Category</dt><dd>{E(o.Category)}</dd></dl>");
   else h.Append($"<p>{(o.Kind=="typed"?"Correlated typing input. This does not establish the author's identity or exclude manually retyped material.":o.Kind=="preexisting"?"Already present when capture began. Its earlier origin was not observed.":"Changes without enough input or source evidence. No origin is asserted.")}</p>");
   foreach(var p in matching)h.Append($"<a class='pill' href='#passage-{p.Start}'>View passage · {p.Start}–{p.Start+p.Length}</a>");
   foreach(var p in pastes) {int survives=spans.Where(x=>x.Origin.PassageId==p.Id).Sum(x=>x.Length);h.Append($"<details><summary>Paste · {E(p.RecordedAt.ToLocalTime().ToString("g"))} · {p.Characters:N0} introduced · {survives:N0} remaining{(survives==0?" · removed from current text":"")}</summary><p>Capture method: {E(p.Origin.Detection)} · Source: {E(p.Origin.LocalUrl)}</p><pre>{E(p.OriginalText)}</pre></details>");}
   h.Append("</section>");
  }
  ObservationReport(h,s);
  h.Append($"<section id='details'><h2>Audit details</h2><dl><dt>Document ID</dt><dd>{s.DocumentId}</dd><dt>Started</dt><dd>{E(s.StartedAt.ToLocalTime().ToString("g"))}</dd><dt>Capture adapter</dt><dd>{E(s.Adapter)}</dd><dt>Saved state</dt><dd>{(s.Paused?"Paused":"Active")}</dd><dt>Activity history</dt><dd>{(a.Complete?"Counters available from audit start":"Partial historical counters")}</dd><dt>Edit events</dt><dd>{a.EditEvents:N0}</dd><dt>Delete events</dt><dd>{a.DeleteEvents:N0}</dd><dt>Preexisting units</dt><dd>{a.PreexistingInserted:N0}</dd><dt>Unobserved units</dt><dd>{a.UnobservedInserted:N0}</dd><dt>Cloud checkpoints</dt><dd>{s.Sequence}</dd><dt>Queued uploads</dt><dd>{s.Pending.Count}</dd><dt>Text fingerprint</dt><dd><code>{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(d.Text)))}</code></dd></dl><p>Counts are UTF-16 text units, not words. Gaps and unknown changes remain visible.</p></section>");
 }
}

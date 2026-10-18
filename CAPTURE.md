# Desktop capture strategy and coverage

TrueAuthor 0.8.0 combines independent local observations. A keyboard observation is not automatically counted as a document insertion.

## Sources and destination documents

- ChatGPT, other recognized AI tools, research/reference pages, PDF/reference apps, code sources and communication apps are automatic source contexts. They do not trigger destination-document tracking prompts. Clipboard provenance is collected automatically while capture is enabled.
- Word, Google Docs and writable document fields are destination candidates. The writer approves tracking a document. Returning to that selected field resumes capture; entering password/search/verification/card fields does not.
- Recent candidate metadata is kept in memory so a writer can select the document after opening the companion. Actual text reads are limited to the approved foreground field.

## Signals

1. Word's document interface supplies snapshots for a selected Word document.
2. Windows accessibility discovers focused writable Edit/Document controls across apps, including browser fields, without an extension. Read-only content, browser chrome and sensitive input labels are excluded.
3. Browser candidates require an observed HTTP(S) page address. Field runtime identity and a page-address fingerprint keep tabs/fields separate. Missing or changed identity stops that capture path.
4. A foreground-scoped keyboard hook translates supported physical keyboard input using the foreground keyboard layout, and observes Enter, Tab, deletion, cut, undo, redo and paste shortcuts. Injected input and unsupported composition remain uncertain.
5. Clipboard copies are hashed into a bounded expiring in-memory source ledger. Clipboard HTML SourceURL and application/window evidence supply provenance; clipboard privacy formats and blocked sources are respected again when reading a paste.
6. Accessible Paste menu invocations cover supported mouse/menu paste operations. Providers that do not emit Invoke events cannot be assumed covered.
7. Exact before/after text matching separates translated typing and observed pasted content. Same-length unrelated text is not typing evidence. A short late-event window can correct the latest unchanged unobserved edit after a delayed paste notification, provided a fresh snapshot agrees. It does not invent extra edits or overwrite confirmed attribution.
8. The optional extension supplies additional browser-specific text/input events. Desktop capture works independently where Windows exposes the field. No admin requirement or browser debug port is introduced.

## Storage and uncertainty

The recent-events view is bounded to 200 entries and two million UTF-16 units, with typing grouped into small bursts. A separate append-only input journal retains the recorded events in ordered Windows-user-encrypted batches. Its local export includes recorded text and must be kept private. A 50 MB per-document limit pauses capture instead of silently dropping history. The full saved document and source history also use Windows CurrentUser encryption. Writes are coalesced. Cloud metadata excludes manuscript text, input-log text, full URLs and clipboard payloads.

Unknown sources remain unknown. Counts are UTF-16 units. IME/dead-key composition, dictation, autocomplete, unsupported custom/canvas editors, missing accessibility providers, non-English browser address-bar labels and providers without menu events require additional adapters or remain partial coverage. This release does not promise universal exact keystroke-to-document reconstruction.

## Validation

Core regression cases cover exact translated text, Unicode, Enter/Tab, mixed typing/paste, stale and duplicate events, focus clearing, late paste correction, counter preservation and source/document role separation. Windows self-tests exercise encrypted recovery, escaped local reports, source history, local API isolation and pairing boundaries.

Live Word testing confirms native document snapshots without the extension. Live Notepad testing confirms extension-free foreground document snapshots, synthetic-input uncertainty, clipboard copying and receipt of Paste-menu observations. Automated input is deliberately not asserted to be physical typing. Browser and Word coverage must be assessed per editor/provider; do not infer universal compatibility from these tests.

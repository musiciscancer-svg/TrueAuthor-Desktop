# True Author Desktop

A local-first Windows companion that records how supported documents develop through typing, edits and observed paste sources. Review your writing history and source highlights in a private dashboard on your PC. Observations are evidence of captured activity, not proof of authorship.

This repository contains only the desktop app, its required C# core library, test fixtures, documentation and build configuration. It does not contain the True Author website, cloud-server implementation, browser extension, Chrome setup helper, accounts, keys or private writing data.

## Download and run

Download the standalone Windows executable from the [official Windows download page](https://trueauthor.io/downloads/windows). Open `TrueAuthor-0.7.1-win-x64.exe`; no extraction or separate .NET installation is required. You can also build from the public source using the commands below. Current development builds are unsigned and may show a Windows publisher warning.

Open a supported document, accept **Start tracking**, then choose **Open in browser** in the companion to inspect its local audit. The notification-area menu can pause capture or exit. An account is optional. The separately distributed Chrome extension can connect after you approve the connection once in Windows; it is not required or included here.

## Build and test

Windows and the .NET 9 SDK are required for development. The distributed executable bundles the runtime.

```powershell
dotnet run --project src/TrueAuthor.Core.Tests -c Release
dotnet run --project src/TrueAuthor.Desktop -c Release -- --self-test
./build.ps1
```

The standalone executable is written to `artifacts/TrueAuthor-0.7.1-win-x64.exe`. The GitHub Actions workflow runs the same tests and creates an unsigned executable artifact from the checked-out source. No server credentials or private repository dependencies are needed.

## Privacy

The app observes supported editor activity and clipboard sources to build the audit. Manuscript text, original pasted passages, full source URLs and detailed lineage stay on the PC. Recovery and device keys use Windows account encryption. Local diagnostics and exported reports may contain document information; share them only intentionally.

Cloud account linking and metadata sync are optional. If enabled by the writer, minimized metadata is sent to the configured server (by default `https://trueauthor.io`): document/device identifiers, timestamps, fingerprints, source categories/domains, passage ranges, revision counts and gaps. Manuscript text, original pasted text and full source URLs are not part of these cloud checkpoints. The connected service is separate from this source repository. See [True Author privacy and evidence](https://trueauthor.io/trust).

The local browser dashboard and optional extension connection listen on loopback only. Desktop connection approval is separate from cloud-account linking. Coverage depends on the editor and available accessibility information; gaps and ambiguous edits remain unobserved.

## Remove the app

Choose **Exit** from the notification-area menu and delete the executable. There is no installer or automatic startup registration. Existing local data is retained. To permanently remove your local audits and device identity as well, first back up anything needed, then remove `%LOCALAPPDATA%\TrueAuthor`. That optional deletion cannot be undone by the app. Cloud copies, if any, must be deleted separately through your account.

## Code signing policy

The maintainer is applying to SignPath Foundation for free signing of the **Windows desktop companion only**. The application is not yet approved and current builds are unsigned. No signature or certificate is claimed for the browser extension or other tools.

If approved, the intended attribution is: Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org). This attribution describes the requested arrangement, not an existing approval.

Josh Edison maintains the project and is the designated release-signing approver. Contributors submit changes for maintainer review. Maintainers must use multi-factor authentication for source-host and signing-service access. Every signing request requires manual approval; signing will be enabled only after SignPath accepts the project and its build integration. See [the signing policy](CODE_SIGNING.md).

## License

Apache License 2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE). The True Author name and logo remain trademarks of their owner. Public classification rules and synthetic test fixtures are included under the same license. Windows/.NET system libraries retain their upstream licenses.

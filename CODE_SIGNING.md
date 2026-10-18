# Code signing policy

Scope: the True Author Windows desktop companion executable built from this repository. No website, server, browser extension or Chrome setup helper is covered.

Status: application preparation; no SignPath certificate or production signing configuration has been approved. Published unsigned development builds must be described as unsigned.

Proposed provider on approval: Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## Roles and release controls

- Maintainer/committer and reviewer: Josh Edison.
- Release-signing approver: Josh Edison.
- Changes from other contributors require review before merging.
- Source-host and signing-service accounts must use multi-factor authentication.
- Releases must be built from reviewed source through the repository's Windows GitHub Actions build.
- Every production signing request requires manual approval. Fork pull requests must not receive signing credentials or trigger production signing.
- Signing credentials are not stored in the repository. Executables must not be changed after signing.

The current build workflow only tests and builds unsigned artifacts. Provider integration will be configured after approval; it will not be represented as working beforehand.

## Privacy and removal

Local document text and detailed source information stay on the PC. User-enabled account linking may send minimized metadata to the configured cloud service. [Privacy details and removal instructions](README.md#privacy) are in the README, along with the external service's policy link.

Report security concerns to the maintainer privately through GitHub when that facility is available; do not publish private manuscripts, account keys or local audit files in issues.

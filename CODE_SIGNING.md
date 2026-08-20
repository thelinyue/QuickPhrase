# Code signing policy

QuickPhrase is an open-source Windows application distributed from the public repository `thelinyue/QuickPhrase` under the MIT License.

Free code signing provided by SignPath.io, certificate by SignPath Foundation.

## Team roles

### Author / Committer

- `thelinyue`
- Maintains production source, dependencies, tests, release scripts and installer configuration.

### Reviewer

- `thelinyue`
- Reviews external contributions and high-risk changes before they enter the release branch.

### Approver

- `thelinyue`
- Manually approves SignPath release-signing requests after verifying the source commit, workflow run and unsigned artifact hashes.

This is currently a single-maintainer project. GitHub and SignPath multi-factor authentication are mandatory for the maintainer.

## Review policy

External contributions must use a GitHub Pull Request and be reviewed by the maintainer. Changes to the following files are treated as high risk and require explicit release review:

```text
.github/workflows/**
scripts/build-release.ps1
scripts/finalize-signed-release.ps1
scripts/verify-phase6.ps1
installer/QuickPhrase.iss
CODE_SIGNING.md
PRIVACY.md
SECURITY.md
package and dependency declarations
```

Signing requests are never automatically approved.

## Signed artifacts

The release process signs QuickPhrase-owned application binaries before packaging:

```text
QuickPhrase.exe
QuickPhrase.dll
QuickPhrase.Core.dll
QuickPhrase.Platform.Windows.dll
```

The Inno Setup installer is built from the verified signed application directory and is then signed in a second SignPath request:

```text
QuickPhrase-Setup-<version>.exe
```

Microsoft, .NET and other upstream third-party binaries are not re-signed by QuickPhrase.

## Build provenance

Release-signing requests must originate from GitHub-hosted Actions runs in this repository. The SignPath GitHub Connector verifies the GitHub Actions artifact and workflow provenance. Locally built or externally supplied binaries are not eligible for release signing.

Every Release includes:

```text
SHA256SUMS.txt
release-manifest.json
source tag
workflow run provenance
```

## Approval checks

Before approving a signing request, the Approver verifies:

1. The source tag and commit are expected and immutable.
2. Required CI, tests and Launcher smoke passed.
3. The artifact contains no WebView2, React or prototype web resources.
4. ProductVersion, FileVersion, filenames and release channel are correct.
5. The unsigned artifact hash matches the workflow output.
6. The requested Artifact Configuration only signs the intended QuickPhrase-owned files.
7. The release notes and manual acceptance gates are complete.

## Incident response

Signing stops immediately when:

- GitHub or SignPath credentials may be compromised.
- A workflow, signing policy or Artifact Configuration changes unexpectedly.
- Artifact provenance cannot be verified.
- Malware, an unauthorized binary or a security regression is suspected.
- A required manual release gate is missing.

Affected Releases are withdrawn or marked as revoked. Existing tags are not moved or force-pushed. Recovery requires credential rotation, policy review, a new version and a completely new build/sign/verification run.

## Privacy

The project privacy policy is published in [PRIVACY.md](PRIVACY.md). Security reports follow [SECURITY.md](SECURITY.md).

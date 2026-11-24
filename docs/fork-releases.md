# Fork releases

How this fork is built and published, and what to do when upstream moves or a
new fork build is cut.

Fork-only: do not upstream this file, `ForkConfig.cs`,
`.github/workflows/fork-release.yml`, or `docs/fork-changelog.md`.

## Branches and remotes

| Name | Purpose |
|---|---|
| `origin` | `theduardomaciel/koda-windows`, the fork that publishes releases |
| `upstream` | `Ivorisnoob/koda-windows`, the source project |
| `main` | Default branch; upstream's `main` plus a copy of `fork-release.yml` |
| `beta` | A pure mirror of `upstream/beta`; only fast-forward it |
| `custom` | Integration and release branch; everything the fork ships |
| feature branches | Cut from `custom` for fork-only work, or from `beta` when the work is going upstream |

Rules:

- `custom` must always contain `upstream/beta`; the release workflow checks this.
- Merge, do not rebase, branches that have been shared.
- Keep `src/Koda/Koda.csproj`, `src/Koda/Package.appxmanifest`,
  `CHANGELOG.md` and `scripts/build-release.ps1` identical to upstream. The
  fork does not change the version in source; the workflow stamps it in CI.
- Keep `.github/workflows/fork-release.yml` identical on `main` and `custom`.
  GitHub only registers workflows that exist on the default branch, so the
  `main` copy is what makes tag pushes run; the tagged copy is what runs.
- Do not merge `custom` into an upstream-bound feature branch.

## Release model

- The source tree keeps upstream's version (`<Version>0.3.0</Version>` and the
  appxmanifest identity). The release workflow stamps both from the tag.
- Tags use four numeric parts: `v<major>.<minor>.<patch>.<forkBuild>`
  (for example `v0.3.0.1`). Four parts map one-to-one onto the assembly, file
  and appx versions, and sort above the upstream base and below the next
  upstream release.
- Windows Installer only compares the first three fields, so `0.3.0.1` and
  `0.3.0.2` are the same MSI product version. `Koda.wxs` sets
  `AllowSameVersionUpgrades`, so a same-base fork build still replaces the
  installed one; the app shows the full four-part version.
- Never reuse or move a published tag.
- Releases must be published and non-prerelease; GitHub's `/releases/latest`
  ignores drafts and pre-releases.
- Artifacts are the x64 MSI and the x64 portable zip, both unsigned. There is
  no MSIX because that format needs a signing certificate Windows already
  trusts, and no keystore equivalent: Windows Installer installs an unsigned
  MSI without complaint.

## One-time GitHub setup

1. Enable Actions on the fork.
2. Allow workflow write permissions. The release workflow requests
   `contents: write` explicitly; either leave the repository default at read
   (the explicit block is what the job uses) or set it to read and write as the
   Android fork does.
3. Put `.github/workflows/fork-release.yml` on the default branch (`main`), with
   the same content as on `custom`. GitHub only registers workflows that exist
   on the default branch; without that copy a tag push starts nothing, and the
   workflow is missing from the Actions list.
4. No secrets are needed.

The workflow adds an `upstream` remote for its ancestry guard. That makes `gh`
resolve the repository to `Ivorisnoob/koda-windows`, so every `gh release`
command passes `--repo $env:GITHUB_REPOSITORY` explicitly.

## Cutting a release

1. Update and verify the branch:

```powershell
git fetch upstream
git checkout beta
git merge --ff-only upstream/beta
git checkout custom
git merge beta
git status --short
git merge-base --is-ancestor upstream/beta custom   # must succeed
dotnet build src\Koda\Koda.csproj -p:Platform=x64
dotnet test tests\Koda.Core.Tests
```

2. Pick the next free fourth number. With upstream at `0.3.0` and `v0.3.0.4`
   published, the next tag is `v0.3.0.5`. If upstream moves to `0.3.1`, restart
   at `v0.3.1.1`. The tag must be higher than every published tag, including
   tags left by failed attempts. Add a `## <version>` section to
   `docs/fork-changelog.md` with the user-facing notes; the workflow fails the
   release if it is missing or empty.

3. Tag and push:

```powershell
git tag -a v0.3.0.5 -m "Koda for Windows v0.3.0.5"
git push origin custom
git push origin v0.3.0.5
```

4. Watch the run and verify the release:

```powershell
gh run list --repo theduardomaciel/koda-windows --limit 5
gh run watch <run-id> --repo theduardomaciel/koda-windows
gh release view v0.3.0.5 --repo theduardomaciel/koda-windows
```

Expected: a green run, a published non-prerelease latest release, and two
assets (`Koda-0.3.0.5-win-x64.msi`, `Koda-0.3.0.5-win-x64-portable.zip`).

The workflow:

1. Checks out the tagged commit with full history.
2. Fetches `upstream/beta` and asserts it is an ancestor.
3. Asserts the tag is four numeric parts and its first three are at least
   upstream's.
4. Stamps `<Version>` in `Koda.csproj` and the identity version in
   `Package.appxmanifest`.
5. Runs the offline tests.
6. Builds the self-contained publish, the portable zip and the MSI with WiX.
7. Verifies the published `Koda.exe` file version and both artifact names.
8. Reads the tag's section from `docs/fork-changelog.md`, appends the Full
   Changelog compare link, and creates the GitHub release with `--notes-file`,
   passing `--repo` so `gh` targets the fork instead of the `upstream` remote
   the guard added.

## Changelog and release notes

The GitHub release body is the only notes surface today. There is no in-app
"What's new" sheet and no update checker yet; the About row shows the version
and links to the fork.

- Fork release notes live in `docs/fork-changelog.md`, newest section first.
  Add a `## <version>` section before tagging. The workflow extracts that
  section, appends the Full Changelog compare link, and publishes it as the
  release body. A missing or empty section fails the release.
- The compare link uses the previous release tag. On the first fork release it
  falls back to the upstream base tag (for example `v0.3.0`).
- `CHANGELOG.md` stays upstream's and is not edited by a fork release.

## Updating from upstream

```powershell
git fetch upstream
git checkout beta
git merge --ff-only upstream/beta
git checkout custom
git merge beta
```

Then:

- Resolve conflicts by reading both sides; never blindly take one.
- Keep upstream's version values and `CHANGELOG.md`.
- Re-point any new user-facing `Ivorisnoob/...` URL at `ForkConfig`: run
  `git grep -ni "ivorisnoob/koda-windows"` and check every hit that is a
  user-facing link.
- Run `git merge-base --is-ancestor upstream/beta custom` and
  `dotnet build src\Koda\Koda.csproj -p:Platform=x64` before tagging.

Note that `upstream/beta` is ahead of `upstream/main`; the fork follows
`beta`, so a fork build can contain upstream work that has not been released
yet. Merge `upstream/main` instead only if the fork should be built strictly
on upstream's released code, and change the workflow's guard to match.

## Install notes

The fork keeps upstream's MSI `UpgradeCode`, AUMID (`Koda.Desktop`) and data
folder (`%LOCALAPPDATA%\Koda`). Installing the fork over upstream is an
in-place replacement that keeps the library; the two cannot be installed side
by side. Switching back to upstream works the same way as long as its version
is not lower.

The artifacts are unsigned, so SmartScreen may ask for **More info**, then
**Run anyway**. That is the same prompt upstream's releases carry.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Workflow did not run | The tag must start with `v`; check Actions is enabled, the tag was pushed (`git push origin <tag>`), and `.github/workflows/fork-release.yml` exists on the default branch (`main`). GitHub registers workflows only from there. |
| Publish fails: tag "exists locally but has not been pushed to Ivorisnoob/koda-windows" | `gh` resolved the upstream remote the workflow added. Every `gh release` command must pass `--repo $env:GITHUB_REPOSITORY`. |
| Publish fails: no `## <version>` section | Add the section to `docs/fork-changelog.md`, push it, then tag. |
| Guard: `upstream/beta` is not an ancestor | Merge the current `upstream/beta` into `custom`, then tag again. |
| Guard: version below upstream | Merge upstream's version bump into `custom`; never override it locally. |
| Guard: tag is not four numeric parts | Use `v0.3.0.1`, not `v0.3.0`. |
| `wix` not found | The workflow installs WiX 5.0.2; a local run needs `dotnet tool install --tool-path <path> wix --version 5.0.2` and that path on `PATH`. |
| MSI refuses to install (`a newer version is already installed`) | The new MSI's base is below the installed one; base the fork build on the installed upstream version. The fourth field does not order. |
| Release exists already | Use a new fork-build tag; prefer that over deleting a published release. |
| Artifact names have the old version | The workflow stamps the appxmanifest before packaging; a local build reads the version from that file. |

## Security

- No signing key is involved: the MSI and zip are intentionally unsigned.
- Keep tags immutable; never move or delete a published release.
- Fork pull requests never receive the release token; do not add a
  `pull_request` trigger to the release workflow.

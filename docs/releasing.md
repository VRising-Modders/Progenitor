# Releasing Progenitor

The checklist maintainers follow before the repository becomes public and before each tagged release. Publishing a mod built on Progenitor is a different task; see [publishing.md](publishing.md).

## 1. Before the repository becomes public

Run every check on the exact commit of `main` that will be public, and on its full history: making a repository public publishes every commit, not only the tip.

1. **Identifier guard.** `IdentifierGuardTests` (in `tests/Progenitor.Tests`) must pass: it scans every tracked file and every entry of a tracked zip for personal identifiers, SteamIDs outside the test placeholder range, and machine paths. It covers only the tip, so also scan the whole history (`git log --all -p`, plus commit messages, author and committer names and emails) with the maintainers' private pattern list of personal identifiers: names, handles, email addresses, account IDs, IP addresses and local user paths. Every hit must be gone from history, not only from the tip. Commits must carry the project identity and a GitHub `noreply` address.
2. **Wording scan.** Scan the same range for names of private tools, internal research locations, links into private repositories, and narration of past chat sessions. Tracked files must stand on their own for an outside reader.
3. **No game or private material.** No game assets, game binaries, interop assemblies, native code, tokens (`*.token`), operator keys or dev-bridge opt-in markers are tracked (`git ls-files` against `.gitignore`). Interoperability data is limited to what [NOTICE](../NOTICE) describes.
4. **Build and tests.** On a machine with the game installed:

   ```
   dotnet build -c Release
   dotnet test tests/Progenitor.Tests
   dotnet test tests/Progenitor.Testing.Tests
   node tools/kb/kb.mjs check
   node --test "tools/kb/test/*.test.mjs"
   ```

   and the MCP tool tests in `tools/mcp` (`npm ci`, then the test files the CI workflow lists). CI must be green on the same commit.
5. **Public files present and current:** [README](../README.md), [CONTRIBUTING](../CONTRIBUTING.md), [SECURITY](../SECURITY.md), [CODE_OF_CONDUCT](../CODE_OF_CONDUCT.md), [LICENSE](../LICENSE), [NOTICE](../NOTICE) (every dependency listed), the issue forms and the pull request template under `.github/`, and the artwork provenance in [branding/README.md](../branding/README.md).
6. **Documentation gate.** Every feature the README advertises has a working example and documentation, with honest verification labels in [api.md](api.md). Features that are not ready are labeled experimental or planned.
7. **Explicit maintainer authorization** to change the visibility. Nothing in this repository is that authorization.

## 2. GitHub settings at the switch

Enable these when the repository becomes public (some are available only on public repositories):

| Setting | Where |
|---|---|
| Branch protection (or a ruleset) on `main`: require pull requests, require the `tests` workflow checks, block force pushes and deletion | Settings → Rules / Branches |
| Secret scanning and push protection | Settings → Code security |
| Private vulnerability reporting (the channel [SECURITY.md](../SECURITY.md) and the code of conduct point to) | Settings → Code security |
| Dependabot alerts | Settings → Code security |
| Automatically delete head branches after merge (already on) | Settings → General |
| GitHub Pages from `main`, folder `/docs` (on since 2026-10-19). It serves the online Help Guide that the README and the store listings link: `https://vrising-modders.github.io/Progenitor/guide/progenitor-guide.html` | Settings → Pages |

After the switch, open the Security tab once as an outside reader would and confirm **Report a vulnerability** is offered, and confirm the issue forms appear under **New issue**.

## 3. Cutting a release

1. Confirm `main` is green and the [roadmap](roadmap.md) and [api.md](api.md) match what ships.
2. Versions are computed from git ([versioning.md](versioning.md)); do not edit version numbers by hand. Build the package with `powershell -File tools/package.ps1` (never `-IncludeDev` for a public release: the dev bridge must not ship).
3. Inspect both zips' file lists: Bootstrap, Core and the preloader, and no `Progenitor.Dev.dll`, token or opt-in marker. `packaging/out/Progenitor-<version>.zip` is the manual install; its only root entry is `BepInEx/`. `packaging/out/Progenitor-<version>-thunderstore.zip` carries the Thunderstore root files, `BepInEx/plugins/` and `BepInEx/patchers/`, and Core's folder packed as `BepInEx/patchers/progenitor-files.zip`: V Rising's Thunderstore install rules only route `plugins`, `patchers`, `core`, `monomod` and `config`, and a mod manager flattens any other folder into `BepInEx/plugins/<package>/`. The preloader unpacks the archive into `BepInEx/progenitor-core` before Bootstrap starts. `tools/package.ps1` refuses a Thunderstore zip with any entry outside those routes or a loose `Progenitor.dll`.
4. Create an **annotated tag** on the release commit and push it (release tags are not signed; the release assets carry `SHA256SUMS`):

   ```
   git tag -a v<version> -m "Progenitor <version>"
   git push origin v<version>
   ```

5. Create the GitHub release from the tag with the changelog (`packaging/thunderstore/CHANGELOG.md`) and attach `Progenitor-<version>.zip` (the manual install players download), `Progenitor-<version>-thunderstore.zip` and `SHA256SUMS`. The manual zip carries `BepInEx/progenitor-self-install.json` (written by `tools/package.ps1`), so an unmodified extract updates itself from its first start; check it is in the file list. Mark it the latest release: the install scripts fall back to it ([section 5](#5-the-install-scripts)).
6. Add the release to Progenitor's release list and push it ([section 4](#4-the-release-list)). Installed copies learn about the release only from that list.
7. Thunderstore does not host Progenitor (it declined in October 2026); Progenitor ships from GitHub Releases. The Thunderstore-layout zip stays attached to the release for players whose mod manager installs from a file.
8. Publish `Progenitor-<version>.zip` (the manual install) on Nexus Mods with the page fields, description and images in [packaging/nexus](../packaging/nexus/README.md). Open every link in the page first; the Help section points at the online Help Guide and the video guides on YouTube, which must already be up.
9. Install the published package with a mod manager into a clean profile and confirm it loads on a client and on a test server you run yourself: the profile has `BepInEx/progenitor-core/Progenitor.dll`, no `Progenitor.dll` under `BepInEx/plugins`, and the first start's log reads `Progenitor package files: unpacked … into BepInEx/progenitor-core.` followed by `Progenitor <version> loaded`. Gale can do the install from a command line: `gale --game v-rising --profile Default --install <zip> --no-gui` (it still opens its window, and installs the zip's dependencies only when its Thunderstore cache is loaded). A pre-release zip was installed this way on 2026-10-19 (Gale 1.23.2; BepInEx copied into the profile by hand, since that install skipped the dependency) and a test server started from that profile loaded Core.

## 4. The release list

Installed copies of Progenitor update themselves from a signed list of releases, `docs/updates/releases.json` with its signature `docs/updates/releases.json.sig`, served by the repository's GitHub Pages site at `https://vrising-modders.github.io/Progenitor/updates/releases.json` (jsDelivr is the fallback). The format and the client's rules are in [auto-update.md](auto-update.md#release-list-github). The tool is `tools/release-feed/release-feed.mjs` (Node only). Status: the tool and the list's format are tested game-free; no release has been added this way yet.

### Once: the release key

1. Generate the key outside the repository:

   ```
   node tools/release-feed/release-feed.mjs keygen --out <folder outside the repository>/progenitor-release.key
   ```

   It writes the private key (PKCS#8 PEM) to that file and the public key into `src/Progenitor.Core/Install/SelfFeedKeys.cs` (`Current`). The tool refuses a path inside the repository that git does not ignore; `.gitignore` covers `progenitor-release*.key` and `*.pem`. Keep a backup of the private key somewhere safe: a lost key means the next release must ship a new public key, which installed copies learn only by installing that release by hand.
2. Commit `SelfFeedKeys.cs` through a pull request, so the next release's Core has the key built in. A Core built before that has no key and offers no update of itself.
3. If the workflow below should sign, give it the key through a GitHub Environment, not a repository secret. Nothing in this repository creates these:
   1. Settings › Environments › **New environment** `release-feed`. Create it, with these rules, before adding the secret: a workflow that names a missing environment creates it unprotected. Under **Deployment branches and tags** allow the branch `main` and the tag pattern `v*` (a published release runs on its tag); under **Required reviewers** add a maintainer, so every signing run waits for an approval.
   2. In that environment add the secret **`PROGENITOR_RELEASE_KEY`** with the private key's text (and `PROGENITOR_RELEASE_KEY_NEXT` only while a key is rolled). The workflow passes it only to its signing step.
   3. The VRising-Modders organization does not allow GitHub Actions to create pull requests (Settings › Actions › General › **Workflow permissions**), so the workflow pushes its branch and a maintainer opens the pull request (below).
4. Seed the list: `docs/updates/releases.json` already names 2026.10.162, read from its published asset (`Progenitor-2026.10.162.zip`, sha256 `40bb2aaada1e55643c1e1768129be661e24a2fb2d4deaae6dd2be55ec241b183`, 15,304,177 bytes), and was signed when the key was made (key fingerprint `9f4ee3fdde3a75a2`, 2026-10-20). To seed a new list: `node tools/release-feed/release-feed.mjs sign --key <key file>`, then `verify`, and commit both files.

To roll the key: `keygen --slot next` writes the new public key into `Next`; ship a release with it; sign with both keys (`--key old --key new`, or `PROGENITOR_RELEASE_KEY` and `PROGENITOR_RELEASE_KEY_NEXT`); once most installs run a Core that has it, move it to `Current` and empty `Next`.

### Each release

1. Build and package ([section 3](#3-cutting-a-release), steps 2 and 3).
2. Create the GitHub release with its assets (section 3, steps 4 and 5).
3. Add the release to the list and sign it, from the published asset (the bytes players download):

   ```
   gh release download v<version> --repo VRising-Modders/Progenitor --pattern "Progenitor-<version>.zip" --dir <temp folder>
   node tools/release-feed/release-feed.mjs add --package <temp folder>/Progenitor-<version>.zip --release https://github.com/VRising-Modders/Progenitor/releases/tag/v<version> --key <key file>
   node tools/release-feed/release-feed.mjs verify
   ```

   The tool reads the package as Progenitor does (its sha256 and size, the sha256 of Core and Bootstrap, the Bootstrap and handshake protocols), refuses a development version or a package whose record does not name its own files, and keeps the list newest first.
4. Commit `docs/updates/releases.json` and `releases.json.sig` on a branch and open a pull request (main is protected).
5. Merge and push main. GitHub Pages publishes the list within minutes; installed copies read it on their next check.

`sign` moves the list's `updated` time forward every time it signs, and `add` signs. A copy of Progenitor never takes a list signed earlier than the one it already has, so an old list served again cannot hide a release; it also means the list on main must always be the latest one signed.

The **release-feed** workflow (`.github/workflows/release-feed.yml`) does steps 3 and 4 when a release is published (never for a prerelease; a manual run names its tag), and pushes the branch `release-feed/<tag>`. Its last step, opening the pull request, fails because the VRising-Modders organization blocks GitHub Actions from creating pull requests; the maintainer opens the pull request for that branch (`gh pr create --base main --head release-feed/<tag>`), then does step 5. It runs in the `release-feed` environment, one run at a time, with its actions pinned by commit. It fails at its first step until the environment's `PROGENITOR_RELEASE_KEY` secret exists; until then run the steps by hand.

- A pull request opened with `GITHUB_TOKEN` does not start the **tests** workflow. Re-run the checks (or push an empty commit to the branch as the maintainer) before merging.
- If two release-list pull requests are open and one merges first, the other conflicts. Rebase it on main taking main's `releases.json` and `releases.json.sig`, then run `add` for its release again (which signs), so the list and its signature cover both releases. Never merge the two lists by hand: that breaks the signature.

## 5. The install scripts

`docs/install.ps1` and `docs/uninstall.ps1` are served by GitHub Pages at `https://vrising-modders.github.io/Progenitor/install.ps1` and `.../uninstall.ps1`; the README, the Help Guide and the mod template's README tell players to run them. A change merged to `main` is live for every player at once, so review script changes like code that ships.

The scripts always install the latest release. **Nothing changes per release** as long as the assets keep their names:

- The installer reads the signed release list ([section 4](#4-the-release-list), `https://vrising-modders.github.io/Progenitor/updates/releases.json`) and installs its newest active `release` entry, checking the entry's `size`, `sha256` and `coreSha256`. It does not check the list's signature itself; the list comes over HTTPS from the project's own site, and Progenitor checks the signature before it updates itself. When the list cannot be read, it reads `https://github.com/VRising-Modders/Progenitor/releases/latest/download/SHA256SUMS`, takes the version and sha256 from the `Progenitor-<version>.zip` row, and downloads that zip from the same `latest/download/` address. A release without that zip or that row breaks the fallback.
- BepInEx is a pinned, reviewed `BepInExPack_V_Rising` version (`$PgBepInExVersion` and `$PgBepInExSha256` near the top of `docs/install.ps1`), downloaded from `https://thunderstore.io/package/download/BepInEx/BepInExPack_V_Rising/<version>/` and checked by sha256. The newest pack is used only with `-LatestBepInEx` (`PROGENITOR_LATEST_BEPINEX=1`), checked for completeness only.
- The scripts never use the GitHub API or raw.githubusercontent.com; `InstallScriptTests` enforces the allowed hosts.

The manual zip carries `BepInEx/progenitor-self-install.json` (written by `tools/package.ps1`), and the installer extracts it as is. For an older zip without one it writes the record itself from the installed Bootstrap and Core (`"source": "installer"`). The record's `version` is the release version that the release list or `SHA256SUMS` names for that exact zip (checked by sha256). Core's own product version (without `+<revision>`) is the release of Core's last change and can be older (2026.10.162 ships a Core that reports 2026.10.161), so it is used only for a zip nothing checks; with neither, no record is written. When the release list names a `coreSha256`, the installed Core must match it or the install is undone.

**Moving to a newer BepInEx pack.** Only after the new pack has been tried with Progenitor on a client and a server:

1. Download `https://thunderstore.io/package/download/BepInEx/BepInExPack_V_Rising/<version>/` and take its sha256 (`Get-FileHash <zip> -Algorithm SHA256`).
2. Set `$PgBepInExVersion` and `$PgBepInExSha256` in `docs/install.ps1`, and the matching names in `InstallScriptTests` if a test names the version.
3. Run `InstallScriptTests` and, once, the opt-in network test (`PROGENITOR_INSTALL_NETWORK_TEST=1`), which downloads the pinned pack and checks its sha256.

After publishing a release, check the installer sees it once the release list on main names it: `powershell -ExecutionPolicy Bypass -File docs/install.ps1 -WhatIf -GamePath <empty folder holding a file named VRising.exe>` names the new version.

**Status.** Game-free tested: `InstallScriptTests` runs both scripts in Windows PowerShell 5.1 and PowerShell 7 (whichever are installed; CI has both) against temporary fake game folders with local fixture zips. The opt-in `Network_install_of_the_latest_release_into_a_fake_folder` (`PROGENITOR_INSTALL_NETWORK_TEST=1`) downloads the real release and BepInEx pack into a fake folder; it passed in Windows PowerShell 5.1 on 2026-10-20 against 2026.10.162. Neither script has been run against a real game install.

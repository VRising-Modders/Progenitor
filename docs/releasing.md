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

5. Create the GitHub release from the tag with the changelog (`packaging/thunderstore/CHANGELOG.md`) and attach `Progenitor-<version>.zip` (the manual install players download), `Progenitor-<version>-thunderstore.zip` and `SHA256SUMS`. The manual zip carries `BepInEx/progenitor-self-install.json` (written by `tools/package.ps1`), so an unmodified extract updates itself from its first start; check it is in the file list.
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
3. Add the private key's text as the repository secret **`PROGENITOR_RELEASE_KEY`** (Settings › Secrets and variables › Actions) if the workflow below should sign. Nothing in this repository creates that secret.
4. Seed the list: `docs/updates/releases.json` already names 2026.10.162, read from its published asset (`Progenitor-2026.10.162.zip`, sha256 `40bb2aaada1e55643c1e1768129be661e24a2fb2d4deaae6dd2be55ec241b183`, 15,304,177 bytes), but is not signed yet. Sign it once the key exists: `node tools/release-feed/release-feed.mjs sign --key <key file>`, then `verify`, and commit both files.

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

The **release-feed** workflow (`.github/workflows/release-feed.yml`) does steps 3 and 4 when a release is published, and opens the pull request for step 5. It fails at its first step until the `PROGENITOR_RELEASE_KEY` secret exists; until then run the steps by hand.

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

5. Create the GitHub release from the tag with the changelog (`packaging/thunderstore/CHANGELOG.md`) and attach `Progenitor-<version>.zip` (the manual install players download), `Progenitor-<version>-thunderstore.zip` and `SHA256SUMS`.
6. Publish `Progenitor-<version>-thunderstore.zip` to Thunderstore as `VRising_Modders-Progenitor`, categories **Mods**, **Client**, **Server** and **Tools**, plus the current game update's category. The package layout, dependencies and checks are in [publishing.md](publishing.md); Progenitor's own package files are under `packaging/thunderstore/`. Its README shows images from `packaging/listing/` on `main`, so merge listing changes before uploading.
   Before uploading, open every link in the listing; the Help section points at the online Help Guide and the video guides on YouTube, which must already be up.
7. Publish `Progenitor-<version>.zip` (the manual install) on Nexus Mods with the page fields, description and images in [packaging/nexus](../packaging/nexus/README.md).
8. Install the published package with a mod manager into a clean profile and confirm it loads on a client and on a test server you run yourself: the profile has `BepInEx/progenitor-core/Progenitor.dll`, no `Progenitor.dll` under `BepInEx/plugins`, and the first start's log reads `Progenitor package files: unpacked … into BepInEx/progenitor-core.` followed by `Progenitor <version> loaded`. Gale can do the install from a command line: `gale --game v-rising --profile Default --install <zip> --no-gui` (it still opens its window, and installs the zip's dependencies only when its Thunderstore cache is loaded). A pre-release zip was installed this way on 2026-10-19 (Gale 1.23.2; BepInEx copied into the profile by hand, since that install skipped the dependency) and a test server started from that profile loaded Core.

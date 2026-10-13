# Versioning

Progenitor versions are computed from git at build time. Nobody edits a version number by hand. Each component (Bootstrap, Core, Dev, each sample) carries the version of its **own** last change, so an unchanged component keeps its version and builds byte-identical.

## Format

| Build of a component | Version string | Example |
|---|---|---|
| Unchanged since main (its inputs have no unmerged commits or uncommitted edits) | `<year>.<month>.<release>` | `2026.9.12` |
| Its inputs have unmerged commits or uncommitted changes | `<year>.<month>.<release>.<sub>` | `2026.9.12.3` |

`2026.9.12.3` reads as "last changed on main in release 12 of September 2026, plus 3 iterations of unmerged work on this component". Releases normally have no sub-release number; it exists only to tell unmerged builds apart.

## How the numbers are computed

`tools/version/ProgenitorVersion.targets` runs before any project compiles. Git runs once per build per MSBuild node (one log of main, one log of the branch, one status); every project reuses the result.

1. **Base.** The merge base of `HEAD` and `origin/main`; if `origin/main` does not exist, local `main`. The **main line** is the base's first-parent history.
2. **Release of a main-line commit.** Take the newest merge commit (two or more parents) on the main line at or before that commit. Its UTC committer date gives `year.month` (month without a leading zero). `release` is the number of merge commits on the main line, up to and including it, whose UTC committer date is in that same month. It restarts at 1 each month. A direct (non-merge) commit on main is not a release: it has the release of the last merge before it. A main-line commit with no merge at or before it is `<its year>.<its month>.0`.
3. **A component's release.** The release of the newest main-line commit whose first-parent diff touches one of the component's inputs. For a merge, that diff is everything the PR brought in. A component with no such commit (it does not exist on main yet) takes the base's release.
4. **Sub-release.** The number of commits in `base..HEAD` whose first-parent diff touches the component's inputs, plus 1 when an input has uncommitted changes (tracked edits or untracked files that are not ignored).

### A component's inputs

| Input | Why |
|---|---|
| The project's directory | Its own sources and project file. |
| Files and wildcard directories the project file includes from outside its directory (`Compile`, `EmbeddedResource`, `Content`, `None`, `AdditionalFiles`) | Linked sources are compiled into it. Bootstrap links `src/Progenitor.Core/Players/Forms/ControlLeasePolicy.cs`, so a change to that file changes Bootstrap. |
| The inputs of every `ProjectReference`, transitively | The DLL records the referenced assembly's version. Dev and the samples reference Core, so every Core change is also a Dev and sample change. |
| `Directory.Build.props`, `Directory.Build.targets` | Shared compiler settings. A change to either changes every component. |

Items whose path uses an MSBuild property are not followed. Files git ignores (for example the generated `tools/addresslib/*.json` Core embeds) never register as changes. `tools/version/*` is not an input: a change to the rules shows up in the computed strings themselves. A project can add inputs with the `ProgenitorVersionExtraInputs` property.

With no project (running the targets file on its own, as `tools/package.ps1` does), every file is an input: that is the **repository version**, the version of the package as a whole.

### Examples

| Situation | Bootstrap | Core | Repository |
|---|---|---|---|
| Clean main; PR 12 of September changed Core only, PR 9 last changed Bootstrap | `2026.9.9` | `2026.9.12` | `2026.9.12` |
| Same, one Core file edited | `2026.9.9` | `2026.9.12.1` | `2026.9.12.1` |
| Branch with 2 commits touching Core, 1 touching Bootstrap | `2026.9.9.1` | `2026.9.12.2` | `2026.9.12.3` |
| A direct commit on main after PR 12 touching Core | `2026.9.9` | `2026.9.12` | `2026.9.12` |
| First PR merged on 1 October (UTC), touching Core | `2026.9.9` | `2026.10.1` | `2026.10.1` |

A direct commit on main therefore produces a second build under an existing version; the informational version's commit (below) tells them apart. Land changes through PRs.

## Where the version appears

| Consumer | Value for `2026.9.12` | Value for `2026.9.12.3` |
|---|---|---|
| `Version`, `MyPluginInfo.PLUGIN_VERSION` (Bootstrap, Core, Dev, Sample, spikes) | `2026.9.12` | `2026.9.12.3` |
| `AssemblyInformationalVersion` (Windows "Product version") | `2026.9.12+<sha>` | `2026.9.12.3+<sha>` or `+<sha>.dirty` |
| `AssemblyVersion`, `FileVersion` (always four numbers) | `2026.9.12.0` | `2026.9.12.3` |
| BepInEx plugin metadata (`PluginInfo.Metadata.Version`) | `2026.9.12` | `2026.9.12` |
| Progenitor handshake and mod manifest rows (`ctx.Version`, `[NetworkCompatibility]`) | `2026.9.12` | `2026.9.12` |
| Thunderstore `version_number` (repository version) | `2026.9.12` | `2026.9.12` (never upload) |
| Package zip name (repository version) | `Progenitor-2026.9.12.zip` | `Progenitor-2026.9.12.3.zip` |
| Catalog HTTP user agent (Core's version) | `Progenitor/2026.9.12` | `Progenitor/2026.9.12.3` |

`<sha>` is the component's last change: the newest branch commit touching its inputs, otherwise the main-line commit that set its release. `.dirty` means an input had uncommitted changes.

**BepInEx** parses the `[BepInPlugin]` version with SemanticVersioning. A four-part string is not SemVer, so BepInEx falls back to `System.Version` and keeps major, minor and build: the sub-release number is dropped and the plugin still loads (source-checked against the installed `BepInEx.Core.dll`).

**Handshake.** The handshake stays three-part. `ctx.Version` and the mod manifest read BepInEx's metadata, so peers exchange `year.month.release` even from a branch build; two builds of a component with the same release match under `Exact` even if one has unmerged changes. Code that passes `MyPluginInfo.PLUGIN_VERSION` to `Handshake.Require`/`Provide` directly (Spike2) sends the full string instead. `NetworkVersionRule.Exact` is case-insensitive string equality. `NetworkVersionRule.Minor` compares major and minor, which for Progenitor versions means **same year and month**; a month boundary is a mismatch, and it does not imply API compatibility. Use a declared capability, not a version compare, to detect a feature.

**Assembly binding.** Bootstrap's Core and plugin resolvers, and BepInEx's own resolver, bind by simple name and ignore the version, so a Core or plugin built against a newer `AssemblyVersion` than the loaded Bootstrap or Core still binds (source-checked; not verified live).

**Deployment.** Builds are deterministic (`Deterministic=true`) and SourceLink is off, so no commit hash reaches an unchanged DLL. A commit that touches only Core leaves Bootstrap's version and bytes unchanged. `tools/deploy.ps1` still stages Bootstrap as `.next` whenever it is in `-Projects` and the process is running (it does not compare Bootstrap bytes); an identical `.next` changes nothing when it is promoted at the next quit, so no restart is needed for it. This was compile-verified: a Release build of Bootstrap before and after a Core-only commit, including a clean rebuild, produced the same SHA-256. Dev and the samples change with every Core change because they reference it.

## Reading a version

- Log: Bootstrap writes `Progenitor Bootstrap <PLUGIN_VERSION> loaded in client process.` (or `server process`) at startup.
- DLL: `(Get-Item Progenitor.dll).VersionInfo.ProductVersion` gives the informational version with the commit.
- Source tree, one component: `dotnet msbuild tools/version/ProgenitorVersion.targets -nologo -t:ProgenitorComputeVersion -p:ProgenitorVersionProjectFile=<full path to .csproj> -getProperty:ProgenitorVersion -getProperty:ProgenitorVersionInputs`
- Source tree, repository version: the same command without `ProgenitorVersionProjectFile`.

A version identifies the source a build came from. It does not prove the deployed game is running that build; check the loaded image as described in [hot-reload.md](hot-reload.md).

## Overrides and failure

| MSBuild property | Effect |
|---|---|
| `ProgenitorVersion` | Explicit version for every component (`-p:ProgenitorVersion=2026.9.12`). Skips git. Must be three or four numbers; anything else fails the build. |
| `ProgenitorVersionProjectFile` | Project whose inputs scope the version. Set to the project being built by `Directory.Build.targets`. |
| `ProgenitorVersionExtraInputs` | Additional input files or directories (`;`-separated, repo-relative or absolute; end a directory with `/`). |
| `ProgenitorMainRefs` | Main refs tried in order. Default `origin/main;main`. |
| `ProgenitorGitExe` | git executable. Default `git` on PATH. |
| `ProgenitorRepoRoot` | Work tree to read. Default: this repository. |

When git is missing, the directory is not a work tree, or no main ref shares history with `HEAD`, the build continues with `0.0.0.0` and a `Progenitor version:` warning. A shallow clone without `origin/main` gets this result; CI checks out full history for that reason. `tools/package.ps1` refuses to package `0.0.0.0`.

## Releases

A release is the clean build of a main merge commit. The **repository version** of that commit (three-part) is the Thunderstore `version_number`, the zip name and the tag name; the DLLs inside keep their own component versions. Build it from a clean detached checkout of that commit. `tools/package.ps1` warns if the repository version has a sub-release number. Thunderstore requires three-part versions, so a four-part package would claim the main build's number and must never be uploaded. `tools/package.ps1 -Version <v>` forces one version onto the package and every DLL.

## Your own plugins

These rules apply to projects built in this repository. A project that uses `MyPluginInfo.PLUGIN_VERSION` (Bootstrap, Core, Dev, Sample, spikes) gets its component version; samples with a literal `[BepInPlugin]` version string keep that string.

A mod built outside this repository chooses its own version. Use three-part `major.minor.patch` in `[BepInPlugin]` so BepInEx, the handshake and Thunderstore all see the same string, and choose `NetworkVersionRule` by what your wire format needs: `Exact` when client and server must be the same build, `Minor` when patch releases stay wire-compatible.

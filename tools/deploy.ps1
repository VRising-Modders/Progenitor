<#
.SYNOPSIS
  Build and copy Progenitor plus any consumer plugin into the client and test-server plugin folders.

.DESCRIPTION
  Copies built DLLs to the client and test-server BepInEx trees.

  The single-folder layout: everything Progenitor itself ships lives in BepInEx/plugins/Progenitor
  (<home> below), exactly as a player's install does.

  - Progenitor.Bootstrap.dll -> <home>/Progenitor.Bootstrap.dll (chainloader; restart)
  - Progenitor.Preloader.dll -> <home>/Progenitor.Preloader.patcher (always; Bootstrap copies it to
                                BepInEx/patchers at its next start when that copy is older) and,
                                while the process is stopped, BepInEx/patchers directly so the next
                                start already runs it; not in the default -Projects
  - Progenitor.dll           -> <home>/progenitor-core/Progenitor.core (loaded by Bootstrap from
                                bytes; in-process watch swap; never a .dll under plugins)
  - address tables           -> <home>/progenitor-core/addresslib (read by Bootstrap before Core loads)
  - everything else          -> BepInEx/progenitor-reload (live)

  If the target process is running, Bootstrap is staged as Progenitor.Bootstrap.dll.next
  (promoted on quit). Core is staged as <home>/progenitor-core/Progenitor.core.next;
  Bootstrap watches that file and swaps generations in-process (docs/dev-loop.md). A file that is
  not there yet (the first deploy into a tree) is written in place, never staged: nothing would
  promote a .next beside a missing file.

  THE THREE-LOCATION LAYOUT (BepInEx/plugins/Progenitor.Bootstrap.dll, BepInEx/progenitor-core).
  A stopped side has the old Bootstrap and the old Core images (Progenitor.dll, .next, .prev)
  removed once the single-folder copies are in place. A running side has the old Bootstrap renamed
  to Progenitor.Bootstrap.dll.removed (a loaded file can be renamed, not deleted) so the next start
  runs the new one; its plugins are withheld, because the running process still loads Core from the
  old folder and no swap can follow. Restart that side.

  DEPLOY ORDERING (roadmap M2.13, jobs FDEP1 and FDEP2, evidence 202 and 219). When this run
  stages a new Core against a RUNNING process, the plugin DLLs are HELD until that swap has
  actually happened: the pre-deploy Core state is read from the bridge (`ping` -> reload.*),
  Core is staged, and the plugins are copied into progenitor-reload only once the generation
  has moved, no swap is in flight, and the last reload says `ok` no earlier than the moment we
  staged. Copying them first is what made a deploy that adds a Core API AND its first consumer
  run the new plugin against the old Core for a second or two, logging one MissingMethodException.

  THE HOLD ARMS ON BYTES, NOT ON THE PROJECT LIST (R66 finding 2). Progenitor.Core is in the
  default -Projects, so "is Core in this run" was true on essentially every deploy, while
  MSBuild only recompiles Core when Core changed. Bootstrap's watcher inspects the staged bytes
  and SKIPS an image identical to the live one WITHOUT moving the generation (CoreWatchPolicy
  .InspectStaged -> SkipIdentical), so the old hold waited the full timeout on a swap that was
  never going to happen -- on the most ordinary plugin-only deploy there is. This script now
  compares the built Progenitor.dll against the live progenitor-core\Progenitor.dll first: equal
  bytes mean the watcher will not move the generation, so nothing is held and the reason is
  printed.

  AND AGAINST THE PENDING .next, NOT ONLY THE LIVE COPY (R67 finding 2). "Equal to live" answers
  "will the generation move"; it does not answer "what is staged", and an unpromoted
  Progenitor.dll.next from an earlier deploy can be a THIRD image. So the comparison is made
  against the pending .next when one exists and against the live copy otherwise: identical to the
  pending .next writes nothing and says that same image is what the next start promotes;
  different from it REPLACES it and says so. Nothing is written only when the image would be
  identical to the file already sitting in that role.

  ON -Side both THE TWO WAITS SHARE ONE DEADLINE (R67 finding 9). Both Cores are staged before
  either wait begins, so the swaps run concurrently; a per-side clock made the worst case
  2 x -CoreSwapTimeoutSeconds, during which the side that had already swapped ran the new Core
  with the previous build's plugins.

  AND NO SIDE CAN SPEND ANOTHER SIDE'S SHARE OF IT (R68 finding 2). Sharing a deadline between
  waits that ran BACK TO BACK only moved the harm: an unanswered poll merely incremented a
  counter, so a side whose Dev bridge had wedged burned the whole window, and the next side
  started with a budget of 0, polled once, and had its plugins copied ahead of a swap nobody
  observed -- on the server, the side with the connected players. The polls are now INTERLEAVED:
  one turn per second, every side still waiting gets that turn, a side's held plugins are copied
  the moment that side's own wait ends, and a side is abandoned after -CoreSwapSilenceSeconds of
  continuous silence with its own warning.

  A PARKED WATCHER ENDS THE WAIT TOO (R68 finding 5). Bootstrap publishes `watch.parked` /
  `watch.parkedReason`: the watcher asked once for the staged image, was refused, and will not
  read that file again until its bytes change or the process restarts (job FREL8). Two of the
  refusals it parks on leave `previousAlive` false and `lastReload.outcome` a stale `ok`, so the
  refusal test alone cannot see them. Deploy reads both fields, before staging (parked plus a
  pending .next is reported like the refusal: only a restart takes that image) and on every poll.

  AND THE POLL ITSELF IS BOUNDED, IN SECONDS (R69 finding 2). FDEP4's round-robin and its
  six-consecutive-misses abort both assumed a bridge that fails fast. `tools/mcp/call.mjs` waits
  30 s for a reply and deploy never overrode it, so the WEDGED bridge the abort is named after --
  one that accepts the connection and then writes nothing -- took ~180 s to reach six misses, past
  the 90 s deadline, and held the other side's turn for 30 s at a time inside the same synchronous
  foreach. Now: every poll is bounded by -CoreSwapPollTimeoutSeconds (PROGENITOR_CALL_TIMEOUT_MS),
  the abort threshold is -CoreSwapSilenceSeconds of continuous SILENCE rather than a count of
  polls, the deadline is recomputed per side inside the turn loop, and the turn order rotates so
  no side is permanently first.

  A PARK IS ONLY THIS RUN'S PARK IF THE WATCHER LOOKED AFTER WE STAGED (R69 finding 7). `watch
  .parked` is refreshed on the watcher's own one-second tick, and that tick does not run while the
  game's frame loop is stalled, so a park published for the PREVIOUS image can still be on the
  wire when the first poll lands. The wait now requires either a watcher stamp at or after this
  run's staging (`watch.parkedAtUtc` / `watch.scanUtc`, when a Bootstrap that publishes one is
  running) or the park standing on two separate polls spanning at least one watcher scan. Where
  neither holds, the park is reported as a WAIT-ON note and the wait continues, because releasing
  the plugins on the previous image's park is the very release the hold exists to prevent.

  A REFUSED SWAP ENDS THE WAIT AT ONCE (R66 finding 1, evidence 219). Once a Core generation has
  failed to collect, Bootstrap keeps it alive and refuses every later swap until the process
  restarts (CoreReloadFrameDriver.Request -> RefusePreviousAlive). That state is on the wire as
  `reload.previousAlive` and `reload.lastReload.outcome = "refused"`, so the hold reads both and
  stops on either with a message that says what is true: the swap was refused and the process
  must restart.

  A HOSTED PRIVATE GAME REFUSES THE SWAP TOO. A client that is hosting a private game (and the
  hosted VRisingServer child itself) never takes a staged Core: the watcher logs "Core watch
  refused: this client is hosting a private game; the child keeps the copied Core." once and
  leaves Progenitor.dll.next on disk, unparked, with no lastReload record. The only signal on the
  wire is `reload.watch.hostingPrivateGame` (`reload.watch.hostedChild` for the child), so the
  hold reads both, before staging and on every poll.

  ONLY A SWAP THAT LANDED RELEASES THE PLUGINS (deploy-hold defect, 2026-10-06; supersedes the
  "copy anyway" rule of FDEP1-FDEP4 and R66 finding 11). The old rule copied the held plugins on
  every other ending, on the theory that one MissingMethodException was the worst outcome. It is
  not: a deploy run while the client hosted a private game released a Dev built against the new
  Core into the old one, Dev's load threw TypeLoadException, and its bridge gate stayed busy until
  the process restarted. So when this run stages a Core that is not running, and the swap is
  refused, parked, quarantined, held off by a hosted game, already contended, unobservable (no
  bridge, watcher off), not observed by the deadline, or the wait is interrupted, the dependent
  plugins are WITHHELD: they are not copied into progenitor-reload. The Core stays staged, the
  script prints why and what to do next (leave the hosted game, or restart that side, then run
  the same deploy again), and it exits 3. Re-running after the swap lands finds Core identical to
  the live copy and copies the plugins straight away. -ReleaseHeldPlugins restores the old
  copy-anyway behavior for a run that needs it (for example to replace a broken Dev bridge),
  with the same warning. Nothing is held when Core is not part of this run, the Core bytes did not
  change, or the process is not running.

  Never deploys to C:\VRisingServer.

  What deploy does NOT create, and must not (docs/dev-loop.md, "Deploy audit 2026-09-06"):
  BepInEx/config/progenitor-allow.txt (an operator artifact — creating it would turn a
  fail-open tree into a fail-closed one behind the operator's back), BepInEx/progenitor-pending/
  (written by Core when a server package is accepted), <plugin>.delta / <plugin>.delta-state
  (tools/enc and the reloader), build-<side>-<build>.json (the Resilience fingerprint Core
  writes beside the tables), and BepInEx/config/progenitor-dev.enable (the Dev opt-in marker,
  written by hand, once per tree).

.PARAMETER Projects
  Project names under src/ or samples/ to deploy. Defaults to Bootstrap, Core, Dev, and the Sample.
  Progenitor.Preloader is never in the default: BepInEx loads BepInEx/patchers before the
  chainloader and nothing promotes a staged .next there, so it is copied only to a side whose
  process is stopped (a running side is skipped with a warning) and takes effect at the next start.

.PARAMETER Side
  client, server, or both. Defaults to both.

.PARAMETER NoBuild
  Skip dotnet build. The layout check still runs: it is about the game's bytes, not ours.

.PARAMETER DumpDir
  Folder holding the game's exported metadata, one folder per side (server\, client\), each with
  one C#-shaped type listing (*.cs). When it is present, deploy runs `layoutgen --check` against it
  and refuses to deploy on exit 2 -- see below. When it is absent (any machine without the
  exported metadata, CI included) the check is skipped with a note. Defaults to
  PROGENITOR_METADATA_DIR, else <workspace>\_tools\interop-dump; -TestRoot defaults to <workspace>\_testserver. The
  workspace is the folder holding the main checkout (tools/common/Workspace.ps1; override with
  PROGENITOR_WORKSPACE).

.PARAMETER CoreSwapTimeoutSeconds
  How long to hold the plugin copies while a staged Core swap completes (default 90). 0 turns the
  hold off entirely and restores the pre-FDEP1 behavior (plugins first, Core staged after).

.PARAMETER CoreSwapPollSeconds
  How often the hold asks the bridge (default 5). `ping` is a MAIN-THREAD op
  (src/Progenitor.Dev/Handlers.cs, MainThreadOps), so every poll takes the bridge's single
  main-thread slot on a game frame; the wait watches the cheap thing (the staged .next file
  disappearing) once a second and only pings on that event or on this heartbeat.

.PARAMETER CoreSwapPollTimeoutSeconds
  How long ONE bridge poll may take before it counts as unanswered (default 3). Passed to
  tools/mcp/call.mjs as PROGENITOR_CALL_TIMEOUT_MS. call.mjs's own default is 30 s, which is what
  made the abort below unreachable (R69 finding 2): a wedged bridge accepts the connection and
  never answers, so the timer -- not a connection error -- ends the call, and at 30 s a poll also
  held the OTHER side's round-robin turn for that long. Bounded here, a turn costs at most this
  many seconds whatever the bridge does.

.PARAMETER CoreSwapSilenceSeconds
  How many seconds of continuous silence from one side's bridge end that side's wait (default 30).
  Expressed in SECONDS, not in a count of polls (R69 finding 2): the old six-consecutive-misses
  threshold meant 30 s with a fast-failing bridge and 180 s with a hanging one -- twice the whole
  90 s window -- so the abort never fired in the case it was written for. Silence is measured from
  the last answered poll (or the start of the wait), so it means the same thing however long an
  individual poll took. Clamped to the shared window and never shorter than one poll timeout.

.PARAMETER ReleaseHeldPlugins
  Copy the plugins anyway when the staged Core swap did not land (the pre-2026-10-06 behavior).
  Without it those plugins are withheld and the script exits 3 (see "ONLY A SWAP THAT LANDED
  RELEASES THE PLUGINS" above). Use it only when the plugins are known to load against the Core
  that is running, for example a Dev rebuild that touches no new Core API.

.PARAMETER WhatIf
  Plan only. Prints every file that would be copied with its sha256 prefix and destination,
  and writes nothing at all -- no build, no config seeding, no copy, no delete. Use it to
  preview a restart window's deploy:

    pwsh tools/deploy.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [string[]]$Projects = @("Progenitor.Bootstrap", "Progenitor.Core", "Progenitor.Dev", "Progenitor.Sample"),
  [ValidateSet("client", "server", "both")] [string]$Side = "both",
  [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\VRising",
  [string]$TestRoot = "",   # default <testserver> = <workspace>\_testserver (tools/common/Workspace.ps1)
  [string]$DumpDir = "",    # default $env:PROGENITOR_METADATA_DIR, else <workspace>\_tools\interop-dump
  [ValidateRange(0, 3600)] [int]$CoreSwapTimeoutSeconds = 90,
  [ValidateRange(1, 60)] [int]$CoreSwapPollSeconds = 5,
  [ValidateRange(1, 120)] [int]$CoreSwapPollTimeoutSeconds = 3,
  [ValidateRange(1, 3600)] [int]$CoreSwapSilenceSeconds = 30,
  [switch]$ReleaseHeldPlugins,
  [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

# The one fence (tools/common/Fence.ps1, R40 finding 4). Asserted on the two root PARAMETERS
# before anything else runs -- before the build, before the targets are derived, before the
# first Test-Path -- and again below on every derived root once the targets exist. The old
# `-like "C:\VRisingServer*"` here saw the raw parameter string, so `-GameDir C:/VRisingServer`
# passed it; Assert-NotLiveServerPath canonicalizes first (docs/dev-loop.md, "Deploy audit").
. (Join-Path $PSScriptRoot "common\Fence.ps1")
# Fence.ps1 dot-sources common\Workspace.ps1. Defaults come from the workspace resolver, never a
# machine literal; an unresolvable workspace throws here and names the parameter to pass.
if (-not $TestRoot) { $TestRoot = Resolve-ProgenitorWorkspacePath -Child "_testserver" -Parameter "-TestRoot" }
if (-not $DumpDir -and $env:PROGENITOR_METADATA_DIR) { $DumpDir = $env:PROGENITOR_METADATA_DIR }
if (-not $DumpDir) { $DumpDir = Resolve-ProgenitorWorkspacePath -Child "_tools\interop-dump" -Parameter "-DumpDir" }
Assert-NotLiveServerPath $GameDir
Assert-NotLiveServerPath $TestRoot

# One flag for the whole script. -WhatIf comes from SupportsShouldProcess; every write below
# goes through Invoke-Deploy* / Add-PlanRow, which consult it. A WhatIf run must be readable
# against a live tree, so it does not build either (a build writes into bin/).
$plan = [bool]$WhatIfPreference
$script:PlanRows = New-Object System.Collections.Generic.List[object]

function Get-Sha256Prefix([string]$path) {
  # Same 16-hex-char shape the load lines and SHA256SUMS use, so a planned row can be eyeballed
  # against `sha256=…` in the BepInEx log or a row in packaging/out/SHA256SUMS.
  # Hashed through System.Security.Cryptography directly, not Get-FileHash: under -WhatIf the
  # cmdlet's own pipeline is short-circuited by $WhatIfPreference and returns nothing, which is
  # how a plan listing ends up with a row of dashes where the sha256 should be.
  try {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
      $stream = [System.IO.File]::OpenRead($path)
      try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
    return ([System.BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 16).ToLowerInvariant()
  } catch {
    return "----------------"
  }
}

function Get-Sha256Full([string]$path) {
  # Full digest, same hand-rolled route as Get-Sha256Prefix (Get-FileHash is short-circuited
  # under -WhatIf). Empty string when the file cannot be read, which never compares equal.
  try {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
      $stream = [System.IO.File]::OpenRead($path)
      try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
    return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
  } catch {
    return ""
  }
}

function Test-SameFileBytes([string]$left, [string]$right) {
  <#
    True when both files exist, have the same length and the same sha256. This is the question
    Bootstrap's CoreWatchPolicy.InspectStaged asks of the staged image (BytesIdentical over the
    two buffers); asking it here is what keeps a plugin-only deploy from staging a Core that the
    watcher will skip and then waiting for a generation move that cannot come (R66 finding 2).
  #>
  if (-not (Test-Path -LiteralPath $left) -or -not (Test-Path -LiteralPath $right)) { return $false }
  try {
    if ((Get-Item -LiteralPath $left).Length -ne (Get-Item -LiteralPath $right).Length) { return $false }
  } catch { return $false }
  $a = Get-Sha256Full $left
  if (-not $a) { return $false }
  return $a -eq (Get-Sha256Full $right)
}

function Add-PlanRow([string]$sideName, [string]$project, [string]$src, [string]$dest, [string]$note = "") {
  $script:PlanRows.Add([pscustomobject]@{
    Side    = $sideName
    Project = $project
    Sha256  = (Get-Sha256Prefix $src)
    Source  = $src
    Dest    = $dest
    Note    = $note
  })
}

function Invoke-DeployNewDir([string]$path) {
  if ($plan) { return }
  New-Item -ItemType Directory -Force -Path $path | Out-Null
}

function Invoke-DeployRemove([string]$path, [switch]$Recurse) {
  if ($plan) { return }
  if ($Recurse) { Remove-Item -LiteralPath $path -Recurse -Force } else { Remove-Item -LiteralPath $path -Force }
}

function Invoke-DeployCopy([string]$src, [string]$dest, [string]$sideName, [string]$project, [string]$note = "") {
  if ($plan) { Add-PlanRow $sideName $project $src $dest $note; return }
  Copy-Item -LiteralPath $src -Destination $dest -Force
}

function Invoke-DeployCopyTree([string]$srcDir, [string]$destDir, [string]$sideName, [string]$project, [string]$note = "") {
  if ($plan) {
    foreach ($f in @(Get-ChildItem -LiteralPath $srcDir -Recurse -File -ErrorAction SilentlyContinue)) {
      $rel = $f.FullName.Substring($srcDir.Length).TrimStart('\')
      Add-PlanRow $sideName $project $f.FullName (Join-Path $destDir $rel) $note
    }
    return
  }
  Copy-Item $srcDir $destDir -Recurse -Force
}

if (-not $NoBuild) {
  if ($plan) {
    Write-Host "[plan] skipping dotnet build (-WhatIf writes nothing); planning against the existing bin/Debug output"
  } else {
    & dotnet build (Join-Path $repo "Progenitor.sln") -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
  }
}

function Assert-LayoutConstantsAgreeWithTheDump {
  <#
    The layout gate (R53 finding 1, job FLAY2). `tools/layoutgen --check` resolves every offset,
    stride and enum value in src/Progenitor.Core/Layout/layout.txt against the game's own
    metadata and exits 2 when one disagrees OR when the dump can no longer find the type or field it
    names -- the shape a game update takes when Stunlock renames something. Each of those numbers
    is a raw memory write, so shipping past a red check is shipping a wrong-memory write.

    Why HERE and not in CI: the dump is 127 MB and lives outside the repository by decision,
    so .github/workflows can only
    ever run the tool's game-free half -- which the test suite already covers
    (LayoutConstantTests). Deploy is the one entry point that runs on a machine that HAS the dump
    and is about to put those constants in front of the game, so deploy is the gate. It is skipped,
    loudly, wherever the dump is not present.

    It writes nothing into a game tree (--check never rewrites a value; a failing run does not
    even emit the generated file), and it runs even under -NoBuild, because -NoBuild means "do not
    rebuild the mod" and not "skip the check that the mod is safe to load". tools/layoutgen is
    not in Progenitor.sln, so "dotnet run" builds that one small tool itself. The single skip is
    -WhatIf, which is documented as writing nothing at all, a build included.

    THE ORACLE IS CHECKED TOO (R54 finding 1, job FLAY3). The dump is a hand-exported metadata
    artifact that no game update, no build and no script regenerates, so `Test-Path` says only
    that SOME dump is on this machine -- not that it describes the game this deploy is about to
    write into. On the first patch day that difference is the whole gate: every declaration
    resolves against the previous build, the tool prints "the dump agrees with all 43 values",
    and 43 raw memory offsets ship unchecked. So each side's GameAssembly.dll is passed with
    --game-assembly, from the SAME roots this script deploys into ($GameDir and $TestRoot, both
    already past the live-server fence), and layoutgen exits 2 when the dump is older than that
    binary or the binary is not the one the generated header recorded. Deploy refuses on that
    exit code exactly as it refuses on a disagreement -- and it refuses up front if a side's
    binary is missing, because a missing binary means the path this script believes in is wrong,
    and a freshness gate handed a path nobody checked is not a gate.

    AND THE RECORD IS CHECKED TOO (R55 finding 4, job FLAY4). Passing --game-assembly here was
    never enough on its own: the recorded-identity half compares the binary against the `game:`
    line in the checked-in generated header, and for one commit that line did not exist -- the
    file had never been regenerated with --game-assembly, --check never writes a file so it could
    not add one, and the staleness diff deliberately ignores the line so it never noticed the
    absence. The comparison silently did nothing while this gate printed green. layoutgen now
    exits 2 when a side named here has no recorded identity at all, so the arguments below cannot
    be present-but-inert again; its refusal names the regenerate command rather than a re-dump,
    because in that case the dump was never the problem. Nothing about the arguments this
    function passes changed -- what changed is that they now have something to compare against.
  #>
  $declarations = Join-Path $repo "src\Progenitor.Core\Layout\layout.txt"
  $generated = Join-Path $repo "src\Progenitor.Core\Layout\Generated"
  # Each side's type listing is the one *.cs file in its folder (the largest, if there are several).
  function Get-TypeListing([string]$sideDir) {
    if (-not (Test-Path $sideDir)) { return (Join-Path $sideDir "type-listing.cs") }
    $listing = Get-ChildItem -Path $sideDir -Filter *.cs -File | Sort-Object Length -Descending | Select-Object -First 1
    if ($listing) { return $listing.FullName }
    return (Join-Path $sideDir "type-listing.cs")
  }
  $serverDump = Get-TypeListing (Join-Path $DumpDir "server")
  $clientDump = Get-TypeListing (Join-Path $DumpDir "client")

  # The binary each dump claims to describe. Derived from this run's own roots, never hard-coded:
  # the client's GameAssembly.dll sits in the game root, the test server's in its server folder.
  $serverBinary = Join-Path $TestRoot "server\GameAssembly.dll"
  $clientBinary = Join-Path $GameDir "GameAssembly.dll"

  $checkArgs = @("--declarations", $declarations, "--out", $generated, "--check")
  $sides = @()
  if ((Test-Path $serverDump) -and -not (Test-Path (Join-Path $TestRoot "server"))) {
    # No test-server tree on this machine at all: the server dump cannot be shown to describe
    # anything here, so it is not used. Skipping the dump is safe (a side with no dump is never a
    # failure); using it unchecked is the hole this gate exists to close.
    Write-Host "[layout] no server tree under $TestRoot; the server dump is not used (its freshness cannot be established here)"
    $serverDump = ""
  }
  if ((Test-Path $clientDump) -and -not (Test-Path $GameDir)) {
    Write-Host "[layout] no game tree at $GameDir; the client dump is not used (its freshness cannot be established here)"
    $clientDump = ""
  }

  if ($serverDump -and (Test-Path $serverDump)) {
    if (-not (Test-Path $serverBinary)) {
      throw ("layout check cannot run: there is a server dump at $serverDump but no " +
        "GameAssembly.dll at $serverBinary, so nothing can say which game build that dump " +
        "describes. Point -TestRoot at the staged test server (or remove the stale dump). " +
        "Nothing was deployed.")
    }
    $checkArgs += @("--dump-server", $serverDump, "--game-assembly", "server=$serverBinary")
    $sides += "server"
  }
  if ($clientDump -and (Test-Path $clientDump)) {
    if (-not (Test-Path $clientBinary)) {
      throw ("layout check cannot run: there is a client dump at $clientDump but no " +
        "GameAssembly.dll at $clientBinary, so nothing can say which game build that dump " +
        "describes. Point -GameDir at the game install (or remove the stale dump). " +
        "Nothing was deployed.")
    }
    $checkArgs += @("--dump-client", $clientDump, "--game-assembly", "client=$clientBinary")
    $sides += "client"
  }
  if (-not $sides) {
    Write-Host "[layout] no dump under $DumpDir; layout constants unchecked against the game (declarations-only half is covered by dotnet test)"
    return
  }

  & dotnet run --project (Join-Path $PSScriptRoot "layoutgen") -- @checkArgs
  if ($LASTEXITCODE -eq 2) {
    throw ("layout check failed for the $($sides -join ' and ') side(s): the dump under $DumpDir " +
      "disagrees with src/Progenitor.Core/Layout/layout.txt, no longer contains something it " +
      "names, or is STALE -- older than the GameAssembly.dll it claims to describe, taken " +
      "from a different build than the generated header records, or checked against a header " +
      "that records no game identity for that side at all (the lines above say which, and name " +
      "the procedure: a re-dump for a stale dump, a regenerate for a missing game: line). " +
      "Every constant in that file is a raw memory write. If the game updated, re-export that " +
      "side's metadata from its GameAssembly.dll and global-metadata.dat into $DumpDir\<side>, " +
      "re-verify each declaration, regenerate " +
      "with tools/layoutgen, and re-run. Nothing was deployed.")
  }
  if ($LASTEXITCODE -ne 0) {
    Write-Warning "[layout] layoutgen exited $LASTEXITCODE (not a drift verdict); layout constants unchecked"
  }
}

if ($plan) { Write-Host "[plan] skipping the layout dump check (-WhatIf writes nothing and builds nothing)" }
else { Assert-LayoutConstantsAgreeWithTheDump }

function Set-BepInExCfgEntry {
  <#
    Write one entry into a BepInEx .cfg, creating the file or the section if needed and
    leaving every other entry alone. BepInEx re-writes the file on the next launch, keeping
    the value found here for a key the plugin binds.

    Job FRLD: Reload.WatchCore now ships FALSE (an automatic code-loading path must be
    opt-in for people who install a release). Our own client and test-server trees are
    developer trees, so deploy writes WatchCore = true into them explicitly and the hot
    swap loop is unchanged. Never called for C:\VRisingServer: deploy refuses that root.

    Never touches BepInEx/config/progenitor-allow.txt: the allowlist is an operator artifact
    and deploy must never create one (a tree with no allowlist is fail-open by design; a tree
    with one deploy wrote would silently start refusing images deploy did not list).
  #>
  param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Section,
    [Parameter(Mandatory = $true)][string]$Key,
    [Parameter(Mandatory = $true)][string]$Value
  )
  $dir = Split-Path $Path -Parent
  $utf8 = New-Object System.Text.UTF8Encoding $false
  $entry = "$Key = $Value"

  if (-not (Test-Path -LiteralPath $Path)) {
    if ($plan) { return "would create" }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $lines = @("## Written by tools/deploy.ps1: this is a developer tree.", "[$Section]", $entry)
    [System.IO.File]::WriteAllLines($Path, $lines, $utf8)
    return "created"
  }

  $lines = [System.Collections.Generic.List[string]](Get-Content -LiteralPath $Path)
  $header = -1
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].Trim() -eq "[$Section]") { $header = $i; break }
  }
  if ($header -lt 0) {
    if ($plan) { return "would add section" }
    $lines.Add("")
    $lines.Add("[$Section]")
    $lines.Add($entry)
    [System.IO.File]::WriteAllLines($Path, $lines, $utf8)
    return "section added"
  }

  for ($i = $header + 1; $i -lt $lines.Count; $i++) {
    $t = $lines[$i].Trim()
    if ($t.StartsWith("[") -and $t.EndsWith("]")) { break }
    if ($t -match "^$([regex]::Escape($Key))\s*=") {
      if ($t -eq $entry) { return "already set" }
      if ($plan) { return "would update" }
      $lines[$i] = $entry
      [System.IO.File]::WriteAllLines($Path, $lines, $utf8)
      return "updated"
    }
  }
  if ($plan) { return "would add" }
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $lines.Insert($header + 1, $entry)
  [System.IO.File]::WriteAllLines($Path, $lines, $utf8)
  return "added"
}

function Test-Running([string]$exeName, [string]$underDir) {
  # Match on path, not just name: the live dedicated server is also VRisingServer.exe.
  $procs = Get-Process -Name $exeName -ErrorAction SilentlyContinue
  foreach ($p in $procs) {
    $path = $null
    try { $path = $p.Path } catch {}
    if ($path -and $path -like "$underDir*") { return $true }
  }
  return $false
}

function Get-AddressTables {
  <#
    The address tables deploy may copy: tools/addresslib/{client,server}-<build>.json and nothing
    else. The folder is a build output (its .gitignore hides *.json), so anything else that lands
    there is not a table and must not be pushed into a game tree:

      build-<side>-<build>.json  the Resilience fingerprint Core WRITES into that folder at runtime
                                 (Resilience/ResilienceCheck.cs). Shipping one would hand Core a
                                 baseline it did not capture and mute a real drift diff.
      driftmap-<build>.json      the compat rewriter's map. It is vetted through the allowlist and
                                 placed by hand; deploy must not push one (none expected today).
  #>
  $src = Join-Path $PSScriptRoot "addresslib"
  return @(Get-ChildItem -Path $src -Filter "*.json" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "client-*.json" -or $_.Name -like "server-*.json" })
}

function Copy-ProgenitorGuide($t) {
  <#
    The Mods menu's Help guide tile and Progenitor's own Help button open
    progenitor-core/docs/guide/progenitor-guide.html (ModHelp.ProgenitorGuidePath) and stay hidden
    when it is missing. package.ps1 ships it; a dev deploy copies it too so deployed builds match.
  #>
  $src = Join-Path (Split-Path $PSScriptRoot -Parent) "docs\guide\progenitor-guide.html"
  if (-not (Test-Path -LiteralPath $src)) { return }
  $destDir = Join-Path $t.Core "docs\guide"
  $final = Join-Path $destDir "progenitor-guide.html"
  if ($plan) { Add-PlanRow $t.Name "guide" $src $final "Progenitor guide"; return }
  Invoke-DeployNewDir $destDir
  try {
    Copy-Item -LiteralPath $src -Destination $final -Force
    Write-Host "[$($t.Name)] Progenitor guide -> progenitor-core/docs/guide"
  } catch {
    Write-Warning "[$($t.Name)] Progenitor guide not updated: $($_.Exception.Message)"
  }
}

function Copy-AddressTables($t) {
  <#
    Bootstrap resolves its detour sites from the JSON next to Core (progenitor-core/addresslib)
    BEFORE Core is loaded, so the tables must sit on disk in every tree, on every deploy --
    not only when Progenitor.Core happens to be in -Projects. A Bootstrap-only deploy that left
    a stale table behind is a restart spent on the wrong addresses.
  #>
  $tables = Get-AddressTables
  if (-not $tables) {
    Write-Warning "[$($t.Name)] no address tables under tools/addresslib (run: node tools/addresslib/build.mjs --side client|server); detours fall back to the prefix scan"
    return
  }
  $tablesDest = Join-Path $t.Core "addresslib"
  Invoke-DeployNewDir $tablesDest
  $copied = 0
  foreach ($table in $tables) {
    $final = Join-Path $tablesDest $table.Name
    if ($plan) { Add-PlanRow $t.Name "addresslib" $table.FullName $final "address table"; $copied++; continue }
    # Write each table to a temp name and swap it in, so a locked or interrupted copy never leaves a
    # half-written table next to Core (Bootstrap reads the folder at the next start).
    $tmp = "$final.tmp"
    try {
      Copy-Item $table.FullName $tmp -Force
      Move-Item $tmp $final -Force
      $copied++
    } catch {
      if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
      Write-Warning "[$($t.Name)] address table $($table.Name) not updated: $($_.Exception.Message)"
    }
  }
  Write-Host "[$($t.Name)] $copied of $($tables.Count) address table(s) -> progenitor-core/addresslib"
}

function Remove-StaleChainloaderCore([string]$pluginsDir, [string]$sideName, [bool]$running) {
  # Step C: Core must not sit in plugins/. A leftover Progenitor.dll there is LoadFrom'd by
  # the chainloader and steals the Progenitor identity before Bootstrap can load progenitor-core.
  $stale = Join-Path $pluginsDir "Progenitor.dll"
  foreach ($p in @($stale, "$stale.next", "$stale.prev")) {
    if (-not (Test-Path $p)) { continue }
    if ($running -and ($p -eq $stale)) {
      Write-Warning "[$sideName] stale Progenitor.dll still in plugins (process running; remove on next start)"
      continue
    }
    if ($plan) { Write-Host "[plan][$sideName] would remove stale $(Split-Path $p -Leaf) from plugins"; continue }
    try {
      Remove-Item $p -Force
      Write-Host "[$sideName] removed stale $(Split-Path $p -Leaf) from plugins (Core lives in progenitor-core)"
    } catch {
      Write-Warning "[$sideName] could not remove stale $(Split-Path $p -Leaf): $_"
    }
  }
}

function Copy-RestartOnly([string]$src, [string]$dest, [string]$sideName, [string]$procName, [bool]$running, [string]$project) {
  $destDir = Split-Path $dest -Parent
  Invoke-DeployNewDir $destDir
  # A .next is only promoted beside a live file; the first copy into a tree is written in place.
  if ($running -and (Test-Path -LiteralPath $dest)) {
    Invoke-DeployCopy $src "$dest.next" $sideName $project "staged (.next; $procName is running)"
    if (-not $plan) { Write-Host "[$sideName] $(Split-Path $src -Leaf) staged as .next ($procName is running)" }
  } else {
    if (Test-Path "$dest.next") { Invoke-DeployRemove "$dest.next" }
    Invoke-DeployCopy $src $dest $sideName $project "$procName not running"
    if (-not $plan) { Write-Host "[$sideName] $(Split-Path $src -Leaf) deployed" }
  }
}

function Copy-ShippedPreloader([string]$src, [string]$homeDir, [string]$sideName, [string]$project) {
  # The copy a player's install ships: beside Bootstrap, under a name no BepInEx scan loads. Nothing
  # holds it, so it is always written; Bootstrap places it in BepInEx/patchers at its next start.
  Invoke-DeployNewDir $homeDir
  Invoke-DeployCopy $src (Join-Path $homeDir "Progenitor.Preloader.patcher") $sideName $project "shipped preloader"
  if (-not $plan) { Write-Host "[$sideName] $(Split-Path $src -Leaf) -> plugins/Progenitor/Progenitor.Preloader.patcher" }
}

function Remove-LegacyLayout($t) {
  <#
    The three-location layout, once this tree has the single-folder copies: the plugins-root Bootstrap
    and the BepInEx/progenitor-core Core images. A stopped side loses both. A running side has the old
    Bootstrap renamed aside (the next start then runs the single-folder one) and the old Core left for
    the running process; Bootstrap reports that folder as ignored from the next start on.
  #>
  $legacyBoot = Join-Path $t.Dir "Progenitor.Bootstrap.dll"
  $legacyCoreDir = Join-Path (Split-Path $t.Dir -Parent) "progenitor-core"
  $homeBoot = Join-Path $t.Home "Progenitor.Bootstrap.dll"
  $homeCore = Join-Path $t.Core $t.CoreFile
  if ((Test-Path -LiteralPath $homeBoot) -or $plan) {
    foreach ($p in @($legacyBoot, "$legacyBoot.next", "$legacyBoot.prev")) {
      if (-not (Test-Path -LiteralPath $p)) { continue }
      if ($plan) { Write-Host "[plan][$($t.Name)] would remove the three-location $(Split-Path $p -Leaf) from plugins"; continue }
      try {
        if ($t.Running -and $p -eq $legacyBoot) {
          $aside = "$p.removed"
          if (Test-Path -LiteralPath $aside) { Remove-Item -LiteralPath $aside -Force }
          Move-Item -LiteralPath $p -Destination $aside -Force
          Write-Warning ("[$($t.Name)] renamed the running three-location Bootstrap to $(Split-Path $aside -Leaf); " +
            "restart this side to run plugins/Progenitor/Progenitor.Bootstrap.dll")
        } else {
          Remove-Item -LiteralPath $p -Force
          Write-Host "[$($t.Name)] removed the three-location $(Split-Path $p -Leaf) from plugins"
        }
      } catch {
        Write-Warning "[$($t.Name)] could not remove the three-location $(Split-Path $p -Leaf): $_"
      }
    }
  }
  if ($t.Running -or -not ((Test-Path -LiteralPath $homeCore) -or $plan)) { return }
  foreach ($name in @("Progenitor.dll", "Progenitor.dll.next", "Progenitor.dll.prev")) {
    $p = Join-Path $legacyCoreDir $name
    if (-not (Test-Path -LiteralPath $p)) { continue }
    if ($plan) { Write-Host "[plan][$($t.Name)] would remove the three-location progenitor-core\$name"; continue }
    try {
      Remove-Item -LiteralPath $p -Force
      Write-Host "[$($t.Name)] removed the three-location progenitor-core\$name (Core lives in plugins/Progenitor/progenitor-core)"
    } catch {
      Write-Warning "[$($t.Name)] could not remove the three-location progenitor-core\${name}: $_"
    }
  }
}

function Copy-Preloader([string]$src, [string]$destDir, [string]$sideName, [string]$procName, [bool]$running, [string]$project) {
  # BepInEx loads BepInEx/patchers before the chainloader, and nothing promotes a .next there, so
  # there is no staging: a running process holds the file and would never pick up a staged copy.
  if ($running) {
    Write-Warning ("[$sideName] $(Split-Path $src -Leaf) NOT deployed: $procName is running. The preloader " +
      "needs this side stopped; deploy again then, and it is picked up at the next start.")
    return
  }
  Invoke-DeployNewDir $destDir
  Invoke-DeployCopy $src (Join-Path $destDir (Split-Path $src -Leaf)) $sideName $project "patchers; $procName not running"
  if (-not $plan) { Write-Host "[$sideName] $(Split-Path $src -Leaf) -> BepInEx/patchers (takes effect at the next start)" }
}

function Get-CoreStatus([hashtable]$t, [int]$TimeoutSeconds = 0) {
  <#
    One `ping` through the debug bridge, reduced to what the deploy ordering needs. The whole
    reload block is read, not three fields of it (R66 finding 1): `previousAlive` and
    `lastReload.outcome` are how a process that has REFUSED every further swap says so, and
    dropping them is what made a refusal look identical to a swap that had not happened yet.

    `ping` is a MAIN-THREAD op -- it is the first entry in Handlers.MainThreadOps
    (src/Progenitor.Dev/Handlers.cs:22) -- so each call takes the bridge's one main-thread slot
    on a game frame and bounces a concurrent status/users/chat_observe for that moment. It does
    not delay the swap cascade (that runs from Bootstrap's FrameTicker and a poll landing
    mid-cascade fails cleanly), but it is not free either, so the hold pings on the
    -CoreSwapPollSeconds heartbeat rather than once a second.

    Read-only: ping schedules nothing. The token file is derived from THIS run's own roots
    (already past the live-server fence) and only when the caller has not set the override
    the hosted child uses; tools/mcp/call.mjs applies the same refusal to it again.

    -TimeoutSeconds BOUNDS THE CALL (R69 finding 2). call.mjs waits 30 s by default, and its timer
    is what ends a call to a bridge that accepted the connection and then went quiet -- the exact
    failure the swap hold's abort is written for. Every caller here passes a bound: the hold's
    polls a few seconds, the pre-staging read a longer one (it happens once per side and a missed
    answer there costs the whole hold). 0 means "leave call.mjs's own default alone". The override
    is set and restored around the one call, like the token file above, so it cannot leak into a
    later node invocation in this process.
  #>
  $status = [pscustomobject]@{
    Ok = $false
    Generation = $null
    InFlight = $false
    WatchEnabled = $true
    PendingNext = $false
    WatchParked = $false
    WatchParkedReason = ""
    HostingPrivateGame = $false
    HostedChild = $false
    # When the running Bootstrap publishes one, the moment the watcher last looked (R69 finding 7).
    # $null on every Bootstrap that does not, which is every one built before this is written.
    WatchStampUtc = $null
    PreviousAlive = $false
    LastOutcome = ""
    LastReason = ""
    LastTimestampUtc = $null
    PinDiag = ""
    # Job FINJ1: reload.processLifetimePlugins -- reload-folder plugins that registered their own
    # IL2CPP types. Never unloaded in place; a coreBound one makes every Core swap refuse. Empty on
    # an older Bootstrap (the field is absent).
    ProcessLifetime = @()
    Error = ""
  }
  $call = Join-Path $PSScriptRoot "mcp\call.mjs"
  if (-not (Test-Path $call)) { $status.Error = "tools/mcp/call.mjs is missing"; return $status }

  $tokenVar = if ($t.Name -eq "client") { "PROGENITOR_CLIENT_TOKEN_FILE" } else { "PROGENITOR_SERVER_TOKEN_FILE" }
  $preset = [Environment]::GetEnvironmentVariable($tokenVar)
  if (-not $preset) {
    $tokenPath = Join-Path $t.Config "progenitor-dev.token"
    Assert-NotLiveServerPath $tokenPath
    [Environment]::SetEnvironmentVariable($tokenVar, $tokenPath)
  }
  # Bound this one call. An explicit PROGENITOR_CALL_TIMEOUT_MS from the caller's environment wins
  # (someone debugging a slow bridge by hand), otherwise the bound this caller asked for is set for
  # the duration of the node process and removed again below.
  $timeoutPreset = [Environment]::GetEnvironmentVariable("PROGENITOR_CALL_TIMEOUT_MS")
  $setTimeout = ($TimeoutSeconds -gt 0) -and -not $timeoutPreset
  if ($setTimeout) {
    [Environment]::SetEnvironmentVariable("PROGENITOR_CALL_TIMEOUT_MS", [string]($TimeoutSeconds * 1000))
  }
  # No bridge-queue mail on stderr for this parsed call (docs/bridge.md#bridge-queue); restored below.
  $callNotesPreset = [Environment]::GetEnvironmentVariable("PROGENITOR_CALL_NOTES")
  [Environment]::SetEnvironmentVariable("PROGENITOR_CALL_NOTES", "0")
  try {
    # stderr is MERGED, not discarded (R66 finding 4): `2>$null` threw away the only text
    # call.mjs writes when it cannot connect, leaving every failure to read as the same
    # "bridge ping exited 1". Error records are separated back out before the JSON parse.
    $raw = @(& node $call $t.Name "ping" 2>&1)
    $errText = (@($raw | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } |
      ForEach-Object { $_.ToString() }) -join "; ").Trim()
    $outText = (@($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n").Trim()
    if ($LASTEXITCODE -ne 0) {
      $status.Error = "bridge ping exited $LASTEXITCODE$(if ($errText) { ": $errText" } elseif ($outText) { ": $outText" })"
      return $status
    }
    $json = $outText | ConvertFrom-Json
    $reload = $json.reload
    if ($null -eq $reload -or $null -eq $reload.generation) {
      $status.Error = "the bridge answered but reported no Core generation (Bootstrap older than the collectible-Core step)"
      return $status
    }
    $status.Generation = [int]$reload.generation
    $status.InFlight = [bool]$reload.inFlight
    $status.PreviousAlive = [bool]$reload.previousAlive
    if ($null -ne $reload.watch) {
      $status.WatchEnabled = [bool]$reload.watch.enabled
      $status.PendingNext = [bool]$reload.watch.pendingNext
      # THE PARK IS READ, NOT JUST PUBLISHED (R68 finding 5). Bootstrap has been putting
      # watch.parked / watch.parkedReason on the wire since FREL8 -- and CoreReloadStatus
      # .SetWatchParked names THIS script as the reader -- while nothing here looked at them.
      # A park taken on a refusal that is not `previousAlive` (a plugin ALC still alive, the
      # request catch-all) leaves previousAlive false and can leave lastReload.outcome a stale
      # `ok` from an earlier swap, so the two fields already parsed cannot see it and the hold
      # would wait out the whole window on a watcher that has decided never to look again.
      # Absent on an older Bootstrap: ConvertFrom-Json gives $null, [bool]$null is false, so the
      # field simply never fires and every other path is unchanged.
      $status.WatchParked = [bool]$reload.watch.parked
      $status.WatchParkedReason = [string]$reload.watch.parkedReason
      # A HOSTED GAME IS READ TOO (deploy-hold defect, 2026-10-06). A client hosting a private
      # game, and the hosted child itself, refuse every staged Core (CoreWatchDecision
      # .RefuseHosting / .RefuseHosted) WITHOUT parking and without a lastReload record, so the
      # fields above all look like a swap that simply has not happened yet. These two flags are
      # the only wire signal (CoreLoader.DescribeStatus -> CoreReloadStatus.ToDump, read live from
      # ProcessHost at ping time). Absent on an older Bootstrap: false, and the deadline catches it.
      $status.HostingPrivateGame = [bool]$reload.watch.hostingPrivateGame
      $status.HostedChild = [bool]$reload.watch.hostedChild
      # The watcher's own clock, if this Bootstrap puts one on the wire (R69 finding 7): the moment
      # the park was taken, else the moment of the last watcher scan. Either answers the only
      # question the wait's park branch needs -- did the watcher look at the file AFTER we staged --
      # and both are absent on an older Bootstrap, where the branch falls back to its two-poll test.
      foreach ($field in @("parkedAtUtc", "scanUtc")) {
        $stampText = [string]$reload.watch.$field
        if (-not $stampText) { continue }
        try {
          $status.WatchStampUtc = ([datetime]::Parse(
            $stampText, [cultureinfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind)).ToUniversalTime()
          break
        } catch { $status.WatchStampUtc = $null }
      }
    }
    if ($null -ne $reload.lastReload) {
      $status.LastOutcome = [string]$reload.lastReload.outcome
      $status.LastReason = [string]$reload.lastReload.reason
      $stamp = [string]$reload.lastReload.timestamp
      if ($stamp) {
        try {
          $status.LastTimestampUtc = ([datetime]::Parse(
            $stamp, [cultureinfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind)).ToUniversalTime()
        } catch { $status.LastTimestampUtc = $null }
      }
    }
    if ($null -ne $reload.pinDiag) { $status.PinDiag = [string]$reload.pinDiag }
    if ($null -ne $reload.processLifetimePlugins) {
      $status.ProcessLifetime = @($reload.processLifetimePlugins | ForEach-Object {
        [pscustomobject]@{
          File = [string]$_.file
          CoreBound = [bool]$_.coreBound
          Pending = [string]$_.restartPending
        }
      })
    }
    $status.Ok = $true
    return $status
  } catch {
    $status.Error = $_.Exception.Message
    return $status
  } finally {
    if (-not $preset) { [Environment]::SetEnvironmentVariable($tokenVar, $null) }
    if ($setTimeout) { [Environment]::SetEnvironmentVariable("PROGENITOR_CALL_TIMEOUT_MS", $null) }
    [Environment]::SetEnvironmentVariable("PROGENITOR_CALL_NOTES", $callNotesPreset)
  }
}

$script:MissingMethodNote = (
  "If one of them was built against a Core API this process does not have yet, expect a " +
  "MissingMethodException or TypeLoadException from it until the Core swap lands and the cascade reloads it; " +
  "a Dev bridge in that state can stop answering until the process restarts " +
  "(docs/dev-loop.md, 'Known artifact: a plugin lands before the Core it was built against', evidence 202).")

# The two numbers the wait is bounded by, in SECONDS (R68 finding 2, corrected by R69 finding 2).
#
# The old threshold was a count -- six consecutive unanswered polls -- and a count is not a
# duration: with a bridge whose port is closed it meant about 30 s, and with a WEDGED bridge (the
# case the abort exists for: connection accepted, no line ever written) each poll cost call.mjs's
# 30 s default, so six of them took ~180 s, twice the whole 90 s window. The abort could not fire
# before the deadline it was supposed to protect.
#
#   PollTimeout  what ONE poll may cost. It is handed to call.mjs as PROGENITOR_CALL_TIMEOUT_MS,
#                so it also bounds how long one side's turn can hold the other side's.
#   Silence      how long a side may go with NO answer before its wait is abandoned, measured from
#                its last answered poll (or the start of the wait), so it means the same thing
#                whether the polls failed fast or hung.
#
# Both are clamped: a poll may not outlast the whole window, and the silence threshold may not be
# shorter than a single poll (which would abandon a side before its first attempt could finish)
# nor longer than the window (which would make it unreachable again, the defect above).
$script:CoreSwapWindow = [Math]::Max(1, $CoreSwapTimeoutSeconds)
$script:CoreSwapPollTimeout = [Math]::Min($CoreSwapPollTimeoutSeconds, $script:CoreSwapWindow)
$script:CoreSwapSilence = [Math]::Min(
  [Math]::Max($CoreSwapSilenceSeconds, $script:CoreSwapPollTimeout), $script:CoreSwapWindow)
# The pre-staging read happens once per side and a missed answer there costs the whole hold, so it
# gets a longer bound than a poll -- still bounded, and still far under call.mjs's 30 s default.
$script:CoreSwapPreCheckTimeout = [Math]::Min(
  [Math]::Max($script:CoreSwapPollTimeout * 3, 10), $script:CoreSwapWindow)
# How long a park with no watcher stamp behind it must stand before the wait acts on it (R69
# finding 7). One watcher scan is 1 s (CoreWatch.ScanIntervalSeconds), so two seconds spanning two
# separate polls is the cheapest thing that is not the first-poll phase race the finding describes.
# It is not proof -- a frame loop that never ticks never refreshes the value -- which is why the
# definitive test is the stamp, and why an unproven park ends only at the deadline.
$script:CoreSwapParkConfirm = 2

function Invoke-HeldPluginCopy([hashtable]$t) {
  # The held plugins for one side, copied exactly once however many paths reach here (a side
  # that finishes inside the wait, an aborted side, and the caller's `finally`).
  if ($t.HeldCopied) { return }
  $t.HeldCopied = $true
  foreach ($held in $t.Deferred) { Copy-PluginToReload $t $held.Dll $held.Proj }
}

# Sides whose plugins this run withheld, for the closing summary and the exit code.
$script:Withheld = New-Object System.Collections.Generic.List[object]

$script:WithheldNextStep = (
  "The new Core stays staged as progenitor-core\Progenitor.dll.next. Once it is the running Core -- " +
  "after leaving the hosted game (the watcher then takes it) or after restarting that side -- run the " +
  "same deploy again: Core will then match the live copy and the plugins are copied straight away. " +
  "-ReleaseHeldPlugins copies them now instead, against the Core that is running.")

function Resolve-HeldPlugins([hashtable]$t, [bool]$swapped, [string]$reason) {
  <#
    The one place a side's dependent plugins are released or withheld, exactly once per side
    (the wait, the pre-check and the caller's `finally` can all reach it). ONLY A SWAP THAT
    LANDED RELEASES THEM (deploy-hold defect, 2026-10-06): a Dev built against the new Core and
    copied in while a hosted private game kept the old one threw TypeLoadException on load and
    left its bridge gate busy for the life of the process. Every other ending withholds the
    plugins -- they are never written to progenitor-reload, so nothing can load them before the
    Core they were built against -- unless -ReleaseHeldPlugins asks for the old copy-anyway order.
  #>
  if ($t.HeldResolved) { return }
  $t.HeldResolved = $true
  if ($swapped) { Invoke-HeldPluginCopy $t; return }
  $names = @($t.Deferred | ForEach-Object { $_.Dll.Name }) -join ", "
  if (-not $names) { return }
  if ($ReleaseHeldPlugins) {
    Write-Warning ("[$($t.Name)] -ReleaseHeldPlugins: copying $names anyway, against the Core this process is " +
      "running and not the one just built. " + $script:MissingMethodNote)
    Invoke-HeldPluginCopy $t
    return
  }
  $script:Withheld.Add([pscustomobject]@{ Side = $t.Name; Files = $names; Reason = $reason }) | Out-Null
  Write-Warning ("[$($t.Name)] WITHHELD: $names NOT copied into progenitor-reload, because the Core they were " +
    "built against is not the Core this process is running. " + $script:WithheldNextStep)
}

function Complete-CoreSwapWait($s, [bool]$swapped, [string]$warning, [string]$reason = "") {
  # End one side's wait and resolve its plugins immediately -- a side that is done must not
  # sit on its files while another side is still being polled.
  $s.Done = $true
  $s.Swapped = $swapped
  if ($warning) { Write-Warning $warning }
  Resolve-HeldPlugins $s.T $swapped $(if ($reason) { $reason } else { "the staged Core swap did not land" })
}

function Step-CoreSwapWait($s, [bool]$expired) {
  <#
    One round-robin turn for one side: the free on-disk check, at most one bridge poll, and the
    tests that can end this side's wait. Called once per second per side still waiting.
  #>
  $t = $s.T
  $nextGone = -not (Test-Path $s.NextFile)

  if ($nextGone) {
    $fresh = @(Get-ChildItem -Path $t.Core -Filter "$($t.CoreFile).failed-*" -ErrorAction SilentlyContinue |
      Where-Object { $s.FailedBefore -notcontains $_.Name })
    if ($fresh) {
      Complete-CoreSwapWait $s $false ("[$($s.Name)] the watcher QUARANTINED the staged Core as $($fresh[-1].Name) " +
        "and restored last-good; the generation did not move, and it will not until a Core image that loads is " +
        "staged.") "the watcher quarantined the staged Core (it did not load)"
      return
    }
  }

  # Poll on the cheap edge (the staged file consumed), on the heartbeat, on the very first turn
  # so every side reports something, and on the turn the shared deadline expires so the answer
  # in the timeout message is this second's and not five seconds old.
  $due = ($s.Polls -eq 0) -or $nextGone -or $expired -or
    ((((Get-Date) - $s.LastPoll).TotalSeconds) -ge $CoreSwapPollSeconds)
  if ($due) {
    $s.LastPoll = Get-Date
    $s.Polls++
    # BOUNDED (R69 finding 2). Without this the call inherits call.mjs's 30 s default, which is
    # both longer than the silence threshold below and longer than the turn this side is allowed
    # to hold while the other side waits its go.
    $now = Get-CoreStatus $t $script:CoreSwapPollTimeout
    $elapsed = [int]((Get-Date) - $s.Started).TotalSeconds
    if (-not $now.Ok) {
      $s.Misses++
      $s.ConsecutiveMisses++
      $s.LastError = $now.Error
      $silence = [int]((Get-Date) - $s.LastAnswer).TotalSeconds
      Write-Host ("[$($s.Name)]   ${elapsed}s: the bridge did not answer ($($now.Error)); " +
        "${silence}s of silence of the $($script:CoreSwapSilence)s allowed")
      if ($silence -ge $script:CoreSwapSilence) {
        # ONE WEDGED BRIDGE MUST NOT SPEND ANOTHER SIDE'S WINDOW (R68 finding 2), AND THE
        # THRESHOLD IS A DURATION (R69 finding 2). The Dev bridge is itself a hot-reloadable
        # plugin and its pending-op state can wedge after the cascade reloads it -- a wedge
        # ACCEPTS the connection and then writes nothing, so as a count of polls this test was
        # reached only after 6 x 30 s, long past the deadline it was meant to protect.
        Complete-CoreSwapWait $s $false ("[$($s.Name)] the bridge has been silent for ${silence}s " +
          "($($s.ConsecutiveMisses) consecutive unanswered poll(s), each bounded at " +
          "$($script:CoreSwapPollTimeout)s; last: $($s.LastError)), so nothing here can say whether the " +
          "Core swap happened and waiting longer would only spend the shared " +
          "$CoreSwapTimeoutSeconds s window the other side(s) still need. This side's wait is abandoned.") `
          "the bridge went silent, so the swap was never observed"
        return
      }
    } else {
      $s.ConsecutiveMisses = 0
      $s.LastAnswer = Get-Date
      $s.LastStatus = $now

      # The refusal is on the wire and is PERMANENT until this process restarts
      # (CoreReloadFrameDriver.Request -> CoreReloadPolicy.RefusePreviousAlive, evidence 219).
      # Waiting out the deadline on it is ninety seconds spent learning nothing.
      if ($now.PreviousAlive -or $now.LastOutcome -eq "refused") {
        Complete-CoreSwapWait $s $false ("[$($s.Name)] the Core swap was REFUSED: a previous Core generation is " +
          "still alive in this process, so Bootstrap restored it and will refuse every further swap until the " +
          "PROCESS RESTARTS (previousAlive=$($now.PreviousAlive), lastReload=$($now.LastOutcome) " +
          "'$($now.LastReason)', generation $($now.Generation); evidence 219). Nothing is going to load the " +
          "staged progenitor-core\Progenitor.dll.next until then. Restart this side (tools/test-server.ps1 " +
          "restart, or quit and relaunch the client) and the staged Core is promoted at the next start." +
          $(if ($now.PinDiag) { "`npin diagnostics:`n$($now.PinDiag)" } else { "" })) `
          "the swap was refused (previous Core generation alive); only a restart promotes it"
        return
      }

      # A HOSTED GAME ENDS THE WAIT AT ONCE (deploy-hold defect, 2026-10-06). While this client
      # hosts a private game -- or this process is the hosted child -- the watcher refuses every
      # staged Core without parking it and without a lastReload record (CoreWatchDecision
      # .RefuseHosting / .RefuseHosted; it logs "Core watch refused: this client is hosting a
      # private game; the child keeps the copied Core." once), so nothing above can see it and
      # the hold would run to the deadline. The flag is read live at ping time and stays set until
      # the child exits or the client joins a dedicated server, at which point the watcher takes
      # the staged image by itself.
      if ($now.HostingPrivateGame -or $now.HostedChild) {
        Complete-CoreSwapWait $s $false ("[$($s.Name)] the Core swap is REFUSED while " +
          $(if ($now.HostedChild) { "this process is a hosted private-game server (the child keeps the copied Core)" }
            else { "this client is hosting a private game" }) +
          " (reload.watch.hostingPrivateGame=$($now.HostingPrivateGame), hostedChild=$($now.HostedChild); " +
          "generation $($now.Generation) is running). The watcher leaves progenitor-core\Progenitor.dll.next " +
          "staged and takes it by itself once the hosted game ends (leave to the main menu or join a dedicated " +
          "server); a restart promotes it too.") `
          "a hosted private game refuses the Core swap until it ends"
        return
      }

      # THE PARK ENDS THE WAIT TOO (R68 finding 5). The watcher asks once per staged image and,
      # on a refused request, parks on that file's (length, write time) and never reads it again
      # until the bytes change or the process restarts (CoreWatch.Park, job FREL8). Two of the
      # refusals it can park on leave previousAlive false and lastReload a stale `ok`, so the
      # test above cannot see them and the hold would run to the deadline on a watcher that has
      # stopped looking. `pendingNext` is required with it: the park is recomputed from the
      # CURRENT stamp on every watcher tick (CoreWatch.cs, SetWatchParked beside ParkedOnStamp),
      # and this run rewrote that file before the first poll a second or more ago, so a park
      # still standing here is a park on the image THIS run staged.
      #
      # EXCEPT THAT "A SECOND OR MORE AGO" IS NOT EVIDENCE THE WATCHER LOOKED (R69 finding 7).
      # The recomputation happens on TickCore, which returns immediately while now < _nextScan
      # (ScanIntervalSeconds 1.0) and does not run at all while the game's frame loop is stalled --
      # and on the client that is any moment the window is not rendering. So the published value
      # can still describe the PREVIOUS image when the first poll lands, and acting on it releases
      # the held plugins on the first poll with a message asserting a fact about the wrong file:
      # exactly the premature release the hold exists to prevent. The park is therefore acted on
      # only with evidence that the watcher looked AFTER this run staged:
      #   * a watcher stamp on the wire at or after $t.StagedAtUtc (watch.parkedAtUtc /
      #     watch.scanUtc) -- the same test Test-SwapIsThisRun applies to the swap, for the same
      #     reason, and the definitive one; or
      #   * failing that (an older Bootstrap publishes no stamp), the park standing on two separate
      #     polls at least one watcher scan apart, which at the 5 s heartbeat is one heartbeat.
      # Neither yet: say what is on the wire and KEEP WAITING. A park that is real re-reports on
      # the next poll and ends the wait then; a park that was stale is gone by then.
      if ($now.WatchParked -and $now.PendingNext) {
        $stampProves = ($null -ne $now.WatchStampUtc) -and ($null -ne $t.StagedAtUtc) -and
          ($now.WatchStampUtc -ge $t.StagedAtUtc)
        $parkProven = $stampProves
        if (-not $stampProves) {
          if ($null -eq $s.ParkFirstSeen) { $s.ParkFirstSeen = Get-Date }
          $s.ParkPolls++
          $parkFor = [int]((Get-Date) - $s.ParkFirstSeen).TotalSeconds
          $parkProven = ($s.ParkPolls -ge 2) -and ($parkFor -ge $script:CoreSwapParkConfirm)
          if (-not $parkProven) {
            Write-Host ("[$($s.Name)]   ${elapsed}s: watch.parked is set with a .next pending, but nothing says " +
              "the watcher looked after this run staged (no watcher stamp on the wire; seen on " +
              "$($s.ParkPolls) poll(s) over ${parkFor}s). Still waiting -- a park on the PREVIOUS image would " +
              "clear on the watcher's next scan.")
            # Not proven: keep waiting -- unless the shared window is already up, in which case
            # fall through to the deadline handling below rather than returning every turn
            # forever, which is what an unconditional return here would do.
            if (-not $expired) { return }
          }
        }
        if ($parkProven) {
          Complete-CoreSwapWait $s $false ("[$($s.Name)] the watcher has PARKED on the Core this run staged" +
            $(if ($stampProves) { " (watcher stamp $($now.WatchStampUtc.ToString('o')), after this run staged)" }
              else { " (no watcher stamp on the wire; the park stood on $($s.ParkPolls) polls)" }) + ": it " +
            "asked for the swap, the request was refused" +
            $(if ($now.WatchParkedReason) { " ('$($now.WatchParkedReason)')" } else { "" }) +
            ", and it will not look at progenitor-core\Progenitor.dll.next again until that file's bytes change " +
            "or the process restarts (watch.parked, job FREL8). Only a RESTART promotes the staged image now -- " +
            "generation $($now.Generation) is what is running.") `
            "the watcher parked on the staged Core after a refused request; only a restart promotes it"
          return
        }
      }

      if (($now.Generation -ne $t.GenerationBefore) -and -not $now.InFlight -and $now.LastOutcome -eq "ok" `
          -and (Test-SwapIsThisRun $now $t.StagedAtUtc)) {
        Write-Host ("[$($s.Name)] Core swap complete (generation $($t.GenerationBefore) -> $($now.Generation)); " +
          "copying plugins now")
        Complete-CoreSwapWait $s $true ""
        return
      }
      Write-Host ("[$($s.Name)]   ${elapsed}s: generation $($now.Generation), inFlight=$($now.InFlight), " +
        "staged .next $(if ($nextGone) { 'consumed' } else { 'still on disk' }), " +
        "lastReload=$($now.LastOutcome)")
    }
  }

  if (-not $expired) { return }

  $waited = [int]((Get-Date) - $s.Started).TotalSeconds
  if ($s.Polls -gt 0 -and $s.Misses -eq $s.Polls) {
    Complete-CoreSwapWait $s $false ("[$($s.Name)] the bridge never answered in ${waited}s ($($s.Polls) poll(s), " +
      "all failed; last: $($s.LastError)), so nothing here can say whether the Core swap happened.") `
      "the bridge never answered, so the swap was never observed"
    return
  }

  $tail = if ($s.LastStatus) {
    "the last answer was generation $($s.LastStatus.Generation) (was $($t.GenerationBefore)), " +
    "inFlight=$($s.LastStatus.InFlight), lastReload=$($s.LastStatus.LastOutcome) '$($s.LastStatus.LastReason)'"
  } elseif ($s.LastError) { "the bridge's last error was: $($s.LastError)" } else { "" }
  Complete-CoreSwapWait $s $false ("[$($s.Name)] the staged Core swap was not observed within ${waited}s " +
    "(the shared $CoreSwapTimeoutSeconds s window)" +
    $(if ($s.ConsecutiveMisses -gt 0) { " (the last $($s.ConsecutiveMisses) poll(s) went unanswered)" } else { "" }) +
    $(if ($tail) { "; $tail" } else { "" }) + ".") `
    "the staged Core swap was not observed within the window"
}

function Wait-CoreSwaps([object[]]$sides, [datetime]$deadline) {
  <#
    Hold the plugin copies until the Core staged a moment ago is the Core that is running
    (roadmap M2.13, jobs FDEP1, FDEP2, FDEP3 and FDEP4). One side's wait ends in success only
    when THIS run's swap landed: the generation moved, no swap is in flight, the last reload
    says `ok`, and it is stamped no earlier than the moment this run staged its .next. That last
    pair is what stops a swap which was ALREADY RUNNING when the deploy started from being
    counted as ours and releasing plugins built against a Core still sitting unread in .next
    (R66 finding 3; the pre-check that refuses to hold at all in that case is the first half of
    the same fix).

    Every other ending -- a refusal, a hosted game, a park, a quarantine, an abandoned side, the
    deadline -- WITHHOLDS that side's plugins with one warning saying why (Resolve-HeldPlugins),
    and so does the caller's `finally` for a wait that was interrupted. This used to copy them
    anyway on the theory that a half-done deploy was worse than one MissingMethodException
    (R66 finding 11); the 2026-10-06 defect (a Dev that could not load against the old Core and
    wedged its bridge) showed the copy is the worse half, and the withheld state is reported with
    the exact next step and a non-zero exit.

    A miss while polling is EXPECTED and never fatal: the swap cascade unloads and reloads the
    Dev plugin, so the bridge stops answering for part of it. Consecutive misses are counted so
    "the bridge never answered" is reported apart from "the generation did not move" (R66
    finding 4) -- and, since FDEP4, so that a side whose bridge has stopped answering altogether
    is abandoned instead of being polled to the deadline.

    THE CHEAP SIGNAL FIRST. Every path the watcher can take consumes the staged file --
    promote, quarantine, and the identical-to-live skip all remove Progenitor.dll.next
    (CoreWatch.ShouldRequestReload) -- so its disappearance is a free, once-a-second event
    that says "the watcher has now acted". The bridge is asked on that edge and otherwise
    only on the -CoreSwapPollSeconds heartbeat, because `ping` runs on the game's main thread.

    ONE DEADLINE, AND THE POLLS ARE INTERLEAVED (R67 finding 9, corrected by R68 finding 2).
    Both sides' Cores are staged before any wait begins, so both swaps are already running
    concurrently, and one shared deadline (taken by the caller) is what keeps `-Side both` from
    costing 2 x -CoreSwapTimeoutSeconds. But sharing a deadline between waits that ran BACK TO
    BACK just moved the harm: the first side could spend the entire window -- an unanswering
    bridge did exactly that, forever, because an unanswered poll only incremented a counter --
    and the second side then started with a budget of zero, polled once, and had its plugins
    copied ahead of a swap nobody had observed, on the side with the connected players. So the
    loop is round-robin: one turn per second, every side still waiting gets that turn, each
    side's plugins are released the moment ITS wait ends, and a side is abandoned after
    $script:CoreSwapSilence seconds of silence with its own warning.

    AND THE TURN IS BOUNDED (R69 finding 2). "No side can consume another's budget" was true of a
    bridge that REFUSES the connection and false of one that HANGS: the poll is synchronous inside
    this loop and call.mjs waited 30 s by default, so a hanging client bridge took the server's
    turn thirty times over, the abort (then a count of six misses) needed ~180 s to fire, and the
    deadline was read once per iteration rather than per side. Now every poll carries
    PROGENITOR_CALL_TIMEOUT_MS = $script:CoreSwapPollTimeout seconds, the abandon threshold is a
    duration, the deadline is read inside the foreach, and the turn order rotates each iteration so
    the client is not permanently first.

    The deadline is tested at the END of a turn, so every side gets at least one poll and
    reports what it found even when the deadline had already passed when the wait started.
  #>
  $states = @()
  foreach ($t in $sides) {
    $started = Get-Date
    $budget = [Math]::Max(0, [int]($deadline - $started).TotalSeconds)
    Write-Host ("[$($t.Name)] holding $($t.Deferred.Count) plugin file(s) until the staged Core swap completes " +
      "(generation $($t.GenerationBefore) now; $budget s left of the shared $CoreSwapTimeoutSeconds s window, " +
      "asking the bridge every $CoreSwapPollSeconds s, each ask bounded at $($script:CoreSwapPollTimeout)s, " +
      "abandoning this side after $($script:CoreSwapSilence)s of silence)")
    $states += [pscustomobject]@{
      T = $t
      Name = $t.Name
      NextFile = (Join-Path $t.Core "$($t.CoreFile).next")
      FailedBefore = @(Get-ChildItem -Path $t.Core -Filter "$($t.CoreFile).failed-*" -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Name })
      Started = $started
      LastPoll = [datetime]::MinValue
      # The last moment this side's bridge ANSWERED. The silence threshold is measured from it, so
      # it starts at the beginning of the wait rather than at MinValue (R69 finding 2).
      LastAnswer = $started
      LastError = ""
      LastStatus = $null
      Polls = 0
      Misses = 0
      ConsecutiveMisses = 0
      # The park's own bookkeeping (R69 finding 7): when a stamp-less park was first seen and on
      # how many polls it has stood since.
      ParkFirstSeen = $null
      ParkPolls = 0
      Done = $false
      Swapped = $false
    }
  }

  $turn = 0
  while ($true) {
    $active = @($states | Where-Object { -not $_.Done })
    if ($active.Count -eq 0) { break }
    Start-Sleep -Seconds 1
    # THE DEADLINE IS READ PER SIDE, AND THE ORDER ROTATES (R69 finding 2). $expired used to be
    # computed once before the foreach, so with a slow poll the later side was judged against a
    # deadline that had passed while the earlier side was being polled; and $targets is
    # client-first, so the client was always polled first. Each poll is bounded by
    # $script:CoreSwapPollTimeout, so a turn now costs seconds at worst, but neither of those is a
    # reason to keep reading a stale clock or to keep one side permanently at the head of the queue.
    if ($active.Count -gt 1) {
      # `0..-1` is @(0,-1) in PowerShell, which would re-list the first and last element, so the
      # rotation is only applied when there is something to rotate.
      $offset = $turn % $active.Count
      if ($offset -gt 0) {
        $active = @($active[$offset..($active.Count - 1)]) + @($active[0..($offset - 1)])
      }
    }
    $turn++
    foreach ($s in $active) {
      if ($s.Done) { continue }
      Step-CoreSwapWait $s ((Get-Date) -ge $deadline)
    }
  }
  return $states
}

function Test-SwapIsThisRun($status, $stagedAtUtc) {
  <#
    True when the reload the bridge is reporting is no older than this run's staging. Without
    it, a swap that was already in flight when deploy started completes a second later, the
    generation inequality fires, and the script affirmatively reports an ordering it did not
    get (R66 finding 3). A missing timestamp (older Bootstrap) is accepted rather than
    deadlocking the hold: the pre-check already refused to hold on an in-flight swap.

    NO SLACK (R67 finding 8). This used to allow the stamp to be one second OLDER than the
    staging moment, justified by "two clocks on one machine" -- and there is one clock:
    Bootstrap's stamp is DateTime.UtcNow taken in the game process
    (src/Progenitor.Bootstrap/CoreReloadStatus.cs:48) and $stagedAtUtc is
    (Get-Date).ToUniversalTime() taken here, both the same system clock through the same OS
    call, and neither value comes off a file. The slack absorbed no skew and re-opened, one
    second wide, the very window R66 finding 3 closed: a bridge core_reload starting between
    the pre-check and the staging could finish inside it and be accepted as ours.

    The inequality is safe without it because it is one-directional by construction: an `ok`
    outcome is stamped at the END of the cascade (CoreLoader.TickReload -> CoreReloadStatus
    .Record(result, reason), CoreLoader.cs:323), and OUR cascade cannot start before the
    watcher has seen the .next this run wrote, which is after $stagedAtUtc. So our own swap is
    always stamped later, and only a swap that finished before we staged is excluded.
  #>
  if ($null -eq $stagedAtUtc) { return $true }
  if ($null -eq $status.LastTimestampUtc) { return $true }
  return $status.LastTimestampUtc -ge $stagedAtUtc
}

function Get-ProcessLifetimeFile([hashtable]$t, [string]$fileName) {
  <#
    Job FINJ1: the reload.processLifetimePlugins entry for $fileName in this side's running process,
    or $null. Read once per side and cached on $t (the Core pre-check's read is reused when there was
    one); a side that is not running, a -Plan run, or a bridge that does not answer reads as none.
  #>
  if (-not $t.Running -or $plan) { return $null }
  if ($null -eq $t.LifetimeStatus) {
    $t.LifetimeStatus = Get-CoreStatus $t $script:CoreSwapPollTimeout
  }
  if (-not $t.LifetimeStatus.Ok) { return $null }
  return @($t.LifetimeStatus.ProcessLifetime | Where-Object { $_.File -ieq $fileName } | Select-Object -First 1)[0]
}

function Copy-PluginToReload([hashtable]$t, $dll, [string]$proj) {
  # Everything that is neither Bootstrap nor Core: the reload folder, plus the plugin's
  # localization and assets folders. Unchanged by FDEP1 except for being callable later.
  Invoke-DeployNewDir $t.Reload
  $stale = Join-Path $t.Dir $dll.Name
  if ((Test-Path $stale) -and -not $t.Running) { Invoke-DeployRemove $stale }
  $reloadDest = Join-Path $t.Reload $dll.Name
  Invoke-DeployCopy $dll.FullName $reloadDest $t.Name $proj "reload folder"
  $lifetime = Get-ProcessLifetimeFile $t $dll.Name
  if (-not $plan) {
    if ($lifetime) {
      # Job FINJ1: the reloader keeps the running build of a plugin that registered its own IL2CPP
      # types ('Reload: <file> changed, but it registers its own IL2CPP types ...'), so this copy
      # is NOT a live reload, whatever the process state.
      Write-Warning ("[$($t.Name)] $($dll.Name) -> reload folder, but the running process keeps its current build: " +
        "it registers its own IL2CPP types (reload.processLifetimePlugins). The new build loads at the next start; restart this side.")
    } else {
      Write-Host "[$($t.Name)] $($dll.Name) -> reload folder$(if ($t.Running) { ' (live reload)' })"
    }
  }

  # Reloadable plugins load from a byte stream, so JSON must sit next to the DLL under
  # <reload>/<AssemblyName>/localization (the first folder Localization.LoadForPlugin probes).
  $locSrc = Join-Path $dll.DirectoryName "localization"
  if (Test-Path $locSrc) {
    $asmName = $dll.BaseName
    $locDest = Join-Path $t.Reload (Join-Path $asmName "localization")
    if (Test-Path $locDest) { Invoke-DeployRemove $locDest -Recurse }
    $locParent = Join-Path $t.Reload $asmName
    Invoke-DeployNewDir $locParent
    Invoke-DeployCopyTree $locSrc $locDest $t.Name $proj "localization"
    if (-not $plan) { Write-Host "[$($t.Name)] $asmName/localization -> reload folder" }
  }

  # Same layout as localization: <reload>/<AssemblyName>/assets (the first folder
  # PluginAssetRoot / LocalizationParser.ResolvePluginFolder(..., "assets") probes).
  $assetSrc = Join-Path $dll.DirectoryName "assets"
  if (Test-Path $assetSrc) {
    $asmName = $dll.BaseName
    $assetDest = Join-Path $t.Reload (Join-Path $asmName "assets")
    if (Test-Path $assetDest) { Invoke-DeployRemove $assetDest -Recurse }
    $assetParent = Join-Path $t.Reload $asmName
    Invoke-DeployNewDir $assetParent
    Invoke-DeployCopyTree $assetSrc $assetDest $t.Name $proj "assets"
    if (-not $plan) { Write-Host "[$($t.Name)] $asmName/assets -> reload folder" }
  }

  # Job FJSON1. Same layout again: <reload>/<AssemblyName>/content (the first folder
  # ContentFiles / LocalizationParser.ResolvePluginFolder(..., "content") probes). JSON content
  # definition files, read after the plugin's OnLoad.
  $contentSrc = Join-Path $dll.DirectoryName "content"
  if (Test-Path $contentSrc) {
    $asmName = $dll.BaseName
    $contentDest = Join-Path $t.Reload (Join-Path $asmName "content")
    if (Test-Path $contentDest) { Invoke-DeployRemove $contentDest -Recurse }
    $contentParent = Join-Path $t.Reload $asmName
    Invoke-DeployNewDir $contentParent
    Invoke-DeployCopyTree $contentSrc $contentDest $t.Name $proj "content"
    if (-not $plan) { Write-Host "[$($t.Name)] $asmName/content -> reload folder" }
  }
}

$targets = @()
if ($Side -ne "server") {
  $targets += @{
    Name = "client"
    Dir = (Join-Path $GameDir "BepInEx\plugins")
    Home = (Join-Path $GameDir "BepInEx\plugins\Progenitor")
    Patchers = (Join-Path $GameDir "BepInEx\patchers")
    Reload = (Join-Path $GameDir "BepInEx\progenitor-reload")
    Core = (Join-Path $GameDir "BepInEx\plugins\Progenitor\progenitor-core")
    CoreFile = "Progenitor.core"
    Config = (Join-Path $GameDir "BepInEx\config")
    Proc = "VRising"
    Under = $GameDir
  }
}
if ($Side -ne "client") {
  $targets += @{
    Name = "server"
    Dir = (Join-Path $TestRoot "server\BepInEx\plugins")
    Home = (Join-Path $TestRoot "server\BepInEx\plugins\Progenitor")
    Patchers = (Join-Path $TestRoot "server\BepInEx\patchers")
    Reload = (Join-Path $TestRoot "server\BepInEx\progenitor-reload")
    Core = (Join-Path $TestRoot "server\BepInEx\plugins\Progenitor\progenitor-core")
    CoreFile = "Progenitor.core"
    Config = (Join-Path $TestRoot "server\BepInEx\config")
    Proc = "VRisingServer"
    Under = $TestRoot
  }
}

# The C:\VRisingServer fence, asserted once for every root this run will touch -- config,
# plugins, patchers, core and reload alike -- and before anything is written or even planned. It holds
# under -WhatIf too: a plan that listed the live server's paths would still be a mistake to read.
# Every root goes through tools/common/Fence.ps1, which canonicalizes before comparing, so a
# forward-slash, trailing-slash, \\?\, admin-share, 8.3 or junction spelling of the live tree
# is refused too (R40 finding 4; the parameters themselves were fenced at the top of the file).
foreach ($t in $targets) {
  foreach ($root in @($t.Under, $t.Dir, $t.Home, $t.Patchers, $t.Reload, $t.Core, $t.Config)) {
    Assert-NotLiveServerPath $root
  }
}

# Bridge queue (docs/bridge.md#bridge-queue): never deploy into a side another agent session holds.
# A -WhatIf plan writes nothing and is not gated. queue.mjs prints only to stdout and exits 5 on a
# refusal; a missing node or a broken queue file never blocks a deploy.
if (-not $plan -and (Get-Command node -ErrorAction SilentlyContinue)) {
  $queueJs = Join-Path $PSScriptRoot "mcp\queue.mjs"
  $queueSides = (@($targets | ForEach-Object { $_.Name }) -join ",")
  $queueOut = (@(& node $queueJs gate --sides $queueSides --op deploy) -join " ").Trim()
  if ($LASTEXITCODE -eq 5) { throw "deploy refused by the bridge queue: $queueOut" }
}

# Developer-tree config seeding. Reload.WatchCore ships false (job FRLD); our trees keep the
# in-process Core hot swap by having the entry written here on every deploy.
foreach ($t in $targets) {
  $cfg = Join-Path $t.Config "progenitor.cfg"
  # Before the first start of a Bootstrap with the new id prefix, only the old-prefix cfg exists.
  # Creating progenitor.cfg here would block Bootstrap's rename (it never overwrites an existing
  # new-name file) and strand every old setting, so seed the old-prefix file instead.
  if (-not (Test-Path -LiteralPath $cfg)) {
    $legacy = Get-ChildItem -LiteralPath $t.Config -Filter "*.progenitor.cfg" -File -ErrorAction SilentlyContinue |
      Select-Object -First 1
    if ($legacy) { $cfg = $legacy.FullName }
  }
  try {
    $state = Set-BepInExCfgEntry -Path $cfg -Section "Reload" -Key "WatchCore" -Value "true"
    Write-Host "$(if ($plan) { '[plan]' })[$($t.Name)] Reload.WatchCore = true ($state) -> $cfg"
  } catch {
    Write-Warning "[$($t.Name)] could not seed Reload.WatchCore: $($_.Exception.Message)"
  }
}

# Address tables and the guide, once per target, independent of -Projects (see Copy-AddressTables).
foreach ($t in $targets) {
  if (-not (Test-Path $t.Dir)) { Write-Warning "$($t.Name) plugin dir missing: $($t.Dir)"; continue }
  Copy-AddressTables $t
  Copy-ProgenitorGuide $t
}

# Resolve every project's built DLL up front, so this run knows BEFORE it copies anything
# whether it is going to stage a new Core (job FDEP1). -Projects order must not decide the
# ordering: `-Projects Progenitor.Sample,Progenitor.Core` stages the same Core as the default
# order and must hold the Sample just the same.
$resolvedProjects = @()
foreach ($proj in $Projects) {
  $projDir = @((Join-Path $repo "src\$proj"), (Join-Path $repo "samples\$proj")) | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $projDir) { throw "project $proj not found under src/ or samples/" }
  # The project's own assembly first (samples/Exercise.TimedBuff builds Exercise.TimedBuff.dll, which
  # the Progenitor* glob never matched); Progenitor.Core builds Progenitor.dll, so the glob stays as the fallback.
  $binDir = Join-Path $projDir "bin\Debug\net6.0"
  $dll = Get-Item (Join-Path $binDir "$proj.dll") -ErrorAction SilentlyContinue
  if (-not $dll) { $dll = Get-ChildItem $binDir -Filter "*.dll" -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "Progenitor*.dll" } | Select-Object -First 1 }
  if (-not $dll) { throw "no built Progenitor*.dll (or $proj.dll) for $proj (build first, or drop -WhatIf's assumption of an existing bin/Debug output)" }
  $resolvedProjects += @{ Proj = $proj; Dll = $dll }
}
$stagesCore = @($resolvedProjects | Where-Object { $_.Dll.Name -eq "Progenitor.dll" }).Count -gt 0
$coreDll = @($resolvedProjects | Where-Object { $_.Dll.Name -eq "Progenitor.dll" } | Select-Object -First 1).Dll

foreach ($t in $targets) {
  $t.Running = (Test-Path $t.Dir) -and (Test-Running $t.Proc $t.Under)
  $t.Deferred = New-Object System.Collections.Generic.List[object]
  $t.HoldPlugins = $false
  $t.WithholdReason = ""
  $t.HeldResolved = $false
  $t.HeldCopied = $false
  $t.LifetimeStatus = $null
  $t.GenerationBefore = $null
  $t.StagedAtUtc = $null
  $t.CoreUnchanged = $false
  $t.CoreSkipStage = $false
  if (Test-Path $t.Dir) { Remove-StaleChainloaderCore $t.Dir $t.Name $t.Running }

  # Does this run actually CHANGE Core in this tree, and is there anything to write? (R66
  # finding 2, corrected by R67 finding 2.) Asked of the bytes, and asked for a running process
  # only: a stopped process promotes whatever is on disk at its next start, and Copy-RestartOnly
  # already deletes any stale .next there before writing the live file.
  #
  # TWO SEPARATE QUESTIONS, because the old single flag answered one and printed the other:
  #
  #   CoreUnchanged  the built Core equals the LIVE progenitor-core\Progenitor.dll, so whatever
  #                  is staged the watcher inspects it and skips it without moving the
  #                  generation (CoreWatchPolicy.InspectStaged -> SkipIdentical). Never hold on
  #                  that: the hold could only time out.
  #   CoreSkipStage  there is nothing to WRITE, because the image this run would stage is
  #                  already the one on disk in that role.
  #
  # The old code set one flag from the live comparison alone and then said "nothing is staged" --
  # a sentence about the directory, decided without looking at it. A DIFFERENT Progenitor.dll.next
  # left over from an earlier deploy (this machine has carried one since evidence 219) was neither
  # replaced nor mentioned, and the next restart promoted an image the operator had just decided
  # against, announced by a line that told them the opposite. So the built Core is compared
  # against the PENDING .next when one exists, and against the live copy otherwise.
  if ($stagesCore -and $t.Running -and $coreDll) {
    $liveCore = Join-Path $t.Core $t.CoreFile
    $nextCore = "$liveCore.next"
    $t.CoreUnchanged = Test-SameFileBytes $coreDll.FullName $liveCore
    $tag = "$(if ($plan) { '[plan]' })[$($t.Name)]"
    if (Test-Path -LiteralPath $nextCore) {
      if (Test-SameFileBytes $coreDll.FullName $nextCore) {
        # Same image already staged. Writing it again would only re-arm the watcher's settle
        # timer, so nothing is written -- but say WHICH image the next start promotes, because
        # when it differs from the live one this side is not running the Core just built.
        $t.CoreSkipStage = $true
        Write-Host ("$tag Progenitor.dll is byte-identical to the Core already staged as " +
          "progenitor-core\Progenitor.dll.next (sha256=$(Get-Sha256Prefix $nextCore)); nothing is written. " +
          "That staged image is what the next start promotes" +
          $(if ($t.CoreUnchanged) { " and it matches the Core running now, so no plugin is held." }
            else {
              # The plugins this run built were built against THAT image, which is not running yet,
              # so they are withheld below (deploy-hold defect, 2026-10-06) -- this run cannot hold
              # on a swap it did not stage (R66 finding 3), and it must not release them before it.
              " -- it is NOT the Core this process is running: that swap has not happened (still pending, " +
              "refused, or held off by a hosted game), so the plugins built against it are withheld."
            }))
      } else {
        Write-Warning ("[$($t.Name)] a DIFFERENT Core was already staged as progenitor-core\Progenitor.dll.next " +
          "(sha256=$(Get-Sha256Prefix $nextCore)) and had not been promoted; this run REPLACES it with the image " +
          "just built (sha256=$(Get-Sha256Prefix $coreDll.FullName)), so that is what the next start promotes." +
          $(if ($t.CoreUnchanged) {
              " The new image is byte-identical to the live Core, so the watcher will consume the staged file and " +
              "skip it without moving the generation: nothing swaps, no plugin is held, and the stale staged image " +
              "is simply gone."
            } else { "" }))
      }
    } elseif ($t.CoreUnchanged) {
      $t.CoreSkipStage = $true
      Write-Host ("$tag Progenitor.dll is byte-identical to the live progenitor-core copy and nothing was staged " +
        "before this run; nothing is written and no plugin is held (the watcher would skip an identical image " +
        "without moving the generation)")
    }
  }

  # Decide the ordering for this target. The hold only makes sense when this run will stage a
  # Core against a live process AND the generation can actually be observed: a plan run writes
  # nothing, a stopped process promotes at its next start, a disabled watcher will never swap,
  # and a bridge that does not answer cannot tell us when the swap happened. A stopped process
  # and a plan run copy as before; every case where the new Core will NOT be running when the
  # plugins land WITHHOLDS them instead of copying (deploy-hold defect, 2026-10-06): the plugins
  # are never written, the reason is recorded in $t.WithholdReason, and Resolve-HeldPlugins
  # reports it after the copy loop (or copies anyway under -ReleaseHeldPlugins).
  if (-not ($stagesCore -and $t.Running)) {
    # R66 finding 12: this branch used to be entirely silent, and it absorbs a case that is not
    # "nothing to hold" at all -- a mistyped root makes Test-Path false, which makes Running
    # false, which produces no hold, no swap and no word about why.
    if ($stagesCore -and -not (Test-Path $t.Dir)) {
      Write-Warning ("[$($t.Name)] $($t.Dir) does not exist, so this side is treated as not running: Core is " +
        "staged nowhere, no plugin is held, and nothing will be deployed here. Check -$(if ($t.Name -eq 'client') { 'GameDir' } else { 'TestRoot' }).")
    } elseif ($stagesCore) {
      Write-Host "[$($t.Name)] $($t.Proc) is not running; Core is written straight to progenitor-core and no plugin is held"
    }
    continue
  }
  # THE THREE-LOCATION LAYOUT IS RUNNING. No single-folder Core is live in this tree yet, so the
  # running process loaded Core from BepInEx/progenitor-core (or none at all) and its watcher looks
  # there: this run writes the single-folder Core in place and no swap can follow. Only a restart
  # loads it, so the plugins built against it are withheld.
  if (-not (Test-Path -LiteralPath (Join-Path $t.Core $t.CoreFile))) {
    if (-not $plan) {
      Write-Warning ("[$($t.Name)] $($t.Proc) is running the three-location layout (no plugins\Progenitor\progenitor-core\" +
        "$($t.CoreFile) yet). The single-folder Core is written in place and loads at the next start; restart this side.")
      $t.WithholdReason = "this side still runs the three-location layout; only a restart loads the single-folder Core"
    }
    continue
  }
  # Nothing this run does will move the generation when the built Core equals the live one (the
  # watcher skips it): the plugins match the running Core, so they go in straight away.
  if ($t.CoreUnchanged) { continue }
  # The image is already staged and belongs to whoever staged it (R66 finding 3's rule: never
  # hold on someone else's swap) -- but it is the image these plugins were built against, and it
  # is not running, so they must not go in either. Explained above, when the bytes were compared.
  if ($t.CoreSkipStage) {
    if (-not $plan -and $CoreSwapTimeoutSeconds -gt 0) {
      $t.WithholdReason = "the Core these plugins were built against is staged but not running yet"
    }
    continue
  }
  if ($plan) {
    Write-Host ("[plan][$($t.Name)] plugin files would be held until the staged Core swap completes (job FDEP1), " +
      "and withheld if it does not")
    continue
  }
  if ($CoreSwapTimeoutSeconds -le 0) {
    Write-Host "[$($t.Name)] -CoreSwapTimeoutSeconds 0: plugins are copied before the Core swap (pre-FDEP1 order)"
    continue
  }
  $before = Get-CoreStatus $t $script:CoreSwapPreCheckTimeout
  if ($before.Ok) { $t.LifetimeStatus = $before }
  if (-not $before.Ok) {
    Write-Warning ("[$($t.Name)] cannot read the Core generation from the bridge ($($before.Error)), so this run " +
      "cannot see whether or when the staged Core swaps.")
    $t.WithholdReason = "the bridge did not answer, so the Core swap cannot be observed"
    continue
  }
  if (-not $before.WatchEnabled) {
    Write-Warning ("[$($t.Name)] Reload.WatchCore is off in the running process, so the staged Core will not swap " +
      "until the next start.")
    $t.WithholdReason = "Reload.WatchCore is off; only a restart promotes the staged Core"
    continue
  }
  # A HOSTED GAME, before we stage anything (deploy-hold defect, 2026-10-06). The watcher refuses
  # every staged Core while this client hosts a private game, or in the hosted child itself, and
  # does so without a park or a lastReload record -- so this is the only branch that can see it.
  if ($before.HostingPrivateGame -or $before.HostedChild) {
    Write-Warning ("[$($t.Name)] " +
      $(if ($before.HostedChild) { "this process is a hosted private-game server; it keeps the copied Core" }
        else { "this client is HOSTING A PRIVATE GAME" }) +
      " (reload.watch.hostingPrivateGame=$($before.HostingPrivateGame), hostedChild=$($before.HostedChild); " +
      "generation $($before.Generation) is running). The watcher refuses the swap until the hosted game ends " +
      "('Core watch refused: this client is hosting a private game; the child keeps the copied Core.'), then " +
      "takes the staged Core by itself; a restart promotes it too.")
    $t.WithholdReason = "a hosted private game refuses the Core swap until it ends"
    continue
  }
  # Already refused, before we stage anything (R66 finding 1). Bootstrap keeps a generation that
  # would not collect alive and refuses every swap until the process restarts, so there is
  # nothing to wait for: say so now rather than after ninety seconds of silence.
  if ($before.PreviousAlive -or $before.LastOutcome -eq "refused") {
    Write-Warning ("[$($t.Name)] this process REFUSES Core swaps until it restarts: a previous Core generation " +
      "is still alive (previousAlive=$($before.PreviousAlive), lastReload=$($before.LastOutcome) " +
      "'$($before.LastReason)', generation $($before.Generation); evidence 219). The staged Core will be " +
      "promoted at the next start, not now. Restart this side to pick up the new Core." +
      $(if ($before.PinDiag) { "`npin diagnostics:`n$($before.PinDiag)" } else { "" }))
    $t.WithholdReason = "the process refuses Core swaps (previous Core generation alive); only a restart promotes it"
    continue
  }
  # A PROCESS-LIFETIME PLUGIN THAT REFERENCES CORE (job FINJ1). A reload-folder plugin that registered
  # its own IL2CPP types is never unloaded in place; when it also references Progenitor, unloading
  # Core under it would pin the outgoing generation, so Bootstrap refuses every Core swap until the
  # process restarts ('Core reload refused: process-lifetime plugin ...'). Nothing to wait for.
  $bound = @($before.ProcessLifetime | Where-Object { $_.CoreBound })
  if ($bound.Count -gt 0) {
    $names = ($bound | ForEach-Object { $_.File }) -join ", "
    Write-Warning ("[$($t.Name)] this process REFUSES Core swaps until it restarts: $names registers its own IL2CPP " +
      "types and references Progenitor (reload.processLifetimePlugins, coreBound=true), so it can never be unloaded " +
      "and the running Core cannot be replaced. The staged Core will be promoted at the next start. Restart this side.")
    $t.WithholdReason = "a process-lifetime plugin ($names) binds the running Core; only a restart promotes the staged Core"
    continue
  }
  # A swap already IN FLIGHT belongs to someone else -- a second deploy, or a bridge core_reload.
  # Its completion would satisfy a bare generation inequality and release plugins built against
  # the Core this run is about to stage (R66 finding 3), so this run cannot hold on it -- and it
  # must not release the plugins before its own Core either.
  if ($before.InFlight) {
    Write-Warning ("[$($t.Name)] a Core swap is already in flight in this process (generation " +
      "$($before.Generation)); this run cannot tell that swap's completion from its own.")
    $t.WithholdReason = "another Core swap was in flight, so this run's swap cannot be observed"
    continue
  }
  # The watcher has PARKED on the image sitting in .next (R68 finding 5): it asked for that swap,
  # the request was refused, and it will not read that file again until its bytes change or the
  # process restarts (CoreWatch.Park, job FREL8). This run is about to change those bytes (an
  # identical image took the CoreSkipStage branch above), which releases the park, and a parked
  # file cannot be swapped before we overwrite it -- so the only swap that can follow is of the
  # image this run stages, and the hold is armed. The wait's park branch then decides, with the
  # R69 finding 7 stamp test, whether the watcher refused this run's image as well.
  if ($before.WatchParked -and $before.PendingNext) {
    Write-Host ("[$($t.Name)] the watcher had PARKED on a different Core staged as progenitor-core\Progenitor.dll.next" +
      $(if ($before.WatchParkedReason) { " ('$($before.WatchParkedReason)')" } else { "" }) +
      "; this run replaces it, which releases the park, and holds the plugins for the retry.")
  } elseif ($before.PendingNext) {
    # A .next already waiting and NOT parked belongs to someone else: the watcher may take it in
    # the moment between this read and our write, and that swap's completion would be counted as
    # ours (R66 finding 3).
    Write-Warning ("[$($t.Name)] a different Core swap is already staged and pending in this process (generation " +
      "$($before.Generation)); this run replaces that image but cannot tell that swap's completion from its own.")
    $t.WithholdReason = "another Core swap was pending, so this run's swap cannot be observed"
    continue
  }
  $t.GenerationBefore = $before.Generation
  $t.HoldPlugins = $true
}

foreach ($entry in $resolvedProjects) {
  $proj = $entry.Proj
  $dll = $entry.Dll

  foreach ($t in $targets) {
    if (-not (Test-Path $t.Dir)) { continue }

    if ($dll.Name -eq "Progenitor.Bootstrap.dll") {
      Copy-RestartOnly $dll.FullName (Join-Path $t.Home $dll.Name) $t.Name $t.Proc $t.Running $proj
      continue
    }
    if ($dll.Name -eq "Progenitor.Preloader.dll") {
      # The client's BepInEx folders (patchers included) are junctioned into the bundled
      # VRising_Server child of a private game, which loads the same patchers file: a running child
      # holds it just as the client does. Matched by path under this game root only.
      $inUse = $t.Running
      if ($t.Name -eq "client" -and -not $inUse -and (Test-Running "VRisingServer" $GameDir)) {
        Write-Warning "[client] a hosted VRisingServer.exe under $GameDir is running and shares BepInEx/patchers through a junction."
        $inUse = $true
      }
      Copy-ShippedPreloader $dll.FullName $t.Home $t.Name $proj
      Copy-Preloader $dll.FullName $t.Patchers $t.Name $t.Proc $inUse $proj
      continue
    }
    if ($dll.Name -eq "Progenitor.dll") {
      # Nothing to write: the image this run would stage is already on disk in that role --
      # either as the pending .next or, with no .next at all, as the live copy (R66 finding 2,
      # R67 finding 2). The reason was printed once, above, when the comparison was made.
      # CoreUnchanged alone is NOT a reason to skip the write: when a different .next is
      # pending, staging the built image is what replaces it.
      if ($t.CoreSkipStage) { continue }
      # The moment this run staged, so the wait can tell OUR swap from one that was already
      # running (R66 finding 3). Taken before the copy, so a slow copy cannot exclude our own swap.
      if ($t.Running -and -not $plan) { $t.StagedAtUtc = (Get-Date).ToUniversalTime() }
      Copy-RestartOnly $dll.FullName (Join-Path $t.Core $t.CoreFile) $t.Name $t.Proc $t.Running $proj
      continue
    }

    if ($t.HoldPlugins -or $t.WithholdReason) { $t.Deferred.Add(@{ Dll = $dll; Proj = $proj }) | Out-Null; continue }
    Copy-PluginToReload $t $dll $proj
  }
}

# The three-location layout, once the single-folder copies are in place (see the header).
foreach ($t in $targets) {
  if (Test-Path $t.Dir) { Remove-LegacyLayout $t }
}

# Sides the pre-check already decided cannot release their plugins (a hosted game, a refusal,
# no bridge, the watcher off, a contended or not-yet-running staged Core): withheld now, before
# any wait, with the reason recorded (or copied anyway under -ReleaseHeldPlugins).
foreach ($t in @($targets | Where-Object { $_.WithholdReason })) {
  Resolve-HeldPlugins $t $false $t.WithholdReason
}

# The held plugins, once the Core they were built against is the Core that is running.
# Wait-CoreSwaps never throws: on a refusal, a hosted game, a park, a timeout, an abandoned side
# or a quarantine it warns and WITHHOLDS that side's plugins (deploy-hold defect, 2026-10-06;
# this used to copy them anyway, see Resolve-HeldPlugins).
#
# The `finally` (R66 finding 11) now resolves rather than copies. Ctrl-C inside the wait's
# Start-Sleep -- after Progenitor.dll.next is on disk and before a held plugin is copied -- leaves
# the previous plugin build in the reload folder and the new Core staged. That used to be called
# the worse mismatch; it is the SAFE one: the previous plugins were built against an older Core,
# which the new Core still serves far more often than an old Core serves new plugins, and the
# finally reports the withheld files so the operator re-runs the deploy once the swap lands.
#
# ONE DEADLINE FOR EVERY SIDE, AND ONE INTERLEAVED LOOP (R67 finding 9, corrected by R68
# finding 2). Both Cores are already staged by the time this runs, so both swaps are running
# concurrently; one shared deadline taken here makes the sides expire together instead of back
# to back. But two waits run BACK TO BACK against a shared deadline let the first side spend all
# of it -- an unanswering bridge did precisely that -- and the second side then polled once and
# had its plugins copied ahead of an unobserved swap. So Wait-CoreSwaps polls every side still
# waiting once per second, releases each side's plugins the moment that side's own wait ends,
# and abandons a side after $script:CoreSwapSilence seconds of silence -- with every poll itself
# bounded at $script:CoreSwapPollTimeout seconds, so a hanging bridge cannot hold another side's
# turn or outrun the abort (R69 finding 2).
#
# Resolve-HeldPlugins is idempotent per side, so a side the wait already resolved is not
# resolved twice by the `finally`.
$heldSides = @($targets | Where-Object { $_.HoldPlugins -and $_.Deferred -and $_.Deferred.Count -gt 0 })
if ($heldSides.Count -gt 0) {
  $holdDeadline = (Get-Date).AddSeconds($CoreSwapTimeoutSeconds)
  try {
    Wait-CoreSwaps $heldSides $holdDeadline | Out-Null
  } finally {
    foreach ($t in $heldSides) {
      Resolve-HeldPlugins $t $false "the wait for the Core swap was interrupted before the swap was observed"
    }
  }
}

# WAIT, NOT DONE: one closing block naming every withheld file, and a distinct exit code so a
# caller (tools/mcp progenitor_deploy prints it) cannot mistake a staged-but-not-running Core for
# a finished deploy.
if ($script:Withheld.Count -gt 0) {
  Write-Host ""
  Write-Host "=== DEPLOY INCOMPLETE (WAIT): plugins withheld until the staged Core is running ==="
  foreach ($w in $script:Withheld) {
    Write-Host ("  [{0}] {1} -- {2}" -f $w.Side, $w.Files, $w.Reason)
  }
  Write-Host ("  " + $script:WithheldNextStep)
  exit 3
}

if ($plan) {
  Write-Host ""
  Write-Host "=== deploy plan (-WhatIf): nothing was written ==="
  foreach ($row in $script:PlanRows) {
    Write-Host ("{0}  [{1}] {2} -> {3}{4}" -f $row.Sha256, $row.Side, $row.Project, $row.Dest, $(if ($row.Note) { "  ($($row.Note))" } else { "" }))
  }
  Write-Host ""
  Write-Host ("--- {0} file(s) would be copied ---" -f $script:PlanRows.Count)
  foreach ($g in ($script:PlanRows | Group-Object Side, Project | Sort-Object Name)) {
    Write-Host ("  {0,-40} {1}" -f $g.Name, $g.Count)
  }
}

<#
.SYNOPSIS
  Unattended verification rig, server half (job FRIG1, roadmap M2.13 item 13).

.DESCRIPTION
  Every live check in the evidence log from 2026-09-05 has the same shape: create a ghost admin,
  send a few chat commands through the Dev bridge, wait, then grep the in-memory log ring for the
  lines the feature is supposed to write. That is a script, and this is it.

      tools/verify.ps1 -Case all
      tools/verify.ps1 -Case config-watch -Json -OutDir C:\Temp\progenitor-verify

  A case is a JSON file in tools/verify/. It declares what it needs and a list of steps; the
  runner pings the side, resolves (or creates) the ghost admin, records the ring's sequence,
  runs the steps in order, evaluates each expectation against the lines that arrived after the
  most recent command or file edit, prints one PASS/FAIL line per step, restores any file it
  edited, destroys the ghost it created (and only that one), and exits 0 only if every step of
  every case passed.

  See tools/verify/README.md for the case-file schema.

.NOTES
  Fences, deliberately narrow:
    * Assert-NotLiveServerPath (tools/common/Fence.ps1) runs on -TestRoot, on every path derived
      from it, on -OutDir, and on every file an editFile step names. C:\VRisingServer under any
      spelling is refused before the first read, let alone the first write.
    * An editFile path must also resolve inside -TestRoot's BepInEx tree. A case cannot reach out
      of the test server.
    * Only read-and-inject bridge ops are callable from a case file ($AllowedOps). quit, host,
      connect, disconnect, core_reload, reload, save, world_save, spawn, set and friends are
      refused by this script: it never starts, stops, restarts or reconfigures anything, and it
      opens no window. 'ghost' is NOT on that list either: it writes and destroys world entities,
      and the runner owns the ghost lifecycle, so only the runner's own two calls make it
      ($RunnerOps, reachable solely through Invoke-Bridge -Runner). A case file that names it is
      refused like any other write op.
    * The ghost the runner destroys is the ghost the runner created. The bridge's destroy is
      all-or-nothing over the whole ghost PlatformId range (docs/bridge-ops.md), so the runner
      refuses to create one at all while a ghost it did not make is on the server: it either
      reuses an existing admin ghost and touches nothing, or it is the only ghost there and its
      sweep takes exactly what it made.
    * That guard fails CLOSED on the one answer it must (R68 finding 3). The bridge serves one
      main-thread op at a time, so 'ghost list' going unanswered behind somebody else's op is
      ordinary, not exceptional. An unanswered or malformed list is retried $GhostListAttempts
      times and then refuses: the run stops rather than creating a ghost beside a foreign one,
      and the cleanup leaves the range alone rather than sweeping it. The sweep runs only when a
      fresh list answered and holds exactly the one index this run created.
    * It never builds, deploys or promotes. The processes it talks to are whatever is already
      running.

.EXAMPLE
  powershell -NoProfile -File tools/verify.ps1 -Case all
.EXAMPLE
  powershell -NoProfile -File tools/verify.ps1 -ListCases
#>
[CmdletBinding()]
param(
  # Case name (the file name in tools/verify/ without .json), or "all".
  [Parameter(Position = 0)] [string]$Case = "all",
  # Which side to drive. Only the server half exists; the client half is roadmap item 13's other half.
  [ValidateSet("server")] [string]$Side = "server",
  # Also write a machine-readable result file under -OutDir.
  [switch]$Json,
  # Where the JSON result and the pre-edit file backups go. Never inside the test server.
  [string]$OutDir = "",
  # The staged test server. Read for the config/reload folders a case names as <config> / <reload>.
  # Defaults to <workspace>\_testserver (tools/common/Workspace.ps1).
  [string]$TestRoot = "",
  # Print the case list and exit.
  [switch]$ListCases
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Off

# ---------------------------------------------------------------------------- paths and fences

. (Join-Path $PSScriptRoot "common\Fence.ps1")
# Fence.ps1 dot-sources common\Workspace.ps1; an unresolvable workspace throws here.
if (-not $TestRoot) { $TestRoot = Resolve-ProgenitorWorkspacePath -Child "_testserver" -Parameter "-TestRoot" }

Assert-NotLiveServerPath $TestRoot

$RepoRoot  = Split-Path $PSScriptRoot -Parent
$CallJs    = Join-Path $PSScriptRoot "mcp\call.mjs"
$CaseDir   = Join-Path $PSScriptRoot "verify"
$ServerDir = Join-Path $TestRoot "server"
$BepInExDir = Join-Path $ServerDir "BepInEx"
$ConfigDir = Join-Path $BepInExDir "config"
$ReloadDir = Join-Path $BepInExDir "progenitor-reload"
foreach ($p in @($ServerDir, $BepInExDir, $ConfigDir, $ReloadDir)) { Assert-NotLiveServerPath $p }

if ([string]::IsNullOrWhiteSpace($OutDir)) {
  $OutDir = Join-Path ([System.IO.Path]::GetTempPath()) "progenitor-verify"
}
Assert-NotLiveServerPath $OutDir
# Backups of edited files land under -OutDir, so -OutDir must not be inside the tree being edited.
if (Test-PathUnderRoot -Path $OutDir -Root $TestRoot) {
  throw "-OutDir '$OutDir' is inside the test server; put the results and backups somewhere else."
}

# The bridge's token file is derived from PROGENITOR_TEST_ROOT by tools/mcp/call.mjs. Point it at
# the same root this run was given, unless the caller has already pointed it somewhere itself.
if (-not $env:PROGENITOR_TEST_ROOT) { $env:PROGENITOR_TEST_ROOT = $TestRoot }

# Ops a case file may call. Everything that starts, stops, saves, spawns, writes or moves the game
# is absent on purpose: a verification run must not change the thing it is measuring.
$AllowedOps = @(
  "ping", "logs", "ops", "users", "worlds", "players", "status", "store",
  "query", "get", "components", "prefab", "inspect", "castles",
  "command", "chat_observe"
)

# Ops only the runner itself may call, through Invoke-Bridge -Runner. 'ghost' create writes a
# [User, Simulate] entity into the live world and 'ghost' destroy removes every entity in the ghost
# PlatformId range, so gating it by op name alone would let a case file create and destroy world
# entities and desync the cleanup bookkeeping below. The runner owns the ghost lifecycle; a case
# declares it needs one with requires.ghostAdmin and never touches the op.
$RunnerOps = @("ghost")

# ---------------------------------------------------------------------------- small helpers

$script:TempDir = Join-Path $OutDir ("run-" + (Get-Date).ToString("yyyyMMdd-HHmmss"))
$script:Backups = @{}          # full path -> original bytes
$script:GhostCreated = $false
$script:GhostIndex = $null
# Run scope, not case scope (R69 finding 1): the ghost admin is resolved once, before the first
# case that needs one, every case uses that index, and the run's finally destroys it iff this run
# created it. GhostResolved is what makes Resolve-GhostAdmin idempotent; GhostHow is the sentence
# it printed, reprinted per case so each case still says which ghost it is running as.
$script:GhostResolved = $false
$script:GhostHow = ""
$script:PingResult = $null    # the run's ping payload; clearsWhen reads the Core identity out of it

function New-DirIfMissing([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
}

function Write-Utf8NoBom([string]$path, [string]$text) {
  $enc = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText($path, $text, $enc)
}

function Has-Prop($obj, [string]$name) {
  if ($null -eq $obj) { return $false }
  return @($obj.PSObject.Properties.Name) -contains $name
}

function Prop($obj, [string]$name, $fallback) {
  if (Has-Prop $obj $name) {
    $v = $obj.$name
    if ($null -ne $v) { return $v }
  }
  return $fallback
}

# ---------------------------------------------------------------------------- the bridge

function Invoke-Bridge {
  # $Payload, not $Args: $args is an automatic variable and a parameter of that name shadows it.
  param([string]$Op, [hashtable]$Payload, [switch]$Runner)

  if ($Runner) {
    if (($RunnerOps -notcontains $Op) -and ($AllowedOps -notcontains $Op)) {
      throw "verify.ps1 refuses the bridge op '$Op' even for the runner (runner-only: $($RunnerOps -join ', '))."
    }
  }
  elseif ($AllowedOps -notcontains $Op) {
    if ($RunnerOps -contains $Op) {
      throw "verify.ps1 refuses the bridge op '$Op' from a case file: the runner owns it. Declare requires.ghostAdmin instead."
    }
    throw "verify.ps1 refuses the bridge op '$Op': this rig only reads and injects (allowed: $($AllowedOps -join ', '))."
  }

  $argFile = $null
  $cmd = @($CallJs, $Side, $Op)
  if ($Payload -and $Payload.Count -gt 0) {
    New-DirIfMissing $script:TempDir
    $argFile = Join-Path $script:TempDir ("args-" + [guid]::NewGuid().ToString("N") + ".json")
    # @file, not an inline argument: PowerShell strips double quotes from arguments handed to a
    # native program (the note in tools/mcp/call.mjs), and no BOM, or Node's JSON.parse throws.
    Write-Utf8NoBom $argFile ($Payload | ConvertTo-Json -Depth 8 -Compress)
    $cmd += ("@" + $argFile)
  }

  # No bridge-queue mail on stderr: the merged output is parsed as JSON (docs/bridge.md#bridge-queue).
  $callNotesPreset = $env:PROGENITOR_CALL_NOTES
  $env:PROGENITOR_CALL_NOTES = "0"
  try { $raw = & node @cmd 2>&1; $code = $LASTEXITCODE }
  finally { $env:PROGENITOR_CALL_NOTES = $callNotesPreset }
  if ($argFile) { Remove-Item -LiteralPath $argFile -Force -ErrorAction SilentlyContinue }

  $text = ($raw | Out-String).Trim()
  $obj = $null
  if ($code -eq 0 -and $text.Length -gt 0) {
    try { $obj = $text | ConvertFrom-Json } catch { $obj = $null }
  }
  return [pscustomobject]@{ ExitCode = $code; Text = $text; Result = $obj }
}

function Get-RingSeq {
  # ping's logSeq is the ring's current head. 'logs' with since:-1 would return the whole ring.
  $r = Invoke-Bridge "ping" $null
  if ($r.ExitCode -ne 0 -or $null -eq $r.Result) { throw "bridge ping failed (exit $($r.ExitCode)): $($r.Text)" }
  return [int64]$r.Result.logSeq
}

function Get-LogLines {
  <#
    Every ring line with seq > $Since, oldest first, as { Seq, Level, Source, Text, Subject }.
    Subject is what expectations are matched against: "[Level] Source: text" — so a pattern can
    name the severity ("\[Error\]") as easily as the message.
  #>
  param([int64]$Since, [string]$Filter = "")

  $lines = New-Object System.Collections.ArrayList
  $cursor = $Since
  for ($page = 0; $page -lt 20; $page++) {
    $a = @{ since = $cursor; max = 2000 }
    if ($Filter) { $a["filter"] = $Filter }
    $r = Invoke-Bridge "logs" $a
    if ($r.ExitCode -ne 0 -or $null -eq $r.Result) { throw "bridge logs failed (exit $($r.ExitCode)): $($r.Text)" }
    $batch = @($r.Result.lines)
    foreach ($l in $batch) {
      $subject = "[" + $l.level + "] " + $l.source + ": " + $l.text
      [void]$lines.Add([pscustomobject]@{
        Seq = [int64]$l.seq; Level = [string]$l.level; Source = [string]$l.source
        Text = [string]$l.text; Subject = $subject
      })
      $cursor = [int64]$l.seq
    }
    if ($batch.Count -lt 2000) { break }
  }
  return $lines
}

# ---------------------------------------------------------------------------- ghost admin

# How hard the runner tries to get an answer out of 'ghost list' before it refuses. The bridge
# serves one main-thread op at a time, so a list that loses a race with a concurrent op is the
# ordinary case, not the exceptional one; a couple of seconds of patience is much cheaper than
# either half of the wrong answer (refusing a clean run, or sweeping somebody else's ghost).
$GhostListAttempts = 3
$GhostListRetryMs  = 1500

function Get-GhostList {
  <#
    The tracked-ghost list as { Ok, Ghosts, Reason }, retried $GhostListAttempts times.

    Ok is $true only when the bridge answered a list this runner can act on: exit 0, JSON,
    ok=true, a 'ghosts' member, and an integer 'index' on every row. Ghosts is then the rows
    (possibly zero of them — an empty server is a perfectly good answer).

    Ok is $false for "the op did not answer" and for "it answered something else", and the two
    callers must treat that as a refusal, never as permission: R68 finding 3 is exactly the run
    where an unanswered list let the rig create a ghost beside the main session's 10001 and then
    sweep the whole PlatformId range under a line saying "created by this run".

    A pscustomobject rather than an array, deliberately: `return @()` from a PowerShell function
    reaches the caller as $null, so an array return cannot distinguish "no ghosts" from
    "no answer" — which is the distinction this whole function exists to make.
  #>
  param([string]$Purpose = "")

  $why = "not attempted"
  for ($attempt = 1; $attempt -le $GhostListAttempts; $attempt++) {
    $list = $null
    try { $list = Invoke-Bridge "ghost" @{ action = "list" } -Runner }
    catch { $list = $null; $why = "the call threw: " + $_.Exception.Message }

    if ($null -ne $list) {
      if ($list.ExitCode -ne 0) { $why = "bridge exit $($list.ExitCode): " + (Elide $list.Text 160) }
      elseif ($null -eq $list.Result) { $why = "the answer was not JSON: " + (Elide $list.Text 160) }
      elseif (-not $list.Result.ok) { $why = "the op answered ok=false: " + (Elide $list.Text 160) }
      elseif (-not (Has-Prop $list.Result "ghosts")) { $why = "the answer carries no 'ghosts' member: " + (Elide $list.Text 160) }
      else {
        $rows = @($list.Result.ghosts)
        $bad = @($rows | Where-Object { -not (Has-Prop $_ "index") -or ($null -eq ($_.index -as [int])) })
        if ($bad.Count -gt 0) {
          $why = "$($bad.Count) of $($rows.Count) row(s) carry no integer 'index': " + (Elide $list.Text 160)
        }
        else {
          return [pscustomobject]@{ Ok = $true; Ghosts = $rows; Reason = "" }
        }
      }
    }

    $where = if ($Purpose) { " ($Purpose)" } else { "" }
    Write-Warning "ghost list attempt $attempt of ${GhostListAttempts}${where} did not answer with a list: $why"
    if ($attempt -lt $GhostListAttempts) { Start-Sleep -Milliseconds $GhostListRetryMs }
  }
  return [pscustomobject]@{ Ok = $false; Ghosts = @(); Reason = $why }
}

function Resolve-GhostAdmin {
  <#
    Resolve the run's ghost admin ONCE, for the whole run. Reuse one that is already there (a
    previous run that was interrupted, or the maintainer's own) and leave it completely alone;
    otherwise create one and remember the index we created, so only that ghost is destroyed at
    the end.

    Called once per RUN, not once per case (R69 finding 1). All eight case files declare
    requires.ghostAdmin and -Case defaults to "all", so the per-case call it used to be meant
    case 2 listed, found the admin ghost CASE 1 HAD JUST CREATED, took the reuse branch and
    cleared GhostCreated -- and the run's finally then returned at its first line, leaving the
    rig's own ghost on the server without a word, with the whole fail-closed sweep R68 finding 3
    hardened unreachable on every default run. Resolving at run scope also removes seven
    redundant 'ghost list' round trips from the game's main thread. The idempotence guard below
    makes a second call a no-op rather than a second listing, so no caller can re-enter the reuse
    branch over this run's own ghost.

    The bridge's 'destroy' has no per-ghost form: it removes every tracked ghost plus any leftover
    in the ghost PlatformId range (docs/bridge-ops.md). "Destroy only the one we created" is therefore
    achieved by never creating one while somebody else's ghost is on the server -- a maintainer's
    non-admin ghost (a chat-broadcast check, say) must not vanish mid-run because this rig wanted an
    admin. If that happens the run stops here and says what to do.

    "I could not find out" is not "there is none" (R68 finding 3): a list the bridge never
    answered refuses the run too, because the create it would license is the first half of a
    sweep that takes somebody else's ghost.
  #>
  if ($script:GhostResolved) { return $script:GhostHow }

  $listing = Get-GhostList "before creating a ghost admin"
  if (-not $listing.Ok) {
    throw ("'ghost list' did not answer a list this run can trust after $GhostListAttempts attempt(s) " +
           "($($listing.Reason)). Refusing to create a ghost: the bridge serves one main-thread op at a time, so an " +
           "unanswered list is ordinary, and creating one blind would mean sweeping the whole ghost PlatformId range " +
           "at cleanup with no idea whose ghosts are in it. Re-run when the bridge is answering.")
  }

  $ghosts = @($listing.Ghosts)
  foreach ($g in $ghosts) {
    if ($g.admin -eq $true) {
      $script:GhostIndex = [int]$g.index
      # Only a ghost this run did NOT create is adopted read-only. The guard is belt-and-braces
      # behind the idempotence check above: this function is called once per run now, so the
      # ghost we created can no longer come back round as somebody else's (R69 finding 1), and
      # if a future caller ever does re-enter here, the flag must not be cleared over our own.
      if (-not ($script:GhostCreated -and ($script:GhostIndex -eq [int]$g.index))) {
        $script:GhostCreated = $false
      }
      $script:GhostResolved = $true
      $script:GhostHow = "reused ghost $($g.name) (userIndex $($g.index)); this run will not destroy it"
      return $script:GhostHow
    }
  }
  if ($ghosts.Count -gt 0) {
    $names = (@($ghosts | ForEach-Object { "$($_.name)/$($_.index)" }) -join ", ")
    throw ("the server already has $($ghosts.Count) ghost(s) this run did not create ($names) and none of them is " +
           "an admin. The bridge's 'ghost destroy' is all-or-nothing over the whole ghost PlatformId range, so " +
           "creating an admin ghost here would mean sweeping those away at cleanup. Destroy them yourself " +
           "(or make one of them an admin) and re-run.")
  }

  # From here on the server is known to hold no ghost at all, so anything the next list reports
  # was made by this call and nothing else.
  $c = Invoke-Bridge "ghost" @{ action = "create"; count = 1; admin = $true } -Runner
  if ($c.ExitCode -ne 0 -or $null -eq $c.Result -or -not $c.Result.ok) {
    # R69 finding 10: an unanswered create is not a create that did not happen. call.mjs gives up
    # after its own timeout and destroys the socket; the op is already queued on the game's main
    # thread and, when its turn comes, it creates the entity. Failing open here left an admin
    # ghost on the test save that GhostCreated never recorded, the cleanup never swept and the
    # throw's text denied -- the same "I could not find out" R68 finding 3 turned into a refusal
    # on the list half of this function. So look before throwing, and adopt what we find: the
    # pre-create list was empty and this run is the only thing that has asked for a ghost since.
    $after = Get-GhostList "after a 'ghost create' that did not answer"
    if ($after.Ok) {
      $rows = @($after.Ghosts)
      $mine = @($rows | Where-Object { $_.admin -eq $true })
      if ($mine.Count -ge 1) {
        $g = $mine[0]
        $script:GhostIndex = [int]$g.index
        $script:GhostCreated = $true
        $script:GhostResolved = $true
        $script:GhostHow = ("adopted ghost $($g.name) (userIndex $($g.index)): 'ghost create' did not answer " +
          "(exit $($c.ExitCode)) but the op had already run, and the list taken before it was empty, so this " +
          "ghost is this run's. It will be destroyed at the end like any other ghost this run created.")
        Write-Warning $script:GhostHow
        return $script:GhostHow
      }
      throw ("ghost create failed (exit $($c.ExitCode)): $($c.Text). A fresh 'ghost list' then reported " +
             "$($rows.Count) ghost(s) and no admin among them, so the create really did not take effect.")
    }
    throw ("ghost create failed (exit $($c.ExitCode)): $($c.Text). 'ghost list' could not confirm what happened " +
           "either ($($after.Reason)), so a ghost MAY have been created anyway and this run cannot see it: run " +
           "the bridge's 'ghost list' by hand and destroy a leftover admin ghost before the next run.")
  }
  $g = @($c.Result.ghosts)[0]
  $script:GhostIndex = [int]$g.index
  $script:GhostCreated = $true
  $script:GhostResolved = $true
  $script:GhostHow = "created ghost $($g.name) (userIndex $($g.index))"
  return $script:GhostHow
}

function Remove-GhostIfCreated {
  if (-not $script:GhostCreated) { return }
  try {
    # Last check before an all-or-nothing sweep: a FRESH list must answer, and it must hold exactly
    # the one index this run created and nothing else. If somebody added a ghost while the run was
    # going (a maintainer's own -Case all takes minutes), destroying would take theirs too; and if
    # the list did not answer at all, we do not know what the sweep would take, which is the same
    # answer (R68 finding 3). Both cases leave the range alone and say so.
    $listing = Get-GhostList "before the destroy sweep"
    if (-not $listing.Ok) {
      Write-Warning ("ghost destroy skipped: 'ghost list' did not answer after $GhostListAttempts attempt(s) " +
                     "($($listing.Reason)), so this run cannot tell whether the sweep would take only its own " +
                     "ghost. Ghost userIndex $script:GhostIndex is still on the server; destroy it by hand " +
                     "(bridge op: ghost destroy) once the bridge answers and no ghost you want kept is there.")
      $script:GhostCreated = $false
      return
    }

    $ghosts = @($listing.Ghosts)
    $mine = @($ghosts | Where-Object { [int]$_.index -eq [int]$script:GhostIndex })
    $others = @($ghosts | Where-Object { [int]$_.index -ne [int]$script:GhostIndex })
    if ($others.Count -gt 0) {
      $names = (@($others | ForEach-Object { "$($_.name)/$($_.index)" }) -join ", ")
      Write-Warning ("ghost destroy skipped: $($others.Count) ghost(s) this run did not create are on the server " +
                     "($names) and 'ghost destroy' would take them too. Ghost userIndex $script:GhostIndex is " +
                     "still there; destroy it by hand when the others are gone.")
      $script:GhostCreated = $false
      return
    }
    if ($mine.Count -ne 1) {
      Write-Host ("  ghost destroy skipped: the list is empty, so ghost userIndex $script:GhostIndex is already " +
                  "gone and there is nothing this run may sweep.")
      $script:GhostCreated = $false
      return
    }

    $r = Invoke-Bridge "ghost" @{ action = "destroy" } -Runner
    if ($r.ExitCode -eq 0 -and $null -ne $r.Result) {
      Write-Host ("  ghost destroy (userIndex $script:GhostIndex, created by this run): destroyed=" +
        $r.Result.destroyed + " leftovers=" + $r.Result.leftovers)
    } else {
      Write-Warning "ghost destroy failed: $($r.Text)"
    }
  } catch {
    Write-Warning "ghost destroy threw: $($_.Exception.Message)"
  }
  $script:GhostCreated = $false
}

# ---------------------------------------------------------------------------- file edits

function Resolve-CasePath([string]$raw) {
  $p = $raw
  $p = $p.Replace("<config>", $ConfigDir)
  $p = $p.Replace("<reload>", $ReloadDir)
  $p = $p.Replace("<server>", $ServerDir)
  $p = $p.Replace("<testroot>", $TestRoot)
  $p = $p.Replace("/", "\")
  $full = [System.IO.Path]::GetFullPath($p)
  Assert-NotLiveServerPath $full
  if (-not (Test-PathUnderRoot -Path $full -Root $TestRoot)) {
    throw "case path '$raw' resolves to '$full', which is outside the test server root '$TestRoot'."
  }
  return $full
}

function Backup-Once([string]$path) {
  if ($script:Backups.ContainsKey($path)) { return }
  $bytes = [System.IO.File]::ReadAllBytes($path)
  $script:Backups[$path] = $bytes
  New-DirIfMissing $script:TempDir
  $copy = Join-Path $script:TempDir ("backup-" + [System.IO.Path]::GetFileName($path))
  if (Test-Path -LiteralPath $copy) {
    $copy = Join-Path $script:TempDir ("backup-" + [guid]::NewGuid().ToString("N") + "-" + [System.IO.Path]::GetFileName($path))
  }
  [System.IO.File]::WriteAllBytes($copy, $bytes)
}

function Restore-EditedFiles {
  foreach ($path in @($script:Backups.Keys)) {
    try {
      $original = $script:Backups[$path]
      $same = $false
      if (Test-Path -LiteralPath $path) {
        $current = [System.IO.File]::ReadAllBytes($path)
        if ($current.Length -eq $original.Length) {
          $same = $true
          for ($i = 0; $i -lt $current.Length; $i++) { if ($current[$i] -ne $original[$i]) { $same = $false; break } }
        }
      }
      if ($same) { Write-Host "  restore: $path already matches the original"; continue }
      [System.IO.File]::WriteAllBytes($path, $original)
      Write-Host "  restore: rewrote $path from the pre-run copy"
    } catch {
      Write-Warning "restore of '$path' failed: $($_.Exception.Message) (the pre-run copy is under $script:TempDir)"
    }
  }
  $script:Backups = @{}
}

# ---------------------------------------------------------------------------- step execution

function New-StepResult([int]$index, [string]$kind, [string]$description, [string]$status, [string]$detail) {
  return [pscustomobject]@{
    Index = $index; Kind = $kind; Description = $description; Status = $status; Detail = $detail
  }
}

function Write-StepLine($result) {
  $tag = "{0,-4}" -f $result.Status
  $line = "  $tag #$($result.Index) $($result.Kind): $($result.Description)"
  if ($result.Detail) { $line += "  | " + $result.Detail }
  switch ($result.Status) {
    "FAIL" { Write-Host $line -ForegroundColor Red }
    "PASS" { Write-Host $line -ForegroundColor Green }
    "SKIP" { Write-Host $line -ForegroundColor Yellow }
    default { Write-Host $line }
  }
}

function Elide([string]$text, [int]$max) {
  if ($null -eq $text) { return "" }
  $t = $text -replace "\s+", " "
  if ($t.Length -le $max) { return $t }
  return $t.Substring(0, $max) + "..."
}

function Invoke-CaseStep {
  param($Step, [int]$Index, [ref]$WindowSeq, $CaseState)

  # --- note -----------------------------------------------------------------
  if (Has-Prop $Step "note") {
    return New-StepResult $Index "note" (Elide ([string]$Step.note) 160) "INFO" ""
  }

  # --- sleepMs --------------------------------------------------------------
  if (Has-Prop $Step "sleepMs") {
    $ms = [int]$Step.sleepMs
    Start-Sleep -Milliseconds $ms
    return New-StepResult $Index "sleep" ("$ms ms") "INFO" ""
  }

  # --- resetWindow ----------------------------------------------------------
  # Move the match window to the ring head without touching the game. A case whose precondition
  # steps produce log lines of their own (an edit that arms the state the case is about, and the
  # reload that edit causes) needs to say "everything above was setup" before its assertions run.
  # The alternative — injecting a dummy command purely to move the window — puts chat traffic on
  # the server to buy a bookkeeping effect, which is worse.
  if (Has-Prop $Step "resetWindow") {
    $WindowSeq.Value = Get-RingSeq
    return New-StepResult $Index "resetWindow" "match window moved to the ring head" "INFO" `
      ("seq " + $WindowSeq.Value)
  }

  # --- command --------------------------------------------------------------
  if (Has-Prop $Step "command") {
    $text = [string]$Step.command
    $WindowSeq.Value = Get-RingSeq     # the command's own lines belong to its window
    $a = @{ text = $text }
    if (Has-Prop $Step "userIndex") { $a["userIndex"] = [int]$Step.userIndex }
    elseif ($null -ne $CaseState.GhostIndex) { $a["userIndex"] = [int]$CaseState.GhostIndex }
    $r = Invoke-Bridge "command" $a
    if ($r.ExitCode -ne 0 -or $null -eq $r.Result) {
      return New-StepResult $Index "command" $text "FAIL" ("bridge exit $($r.ExitCode): " + (Elide $r.Text 200))
    }
    $as = Prop $r.Result "injectedAs" "?"
    return New-StepResult $Index "command" $text "PASS" ("injected as $as")
  }

  # --- op -------------------------------------------------------------------
  if (Has-Prop $Step "op") {
    $op = [string]$Step.op
    $WindowSeq.Value = Get-RingSeq
    $a = @{}
    if (Has-Prop $Step "args") {
      foreach ($p in $Step.args.PSObject.Properties) { $a[$p.Name] = $p.Value }
    }
    $r = Invoke-Bridge $op $a
    if ($r.ExitCode -ne 0) {
      return New-StepResult $Index "op" $op "FAIL" ("bridge exit $($r.ExitCode): " + (Elide $r.Text 200))
    }
    if (Has-Prop $Step "expectJson") {
      $pattern = [string]$Step.expectJson
      if ($r.Text -match $pattern) {
        return New-StepResult $Index "op" "$op =~ $pattern" "PASS" (Elide $r.Text 160)
      }
      return New-StepResult $Index "op" "$op =~ $pattern" "FAIL" ("no match in: " + (Elide $r.Text 200))
    }
    return New-StepResult $Index "op" $op "PASS" (Elide $r.Text 160)
  }

  # --- editFile -------------------------------------------------------------
  if (Has-Prop $Step "editFile") {
    $e = $Step.editFile
    $path = Resolve-CasePath ([string]$e.path)
    if (-not (Test-Path -LiteralPath $path)) {
      return New-StepResult $Index "editFile" ([string]$e.path) "FAIL" "no such file: $path"
    }
    Backup-Once $path
    $text = [System.IO.File]::ReadAllText($path)
    $re = New-Object System.Text.RegularExpressions.Regex([string]$e.replace)
    $hits = $re.Matches($text).Count
    $wantHits = [int](Prop $e "count" 1)
    if ($hits -lt $wantHits) {
      return New-StepResult $Index "editFile" ("$([System.IO.Path]::GetFileName($path)): /$($e.replace)/") "FAIL" `
        "the pattern matched $hits time(s), expected at least $wantHits"
    }
    $WindowSeq.Value = Get-RingSeq     # the reload the edit causes belongs to this step's window
    $new = $re.Replace($text, [string]$e.with)
    Write-Utf8NoBom $path $new
    return New-StepResult $Index "editFile" ("$([System.IO.Path]::GetFileName($path)): /$($e.replace)/ -> '$($e.with)'") "PASS" `
      "$hits replacement(s)"
  }

  # --- expectLog ------------------------------------------------------------
  if (Has-Prop $Step "expectLog") {
    $pattern = [string]$Step.expectLog
    $scope = [string](Prop $Step "scope" "window")
    $since = switch ($scope) {
      "case" { $CaseState.StartSeq }
      "ring" { 0 }
      default { $WindowSeq.Value }
    }
    $filter = [string](Prop $Step "filter" "")
    $lines = Get-LogLines $since $filter
    if (Has-Prop $Step "within") { $lines = @($lines | Select-Object -First ([int]$Step.within)) }
    $hits = @($lines | Where-Object { $_.Subject -match $pattern })

    $desc = "/$pattern/"
    if ($scope -ne "window") { $desc += " (scope $scope)" }
    if (Has-Prop $Step "count") {
      $want = [int]$Step.count
      if ($hits.Count -eq $want) {
        return New-StepResult $Index "expectLog" "$desc x$want" "PASS" (Elide (@($hits)[0].Subject) 200)
      }
      $detail = "matched $($hits.Count) line(s), expected exactly $want"
      if ($hits.Count -gt 0) { $detail += "; first: " + (Elide (@($hits)[0].Subject) 160) }
      return New-StepResult $Index "expectLog" "$desc x$want" "FAIL" $detail
    }
    $min = [int](Prop $Step "minCount" 1)
    if ($hits.Count -ge $min) {
      return New-StepResult $Index "expectLog" $desc "PASS" (Elide (@($hits)[0].Subject) 200)
    }
    return New-StepResult $Index "expectLog" $desc "FAIL" `
      "matched $($hits.Count) line(s) of $($lines.Count) since seq $since, expected at least $min"
  }

  # --- expectNoLog ----------------------------------------------------------
  if (Has-Prop $Step "expectNoLog") {
    $pattern = [string]$Step.expectNoLog
    $scope = [string](Prop $Step "scope" "case")
    $since = switch ($scope) {
      "window" { $WindowSeq.Value }
      "ring" { 0 }
      default { $CaseState.StartSeq }
    }
    $lines = Get-LogLines $since ([string](Prop $Step "filter" ""))
    $hits = @($lines | Where-Object { $_.Subject -match $pattern })
    $desc = "/$pattern/ absent (scope $scope)"
    if ($hits.Count -eq 0) {
      return New-StepResult $Index "expectNoLog" $desc "PASS" "$($lines.Count) line(s) checked"
    }
    return New-StepResult $Index "expectNoLog" $desc "FAIL" `
      ("$($hits.Count) line(s) matched; first: " + (Elide (@($hits)[0].Subject) 200))
  }

  return New-StepResult $Index "unknown" (Elide (($Step | ConvertTo-Json -Compress -Depth 5)) 160) "FAIL" `
    "unrecognised step (expected one of: note, sleepMs, resetWindow, command, op, editFile, expectLog, expectNoLog)"
}

# ---------------------------------------------------------------------------- knownFailing

<#
  A case may declare that it is currently expected to fail for a reason outside itself
  ("knownFailing"), and the clearing condition for that excuse is carried as DATA, not as prose
  ("clearsWhen"), so the runner can tell the operator when the excuse has expired. R68 finding 7:
  a note that is printed only on failure survives the fix silently, and the next real regression
  is then explained away by a paragraph pointing at a commit that is already in.

  clearsWhen is an object; every key present must hold ("and"). Each is evaluated to yes / no /
  unknown, and only an unambiguous "yes" from every key clears the note. Unknown is never
  promoted to yes -- an excuse must be retired on evidence, not on a failed lookup.

    "clearsWhen": {
      "describe":              "a Core generation carrying ec16f76",   // prose for the report
      "coreCommitAtLeast":     "ec16f76",   // that commit is in HEAD's history AND the Core the
                                            // process is RUNNING was built from an image no older
                                            // than it (see Test-CoreCommitAtLeast)
      "coreGenerationAtLeast":  45,         // ping reload.generation
      "coreVersionAtLeast":    "0.5.0",     // ping progenitor
      "logMatches":            "regex",     // at least one ring line matches (scope: ring)
      "logAbsent":             "regex",     // no ring line matches
      "logFilter":             "substring", // passed to the logs op for the two above
      "manual":                "..."        // deliberately unevaluable; always "unknown"
    }

  A note may also say WHICH steps it excuses, as "knownFailingSteps" beside it: a list of step
  indices (1-based, as printed) and/or regexes matched against the step's printed description.
  R69 finding 9: the verdict used to be decided by clearsWhen alone, so a case that failed for a
  completely unrelated reason was reported as the excused one -- which is what happened to the
  config-watch run this rig was measured against, where the Sample plugin was not loaded at all
  and every expectation failed six steps before the ones the note is about, under a heading
  naming a mechanism that was not running. When the field is present and a failing step is not
  in it, the verdict says so ("KNOWN FAILING (plus N unexplained step(s))"). When it is absent
  the note still covers the case, but the failing step numbers are printed either way, so the
  real cause is one line away rather than a re-diagnosis.
#>

function Test-StepIsKnownFailing($StepResult, $KnownFailingSteps) {
  # $null means "the note names no steps", which every caller reads as "covers them all".
  if ($null -eq $KnownFailingSteps) { return $true }
  foreach ($entry in @($KnownFailingSteps)) {
    if ($null -eq $entry) { continue }
    $asInt = $entry -as [int]
    if ($null -ne $asInt) {
      if ([int]$StepResult.Index -eq [int]$asInt) { return $true }
      continue
    }
    $pattern = [string]$entry
    if (-not $pattern) { continue }
    # The description is what the operator saw on the FAIL line, so it is what a note author can
    # copy. A malformed regex must not fail a run that is only reporting: treat it as no match.
    try { if ($StepResult.Description -match $pattern) { return $true } }
    catch { }
  }
  return $false
}

function ConvertTo-UtcTime([string]$text) {
  if ([string]::IsNullOrWhiteSpace($text)) { return $null }
  # DateTimeOffset, not DateTime: both inputs here carry a zone (Bootstrap writes "o", git writes
  # %cI with an offset), and DateTime's RoundtripKind cannot be combined with AdjustToUniversal --
  # the pair throws rather than parsing, which is a runtime error in a helper nothing wraps.
  $parsed = [datetimeoffset]::MinValue
  $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
  if ([datetimeoffset]::TryParse($text, [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
    return $parsed.UtcDateTime
  }
  return $null
}

function Get-CoreLoadedUtc($ping) {
  <#
    When the Core generation the process is running now was loaded, as UTC, or $null.

    Two sources, both from the ping payload, and BOTH are read whenever both are there.

      * A completed swap stamps lastReload.timestamp when the cascade ENDS
        (CoreReloadStatus.Record), with full round-trip precision.
      * uptimeSeconds counts from Progenitor.Plugin.StartedUtc, which is a Core type, so its
        static initializer runs when THIS generation's ALC first touches it -- during Install,
        i.e. before the cascade's tail. It is a per-generation load moment, not a process one.

    So the two do not measure the same instant, and lastReload is the later of them by
    construction. R69 finding 8: this function's docstring promised the earlier one and the body
    returned whichever it found first, which was the later. Now it computes both and returns
    min(), which is the conservative direction: an earlier "loaded" moment can only widen
    Test-CoreCommitAtLeast's "the image on disk is newer than what is running" window, so it can
    make a commit check say "cannot tell" but never "cleared". Anything copied over
    progenitor-core\Progenitor.dll inside the cascade tail is then still outside the running
    generation, which is the truth.
  #>
  if ($null -eq $ping) { return $null }

  $fromReload = $null
  $reload = Prop $ping "reload" $null
  $gen = [int](Prop $reload "generation" 0)
  $last = Prop $reload "lastReload" $null
  if ($null -ne $last) {
    $outcome = [string](Prop $last "outcome" "")
    if ((@("ok", "restored") -contains $outcome) -and ([int](Prop $last "gen" -1) -eq $gen)) {
      $fromReload = ConvertTo-UtcTime ([string](Prop $last "timestamp" ""))
    }
  }

  $fromUptime = $null
  $uptime = [int](Prop $ping "uptimeSeconds" -1)
  if ($uptime -ge 0) { $fromUptime = (Get-Date).ToUniversalTime().AddSeconds(-$uptime) }

  if (($null -ne $fromReload) -and ($null -ne $fromUptime)) {
    if ($fromReload -lt $fromUptime) { return $fromReload }
    return $fromUptime
  }
  if ($null -ne $fromReload) { return $fromReload }
  return $fromUptime
}

function Invoke-Git([string[]]$GitArgs) {
  # Read-only git, and only read-only git: this script never commits, checks out or resets.
  if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
  try {
    $out = & git @GitArgs 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($out | Out-String).Trim() }
  } catch {
    # A native command writing to stderr is an error under ErrorActionPreference=Stop on some
    # hosts. Nothing here is worth aborting a verification run for: report it as a failed lookup,
    # which reads as "unknown" and leaves the knownFailing note in place.
    return [pscustomobject]@{ ExitCode = 1; Text = $_.Exception.Message }
  }
}

function Test-CoreCommitAtLeast([string]$sha, $ping) {
  <#
    "The Core this process is running carries commit <sha>."

    There is no commit id anywhere in the bridge payload, so this is answered from three facts
    that are all checkable, and it answers "unknown" the moment any of them is missing:

      1. <sha> is an ancestor of HEAD in the repository this script lives in -- the tree the
         deploy comes from (the clean worktree is a detached checkout of the same history).
      2. The Core image on disk under -TestRoot was written at or after that commit's date.
      3. The running generation loaded THAT image: the image's write time is at or before the
         moment the live generation was loaded. This is what keeps a staged-but-refused Core --
         the exact state R68 describes, where Progenitor.dll.next has been refused thousands of
         times -- from reading as "the fix is live".
  #>
  $g = Invoke-Git @("-C", $RepoRoot, "rev-parse", "--verify", "--quiet", ($sha + "^{commit}"))
  if ($null -eq $g) { return [pscustomobject]@{ State = "unknown"; Reason = "git is not on PATH" } }
  if ($g.ExitCode -ne 0 -or -not $g.Text) {
    return [pscustomobject]@{ State = "unknown"; Reason = "commit '$sha' is not in $RepoRoot" }
  }
  $full = ($g.Text -split "\s+")[0]

  $anc = Invoke-Git @("-C", $RepoRoot, "merge-base", "--is-ancestor", $full, "HEAD")
  if ($anc.ExitCode -eq 1) {
    return [pscustomobject]@{ State = "no"; Reason = "commit $($full.Substring(0,7)) is not in HEAD's history" }
  }
  if ($anc.ExitCode -ne 0) {
    return [pscustomobject]@{ State = "unknown"; Reason = "git merge-base failed: " + (Elide $anc.Text 120) }
  }

  $dateOut = Invoke-Git @("-C", $RepoRoot, "show", "-s", "--format=%cI", $full)
  $commitUtc = if ($dateOut.ExitCode -eq 0) { ConvertTo-UtcTime $dateOut.Text } else { $null }
  if ($null -eq $commitUtc) {
    return [pscustomobject]@{ State = "unknown"; Reason = "could not read the commit date of $($full.Substring(0,7))" }
  }

  # The single-folder Core, else the three-location one (src/Progenitor.Core/SelfUpdate/InstallLayout.cs).
  $corePath = Join-Path $BepInExDir "plugins\Progenitor\progenitor-core\Progenitor.core"
  if (-not (Test-Path -LiteralPath $corePath)) { $corePath = Join-Path $BepInExDir "progenitor-core\Progenitor.dll" }
  Assert-NotLiveServerPath $corePath
  if (-not (Test-Path -LiteralPath $corePath)) {
    return [pscustomobject]@{ State = "unknown"; Reason = "no Core image at $corePath" }
  }
  $imageUtc = ([System.IO.File]::GetLastWriteTimeUtc($corePath))

  $loadedUtc = Get-CoreLoadedUtc $ping
  if ($null -eq $loadedUtc) {
    return [pscustomobject]@{ State = "unknown"; Reason = "the ping payload does not say when the live Core was loaded" }
  }
  if ($imageUtc -gt $loadedUtc) {
    return [pscustomobject]@{
      State = "unknown"
      Reason = ("the Core image on disk (written " + $imageUtc.ToString("s") + "Z) is newer than the running " +
                "generation (loaded " + $loadedUtc.ToString("s") + "Z), so what is running is not what is on disk")
    }
  }
  if ($imageUtc -lt $commitUtc) {
    return [pscustomobject]@{
      State = "no"
      Reason = ("the running Core image (" + $imageUtc.ToString("s") + "Z) predates commit " +
                $full.Substring(0, 7) + " (" + $commitUtc.ToString("s") + "Z)")
    }
  }
  return [pscustomobject]@{
    State = "yes"
    Reason = ("commit " + $full.Substring(0, 7) + " is in HEAD and the running Core image (" +
              $imageUtc.ToString("s") + "Z) is not older than it")
  }
}

function Test-ClearsWhen($ClearsWhen, $Ping) {
  <#
    yes / no / unknown for the whole condition, plus one line of why. Missing or empty
    clearsWhen is "unknown": a knownFailing note that carries no clearing condition cannot be
    retired automatically, and saying so is the nudge to add one.
  #>
  if ($null -eq $ClearsWhen) {
    return [pscustomobject]@{ State = "unknown"; Reason = "the case declares no clearsWhen condition"; Describe = "" }
  }

  $describe = [string](Prop $ClearsWhen "describe" "")
  $verdicts = @()

  if (Has-Prop $ClearsWhen "coreCommitAtLeast") {
    $sha = [string]$ClearsWhen.coreCommitAtLeast
    $v = Test-CoreCommitAtLeast $sha $Ping
    $verdicts += [pscustomobject]@{ Key = "coreCommitAtLeast=$sha"; State = $v.State; Reason = $v.Reason }
  }

  if (Has-Prop $ClearsWhen "coreGenerationAtLeast") {
    $want = [int]$ClearsWhen.coreGenerationAtLeast
    $have = [int](Prop (Prop $Ping "reload" $null) "generation" -1)
    if ($have -lt 0) {
      $verdicts += [pscustomobject]@{ Key = "coreGenerationAtLeast=$want"; State = "unknown"; Reason = "ping carries no reload.generation" }
    } else {
      $verdicts += [pscustomobject]@{
        Key = "coreGenerationAtLeast=$want"
        State = $(if ($have -ge $want) { "yes" } else { "no" })
        Reason = "the live Core generation is $have"
      }
    }
  }

  if (Has-Prop $ClearsWhen "coreVersionAtLeast") {
    $want = [string]$ClearsWhen.coreVersionAtLeast
    $have = [string](Prop $Ping "progenitor" "")
    $wv = $null; $hv = $null
    if ([version]::TryParse($want, [ref]$wv) -and [version]::TryParse($have, [ref]$hv)) {
      $verdicts += [pscustomobject]@{
        Key = "coreVersionAtLeast=$want"
        State = $(if ($hv -ge $wv) { "yes" } else { "no" })
        Reason = "the side reports version $have"
      }
    } else {
      $verdicts += [pscustomobject]@{ Key = "coreVersionAtLeast=$want"; State = "unknown"; Reason = "'$have' or '$want' is not a version" }
    }
  }

  $logFilter = [string](Prop $ClearsWhen "logFilter" "")
  foreach ($key in @("logMatches", "logAbsent")) {
    if (-not (Has-Prop $ClearsWhen $key)) { continue }
    $pattern = [string]$ClearsWhen.$key
    try {
      $lines = Get-LogLines 0 $logFilter
      $hits = @($lines | Where-Object { $_.Subject -match $pattern })
      $wantHit = ($key -eq "logMatches")
      $got = ($hits.Count -gt 0)
      $verdicts += [pscustomobject]@{
        Key = "$key=/$pattern/"
        State = $(if ($got -eq $wantHit) { "yes" } else { "no" })
        Reason = "$($hits.Count) of $($lines.Count) ring line(s) matched"
      }
    } catch {
      $verdicts += [pscustomobject]@{ Key = "$key=/$pattern/"; State = "unknown"; Reason = "the ring could not be read: " + $_.Exception.Message }
    }
  }

  if (Has-Prop $ClearsWhen "manual") {
    $verdicts += [pscustomobject]@{
      Key = "manual"; State = "unknown"; Reason = [string]$ClearsWhen.manual
    }
  }

  if ($verdicts.Count -eq 0) {
    return [pscustomobject]@{ State = "unknown"; Reason = "clearsWhen names no key this runner can evaluate"; Describe = $describe }
  }

  $state = "yes"
  if (@($verdicts | Where-Object { $_.State -eq "no" }).Count -gt 0) { $state = "no" }
  elseif (@($verdicts | Where-Object { $_.State -eq "unknown" }).Count -gt 0) { $state = "unknown" }
  $reason = (@($verdicts | ForEach-Object { "$($_.Key): $($_.State) ($($_.Reason))" }) -join "; ")
  return [pscustomobject]@{ State = $state; Reason = $reason; Describe = $describe }
}

function Write-KnownFailingVerdict($Case) {
  <#
    One line per case that carries a knownFailing note, and the four outcomes are different
    facts, so they read differently:

      KNOWN FAILING  the case failed and the excuse still applies (or cannot be evaluated).
      KNOWN FAILING (plus N unexplained step(s))
                     the case failed, the excuse still applies to the steps it names, and at
                     least one OTHER step failed too. R69 finding 9: without this the whole
                     failure was reported as the excused one, and a run in which the Sample was
                     not loaded at all was explained to the operator as a stale-ConfigFile defect.
      STALE          the case failed and the clearing condition HOLDS -- this failure is not the
                     known one; re-diagnose it and remove the field.
      STALE (passed) the case passed while carrying the note -- the excuse outlived the defect.

    The failing step numbers are printed under all of them, because the note is prose and the
    step list is evidence.

    The exit code is untouched by all four: a known failure is still a failure, and a stale
    note is not a failure at all.
  #>
  $steps = [string](Prop $Case "FailedSteps" "")
  switch ($Case.KnownFailingVerdict) {
    "known"       { Write-Host ("  KNOWN FAILING: " + $Case.KnownFailing) -ForegroundColor Yellow
                    Write-Host ("    clearsWhen: " + $Case.ClearsWhenState + " -- " + $Case.ClearsWhenReason) -ForegroundColor DarkYellow
                    if ($steps) { Write-Host ("    failing step(s): " + $steps) -ForegroundColor DarkYellow }
                    if (-not [bool](Prop $Case "KnownFailingStepsDeclared" $false)) {
                      Write-Host ("    (the note names no knownFailingSteps, so every failing step above is being " +
                                  "excused by it; add the field to say which ones it really covers.)") -ForegroundColor DarkYellow
                    } }
    "known-plus"  { $extra = @(Prop $Case "UnexplainedSteps" @())
                    Write-Host ("  KNOWN FAILING (plus $($extra.Count) unexplained step(s)): " + $Case.KnownFailing) -ForegroundColor Yellow
                    Write-Host ("    clearsWhen: " + $Case.ClearsWhenState + " -- " + $Case.ClearsWhenReason) -ForegroundColor DarkYellow
                    Write-Host ("    failing step(s): " + $steps) -ForegroundColor DarkYellow
                    Write-Host ("    NOT covered by the note -- diagnose these, the excuse says nothing about them:") -ForegroundColor Magenta
                    foreach ($u in $extra) { Write-Host ("      " + $u) -ForegroundColor Magenta } }
    "stale-fail"  { Write-Host ("  STALE knownFailing note: the clearing condition is satisfied (" +
                                $Case.ClearsWhenReason + "), so this failure is NOT the known one. Re-diagnose it " +
                                "and remove the knownFailing field from the case file.") -ForegroundColor Magenta
                    if ($steps) { Write-Host ("    failing step(s): " + $steps) -ForegroundColor Magenta } }
    "stale-pass"  { Write-Host ("  STALE knownFailing note: " + $Case.Name + " passed while carrying one; remove " +
                                "the field. (clearsWhen: " + $Case.ClearsWhenState + " -- " + $Case.ClearsWhenReason + ")") -ForegroundColor Magenta }
  }
}

# ---------------------------------------------------------------------------- case running

function Get-CaseFiles([string]$name) {
  if (-not (Test-Path -LiteralPath $CaseDir)) { throw "no case folder at $CaseDir" }
  $files = @(Get-ChildItem -LiteralPath $CaseDir -Filter *.json -File)
  if ($name -ne "all") {
    $wanted = $name
    if (-not $wanted.EndsWith(".json")) { $wanted += ".json" }
    $files = @($files | Where-Object { $_.Name -eq $wanted })
    if ($files.Count -eq 0) { throw "no case file '$wanted' in $CaseDir" }
    return $files
  }
  # "all": ordered by the case's own 'order' field, then by name, so a case that pins the plugin
  # ALC (patch-kinds) runs after the ones that reload it.
  $rows = @()
  foreach ($f in $files) {
    $order = 500
    try {
      $j = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
      if (Has-Prop $j "order") { $order = [int]$j.order }
    } catch { }
    $rows += [pscustomobject]@{ File = $f; Order = $order }
  }
  return @($rows | Sort-Object Order, { $_.File.Name } | ForEach-Object { $_.File })
}

function Invoke-Case {
  param([System.IO.FileInfo]$File)

  $case = Get-Content -LiteralPath $File.FullName -Raw | ConvertFrom-Json
  $name = [string](Prop $case "name" ([System.IO.Path]::GetFileNameWithoutExtension($File.Name)))
  $requires = Prop $case "requires" $null
  $needSide = [string](Prop $requires "side" "server")
  $needGhost = [bool](Prop $requires "ghostAdmin" $false)

  Write-Host ""
  Write-Host "== $name" -ForegroundColor Cyan
  if (Has-Prop $case "title") { Write-Host ("   " + $case.title) }
  if (Has-Prop $case "evidence") { Write-Host ("   evidence: " + (@($case.evidence) -join ", ")) }

  if ($needSide -ne $Side) {
    Write-Host "  SKIP this case needs side '$needSide'; this run is '$Side'." -ForegroundColor Yellow
    return [pscustomobject]@{
      Name = $name; Status = "SKIP"; Steps = @(); Failed = 0; Passed = 0; KnownFailing = ""
      KnownFailingVerdict = ""; ClearsWhen = ""; ClearsWhenState = ""; ClearsWhenReason = ""
    }
  }

  $state = [pscustomobject]@{ StartSeq = (Get-RingSeq); GhostIndex = $null }
  if ($needGhost) {
    # The ghost belongs to the RUN, not to this case (R69 finding 1): it was resolved before the
    # first case and it is destroyed, if this run made it, by the run's finally. A case neither
    # creates nor destroys one; it reads the index and says which ghost it is running as.
    if ($null -eq $script:GhostIndex) {
      throw ("$name declares requires.ghostAdmin but no ghost admin was resolved for this run. " +
             "That is a runner defect: Resolve-GhostAdmin runs once before the first case.")
    }
    $state.GhostIndex = $script:GhostIndex
    Write-Host ("   ghost admin userIndex $script:GhostIndex" +
      $(if ($script:GhostHow) { " -- $script:GhostHow" } else { "" }))
  }

  $results = @()
  $windowSeq = $state.StartSeq
  $i = 0
  foreach ($step in @($case.steps)) {
    $i++
    $ref = [ref]$windowSeq
    try {
      $r = Invoke-CaseStep -Step $step -Index $i -WindowSeq $ref -CaseState $state
    } catch {
      $r = New-StepResult $i "error" (Elide (($step | ConvertTo-Json -Compress -Depth 5)) 120) "FAIL" $_.Exception.Message
    }
    $windowSeq = $ref.Value
    $results += $r
    Write-StepLine $r
    if ($r.Status -eq "FAIL" -and [bool](Prop $case "stopOnFail" $false)) {
      Write-Host "  (stopOnFail: skipping the rest of this case)" -ForegroundColor Yellow
      break
    }
  }

  $failed = @($results | Where-Object { $_.Status -eq "FAIL" }).Count
  $passed = @($results | Where-Object { $_.Status -eq "PASS" }).Count
  $status = if ($failed -eq 0) { "PASS" } else { "FAIL" }
  Write-Host ("  -> $name $status ($passed passed, $failed failed, $($results.Count) steps)") `
    -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })

  # A case can declare that it is currently expected to fail for a reason outside itself, and
  # carry the condition that clears the excuse as data (clearsWhen). Three different facts come
  # out of that, and each is reported: the excuse still applies, the excuse has expired on a case
  # that still fails (STALE, and this failure is a new one), or the case passed while carrying
  # the note (STALE too). The exit code is unchanged by all three: a known failure is still a
  # failure, and a stale note is not a failure.
  $failing = @($results | Where-Object { $_.Status -eq "FAIL" })
  $failedList = (@($failing | ForEach-Object { "#$($_.Index) $($_.Kind)" }) -join ", ")

  $knownFailing = [string](Prop $case "knownFailing" "")
  $clears = $null
  $verdict = ""
  $knownSteps = $null
  $unexplained = @()
  if ($knownFailing) {
    # Reporting must not be able to fail a run: a clearsWhen key that throws (git, the ring, a
    # malformed value) reads as "unknown", which keeps the note exactly where it was.
    try { $clears = Test-ClearsWhen (Prop $case "clearsWhen" $null) $script:PingResult }
    catch { $clears = [pscustomobject]@{ State = "unknown"; Reason = "clearsWhen threw: " + $_.Exception.Message; Describe = "" } }
    if (Has-Prop $case "knownFailingSteps") { $knownSteps = @($case.knownFailingSteps) }
    $unexplained = @($failing | Where-Object { -not (Test-StepIsKnownFailing $_ $knownSteps) })
    if ($failed -gt 0) {
      if ($clears.State -eq "yes") { $verdict = "stale-fail" }
      elseif ($unexplained.Count -gt 0) { $verdict = "known-plus" }
      else { $verdict = "known" }
    }
    else { $verdict = "stale-pass" }
  }

  $result = [pscustomobject]@{
    Name = $name; Status = $status; Steps = $results; Failed = $failed; Passed = $passed
    FailedSteps = $failedList
    KnownFailing = $knownFailing
    KnownFailingVerdict = $verdict
    KnownFailingStepsDeclared = ($null -ne $knownSteps)
    UnexplainedSteps = (@($unexplained | ForEach-Object { "#$($_.Index) $($_.Kind): $($_.Description)" }))
    ClearsWhen = [string](Prop $clears "Describe" "")
    ClearsWhenState = [string](Prop $clears "State" "")
    ClearsWhenReason = [string](Prop $clears "Reason" "")
  }
  if ($verdict) { Write-KnownFailingVerdict $result }
  return $result
}

# ---------------------------------------------------------------------------- main

if ($ListCases) {
  Write-Host "cases in $CaseDir"
  foreach ($f in (Get-CaseFiles "all")) {
    $j = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
    $steps = @($j.steps).Count
    Write-Host ("  {0,-22} {1,3} steps  {2}" -f [System.IO.Path]::GetFileNameWithoutExtension($f.Name), $steps, (Prop $j "title" ""))
  }
  exit 0
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
  Write-Error "node is not on PATH; tools/mcp/call.mjs cannot run."
  exit 3
}
if (-not (Test-Path -LiteralPath $CallJs)) {
  Write-Error "missing $CallJs"
  exit 3
}

New-DirIfMissing $OutDir

$startedUtc = (Get-Date).ToUniversalTime()
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$cases = @()
$exit = 0

try {
  $ping = Invoke-Bridge "ping" $null
  if ($ping.ExitCode -ne 0 -or $null -eq $ping.Result) {
    Write-Error "the $Side bridge did not answer ping (exit $($ping.ExitCode)): $($ping.Text)"
    exit 3
  }
  if ([string]$ping.Result.side -ne $Side) {
    Write-Error "the bridge on the '$Side' port answered side='$($ping.Result.side)'."
    exit 3
  }
  # Kept for the clearsWhen evaluation: the live Core generation, the side's version and, with the
  # last swap's timestamp, when the running Core generation was loaded.
  $script:PingResult = $ping.Result
  Write-Host ("bridge: side=$($ping.Result.side) progenitor=$($ping.Result.progenitor) worlds=[" +
    ((@($ping.Result.worlds)) -join ", ") + "] uptime=$($ping.Result.uptimeSeconds)s logSeq=$($ping.Result.logSeq)")
  Write-Host ("test server root: $TestRoot")
  Write-Host ("results/backups:  $script:TempDir")

  $caseFiles = @(Get-CaseFiles $Case)

  # One ghost for the whole run (R69 finding 1). Resolved before the first case, so the reuse
  # branch can only ever see a ghost this run did not create, and destroyed once by the finally
  # below, so the fail-closed sweep is on the path a default `-Case all` actually takes. Cases
  # that need no ghost -- there are none today, and the check is what keeps it that way -- do not
  # pay for one.
  $needAnyGhost = $false
  foreach ($f in $caseFiles) {
    try {
      $j = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
      $req = Prop $j "requires" $null
      if ([string](Prop $req "side" "server") -ne $Side) { continue }
      if ([bool](Prop $req "ghostAdmin" $false)) { $needAnyGhost = $true; break }
    } catch {
      # A case file that will not parse fails loudly in Invoke-Case, which is where the operator
      # can see which one it was. Here it only means "cannot tell whether it needs a ghost", and
      # resolving one we turn out not to need costs a list and a create we then clean up.
      $needAnyGhost = $true
      break
    }
  }
  if ($needAnyGhost) {
    Write-Host ("ghost: " + (Resolve-GhostAdmin))
  }

  foreach ($f in $caseFiles) {
    $cases += (Invoke-Case -File $f)
  }
} catch {
  Write-Host ""
  Write-Error $_.Exception.Message
  $exit = 3
} finally {
  Write-Host ""
  Write-Host "cleanup"
  Restore-EditedFiles
  Remove-GhostIfCreated
}

$sw.Stop()
$failedCases = @($cases | Where-Object { $_.Status -eq "FAIL" })
$skipped = @($cases | Where-Object { $_.Status -eq "SKIP" })
$totalSteps = 0
$totalFailed = 0
foreach ($c in $cases) { $totalSteps += @($c.Steps).Count; $totalFailed += $c.Failed }

Write-Host ""
Write-Host ("summary: {0} case(s), {1} failed, {2} skipped, {3} step(s), {4} step failure(s), {5:n1}s" -f `
  $cases.Count, $failedCases.Count, $skipped.Count, $totalSteps, $totalFailed, $sw.Elapsed.TotalSeconds)
$stale = @($cases | Where-Object { [string](Prop $_ "KnownFailingVerdict" "") -like "stale-*" })
foreach ($c in $cases) {
  Write-Host ("  {0,-6} {1}" -f $c.Status, $c.Name)
  $steps = [string](Prop $c "FailedSteps" "")
  switch ([string](Prop $c "KnownFailingVerdict" "")) {
    "known"      { Write-Host ("         known failing (" + $steps + "): " + $c.KnownFailing) -ForegroundColor Yellow }
    "known-plus" { Write-Host ("         known failing (" + $steps + "), but " +
                               @(Prop $c "UnexplainedSteps" @()).Count + " of those step(s) are NOT what the note " +
                               "excuses: " + ((@(Prop $c "UnexplainedSteps" @())) -join " | ")) -ForegroundColor Magenta }
    "stale-fail" { Write-Host ("         STALE knownFailing note: the clearing condition holds (" +
                               $c.ClearsWhenReason + "); this failure is a new one. Failing step(s): " +
                               $steps) -ForegroundColor Magenta }
    "stale-pass" { Write-Host ("         STALE knownFailing note: the case passed; remove the field from its " +
                               "case file.") -ForegroundColor Magenta }
  }
}
if ($stale.Count -gt 0) {
  # Reported, not enforced: a note that has outlived its defect is a documentation defect, and
  # failing the run for it would make the rig's exit code mean two different things.
  Write-Host ("$($stale.Count) stale knownFailing note(s): " + (@($stale | ForEach-Object { $_.Name }) -join ", ") +
              ". The exit code is unaffected.") -ForegroundColor Magenta
}

if ($exit -eq 0 -and $failedCases.Count -gt 0) { $exit = 1 }

if ($Json) {
  New-DirIfMissing $script:TempDir
  $file = Join-Path $script:TempDir ("verify-" + ($Case -replace "[^A-Za-z0-9_.-]", "_") + ".json")
  $payload = [pscustomobject]@{
    tool        = "tools/verify.ps1"
    side        = $Side
    case        = $Case
    testRoot    = $TestRoot
    startedUtc  = $startedUtc.ToString("o")
    durationMs  = [int]$sw.Elapsed.TotalMilliseconds
    exitCode    = $exit
    cases       = $cases
    stepFailures = $totalFailed
    staleKnownFailing = @($stale | ForEach-Object { $_.Name })
  }
  Write-Utf8NoBom $file ($payload | ConvertTo-Json -Depth 8)
  Write-Host "json: $file"
}

exit $exit

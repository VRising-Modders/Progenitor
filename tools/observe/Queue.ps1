<#
.SYNOPSIS
  The batch queue for tools/observe.ps1: queue a list of the tool's own actions now, drain them
  when the desktop session can actually take them, and leave a durable, self-describing record.

.DESCRIPTION
  Dot-sourced by tools/observe.ps1 into the SCRIPT scope, so everything here sees the parameter
  block, $script:Result, Fail, Finish, Say, Test-SessionLocked and Resolve-Target. It is a
  separate file for the same reason the interop is: a 600-line block bolted onto an already long
  script is undiffable, and this half has no Win32 in it at all.

  THE PROBLEM IT SOLVES

  Evidence 174: with the workstation locked, `shot` keeps returning a real game frame
  (PrintWindow asks the window to render; a locked session stops compositing without stopping the
  swapchain) while every input subcommand refuses with exit 3 and no override. That is the
  correct policy and it does not change here. What it cost, twice on 2026-09-04, is a
  verification run that reached an input step, found the session locked, and simply stopped --
  leaving the check owed and nobody able to finish it until a human came back and unlocked.

  The queue is the answer to that and to nothing else: the run records what it wanted to send,
  and a drain sends it when the session can take it. Nothing waits on a human being present.

  THE SECURITY BOUNDARY

  There is no mechanism here, and there must never be one, for delivering input to a locked
  session. No desktop switching, no message synthesis, no unlocking, no credentials. The refusal
  in Enter-InputMode (exit 3, no override) is still the only answer while locked, and the drain
  reaches it through exactly the same code path a hand-typed command does -- it spawns the tool.
  A queued input batch WAITS for the lock to clear. It never works around it.

  That refusal costs nothing real: Unity's Input System reads raw input, which Windows delivers
  to the foreground window on the ACTIVE desktop. Input synthesized at a locked session would not
  drive the game even if the tool were willing to try.

  THE SPLIT, WHICH IS THE WHOLE POINT

  Reading the screen and writing input have different safety requirements, so they must not share
  a gate (evidence 174's rule). A batch whose steps are ALL non-input -- `shot` and `wait` -- is a
  capture-only batch and runs IMMEDIATELY, regardless of the lock, both at queue time and every
  time a drain looks at the queue. Only a batch that contains a real input step waits.

  The split is per BATCH, not per step, and that is deliberate. "press F10, then screenshot" is
  one question; running the screenshot half early would answer a different one. A batch is
  ordered and it is atomic in intent, so it moves as a unit.

  HOW A STEP IS EXECUTED

  By spawning tools/observe.ps1 as a child process with -Json and reading its exit code. Not by
  calling the Invoke-* functions in-process. That costs about a second of Add-Type per step and
  buys the thing worth having: a queued step goes through byte-for-byte the same gates, refusals,
  blank checks and JSON shape as the same command typed by hand. There is no second
  implementation of the input path to keep in step with the first, and no way for the queue to
  become a hole in a gate.

  STALENESS

  A batch records the identity of the process it was aimed at -- pid AND process start time,
  because Windows reuses pids -- plus a wall-clock expiry. Before any step runs, the drain
  re-resolves the target and compares. A client that has restarted since the batch was queued is
  a DIFFERENT WORLD: the character is somewhere else, the panels are closed, the mod may be a
  different build. Firing the batch into it would produce a confident, wrong answer, which is
  worse than no answer. It is refused with exit 8 and the reason is written down.

.NOTES
  Windows PowerShell 5.1. No C# here.
#>

# ---------------------------------------------------------------------------------------------
# Layout on disk. One directory, everything in it, nothing to be told twice about.
#
#   <QueueRoot>\batches\<batchId>.json      the batch AND its result. One file, one truth.
#   <QueueRoot>\shots\<batchId>\NN-label.png  captures, unless the step named its own -Out
#   <QueueRoot>\logs\<batchId>.clientlog.txt  the client log lines the batch spanned
#   <QueueRoot>\latest.json                 a copy of the most recently finished batch file
#   <QueueRoot>\drain.log                   append-only narration of every drain
#   <QueueRoot>\drain.lock                  held open exclusively by the live drain
#   <QueueRoot>\stop-drain                  create this file to stop a waiting drain
# ---------------------------------------------------------------------------------------------

$script:QueueSchema = 1
$script:QueueInputActions = @("key", "type", "move", "click", "scroll")
$script:QueueStepActions = @("key", "type", "move", "click", "scroll", "shot", "wait")

# Which arguments each step action accepts. An unknown one is a typo, and a typo that is silently
# dropped is a batch that does something other than what it says -- so it is a queue-time error,
# reported before anything is written, rather than a surprise hours later at drain time.
$script:QueueStepArgs = @{
  "key"    = @("value", "repeatcount", "holdms", "gapms", "settlems", "delayms", "nofocus")
  "type"   = @("value", "settlems", "delayms", "nofocus")
  "move"   = @("x", "y", "dx", "dy", "screencoords", "settlems", "delayms", "nofocus")
  "click"  = @("x", "y", "button", "clicks", "screencoords", "settlems", "delayms", "nofocus")
  "scroll" = @("delta", "horizontal", "x", "y", "screencoords", "settlems", "delayms", "nofocus")
  "shot"   = @("out", "region", "method", "minstddev", "maxdominantshare", "allowoccluded", "allowlockedsession", "noblankcheck")
  "wait"   = @("ms")
}

# Arguments that are switches on observe.ps1 (present or absent, never -Name value).
$script:QueueSwitchArgs = @("screencoords", "nofocus", "horizontal", "allowoccluded", "allowlockedsession", "noblankcheck")

# name-in-a-step -> -ParameterName on observe.ps1. Only where they differ in spelling.
$script:QueueArgParam = @{
  "value" = "Value"; "repeatcount" = "RepeatCount"; "holdms" = "HoldMs"; "gapms" = "GapMs"
  "settlems" = "SettleMs"; "delayms" = "DelayMs"; "nofocus" = "NoFocus"
  "x" = "X"; "y" = "Y"; "dx" = "Dx"; "dy" = "Dy"; "screencoords" = "ScreenCoords"
  "button" = "Button"; "clicks" = "Clicks"; "delta" = "Delta"; "horizontal" = "Horizontal"
  "out" = "Out"; "region" = "Region"; "method" = "Method"; "minstddev" = "MinStdDev"
  "maxdominantshare" = "MaxDominantShare"; "allowoccluded" = "AllowOccluded"
  "allowlockedsession" = "AllowLockedSession"; "noblankcheck" = "NoBlankCheck"
}

# ---------------------------------------------------------------------------------------------
# Paths.
# ---------------------------------------------------------------------------------------------

function Get-QueuePaths {
  # The queue root is a caller-supplied path, so it goes through the fence like any other root
  # before a single directory is created under it.
  try { Assert-NotLiveServerPath $QueueRoot }
  catch { Fail $_.Exception.Message 3 }

  $root = [System.IO.Path]::GetFullPath($QueueRoot)
  $p = [pscustomobject]@{
    Root     = $root
    Batches  = Join-Path $root "batches"
    Shots    = Join-Path $root "shots"
    Logs     = Join-Path $root "logs"
    Latest   = Join-Path $root "latest.json"
    DrainLog = Join-Path $root "drain.log"
    Lock     = Join-Path $root "drain.lock"
    Stop     = Join-Path $root "stop-drain"
  }
  foreach ($d in @($p.Root, $p.Batches, $p.Shots, $p.Logs)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
  }
  return $p
}

function Get-BatchPath { param($Paths, [string]$BatchIdValue) return (Join-Path $Paths.Batches "$BatchIdValue.json") }

function New-QueueBatchId {
  return "b-" + (Get-Date -Format "yyyyMMdd-HHmmss-fff") + "-" + ([Guid]::NewGuid().ToString("N").Substring(0, 4))
}

function ConvertTo-SafeFileToken {
  param([string]$Text)
  $t = ($Text -replace '[^A-Za-z0-9._-]', '-')
  if ($t.Length -gt 48) { $t = $t.Substring(0, 48) }
  if ($t -eq "") { $t = "step" }
  return $t
}

# Write a JSON file the way a file that must survive an interrupted drain has to be written:
# fully, to a temp name, then moved over the target. A half-written batch file is a batch whose
# state cannot be read, and the whole point of this thing is that the record outlives the run.
#
# The temp name carries the writer's pid (R45 finding 24). It used to be a fixed "$Path.tmp", so
# two processes writing the same batch file collided on the WriteAllText or the Move-Item and,
# with $ErrorActionPreference = "Stop", the loser threw an unhandled IOException -- which, when
# the loser was the drain, killed it mid-queue. The exclusive-lock rule below now stops two
# writers from being there at all; this stops the collision being fatal if one ever is.
function Get-JsonTempPath {
  param([string]$Path)
  return "$Path.$PID.tmp"
}

function Save-JsonFile {
  param([string]$Path, $Object)
  $tmp = Get-JsonTempPath $Path
  $json = ConvertTo-Json -InputObject $Object -Depth 12
  [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
  Move-Item -LiteralPath $tmp -Destination $Path -Force
}

# Set a property on a batch object whether or not it already has one. A batch read back from disk
# was shaped by whatever schema wrote it, and `$b.newField = x` on a PSCustomObject that has no
# such property is a terminating error -- which, inside a drain, is one more way to lose a queue.
function Add-BatchProperty {
  param($Object, [string]$Name, $Value)
  if ($null -eq $Object) { return }
  if ($Object.PSObject.Properties[$Name]) { $Object.PSObject.Properties[$Name].Value = $Value }
  else { Add-Member -InputObject $Object -MemberType NoteProperty -Name $Name -Value $Value -Force }
}

function Read-JsonFile {
  param([string]$Path)
  try { return (Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json) }
  catch { return $null }
}

function Write-DrainLog {
  param($Paths, [string]$Line)
  $stamped = "{0}  [pid {1}]  {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"), $PID, $Line
  try { Add-Content -LiteralPath $Paths.DrainLog -Value $stamped -Encoding UTF8 } catch {}
}

# ---------------------------------------------------------------------------------------------
# The queue-wide execution lock.
#
# One process at a time may EXECUTE batches out of a queue root. That used to be true of `drain`
# alone; `queue`'s inline capture-only run took no lock at all, so a live drain could pick the
# same file up as `pending` and run it a second time (R45 finding 24). Both take it now, through
# Open-ExecutionLock, and the file records which of the two is holding it.
#
# The lock is an exclusively-opened file held for the holder's lifetime, so a holder that dies
# releases it the moment the OS closes its handle. There is no stale lock to clean up and no
# timestamp heuristic to get wrong.
# ---------------------------------------------------------------------------------------------

# FileShare.Read, not FileShare.None, and that is a fix rather than a relaxation (R45 finding 25,
# third defect, which turned out to be worse than reported). Exclusion is unchanged: the holder
# takes WRITE access and grants only Read, so any second attempt to open for writing -- which is
# what another holder does -- fails with a sharing violation. What changes is that the name in the
# file can now be READ while it is held. Under FileShare.None nobody could open it at all, so the
# old status command's `Get-Content` in its catch block could only ever succeed when the lock was
# NOT held -- meaning the holder line it printed was, without exception, the stale stamp of a
# process that had already exited.
function Open-ExecutionLock {
  param($Paths, [string]$Stamp)
  $stream = $null
  try {
    $stream = [System.IO.File]::Open($Paths.Lock, [System.IO.FileMode]::OpenOrCreate,
                                     [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
  }
  catch { return $null }
  try {
    $stream.SetLength(0)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Stamp)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
  }
  catch {}
  return $stream
}

# Truncate before releasing, so the text in the file names a LIVE holder and nothing else. A
# holder that is killed cannot do this, which is why the state below never reports a holder for a
# lock it was able to open (R45 finding 25: `RUNNING -- drain pid 12345 started ...` was printed
# for a process that had exited hours earlier).
function Close-ExecutionLock {
  param($Stream)
  if ($null -eq $Stream) { return }
  try { $Stream.SetLength(0); $Stream.Flush() } catch {}
  try { $Stream.Dispose() } catch {}
}

# What the lock file can be made to say, WITHOUT writing to it and without excluding anybody.
#
# Three separate defects lived in the five lines this replaces (R45 finding 25). The old probe
# opened OpenOrCreate/FileShare.None, so merely ASKING whether a drain was running created a
# zero-byte drain.lock and held it exclusively for the length of the open -- and a drain starting
# in that instant refused with exit 7 and the sentence "a drain is already running", a positive
# claim about a state that was never true. It then reported RUNNING for ANY failure to open: an
# ACL denial, an antivirus handle and a locked parent directory all printed `drain RUNNING`.
#
# So: FileMode.Open (never creates), the holder's name read through a share mode that excludes
# nobody, and a verdict that distinguishes what was actually measured:
#
#   absent      the file is not there -- nothing has ever executed in this queue root
#   idle        a writer's open succeeded, so nobody is holding it
#   running     that open failed with a SHARING violation, which is what a live holder produces
#   unreadable  it failed some other way; the cause is quoted, never diagnosed as a drain
#
# One residual window is real and is stated rather than hidden: the "is it held" test has to ask
# for the same access a holder asks for, so while its handle is open a holder starting in that
# instant would fail. It is microseconds, against the old code's identical window PLUS a file
# creation, and no caller polls it in a tight loop any more (QueueSelfTest.ps1 did).
function Get-ExecutionLockState {
  param($Paths)
  $result = [pscustomobject]@{ State = "unreadable"; Holder = ""; Detail = "" }
  if (-not (Test-Path -LiteralPath $Paths.Lock)) {
    $result.State = "absent"
    $result.Detail = "no lock file at $($Paths.Lock): nothing has executed a batch in this queue root"
    return $result
  }

  # Who says it owns the lock. Read-only with the most permissive share there is, so it succeeds
  # whether or not a holder is there and never excludes one. This text is only ever REPORTED for
  # the running verdict below -- an idle lock is truncated by whoever released it, and a stale
  # name in a lock nobody holds is exactly what finding 25's third defect printed.
  $holder = ""
  $rs = $null
  try {
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $rs = [System.IO.File]::Open($Paths.Lock, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    $sr = New-Object System.IO.StreamReader($rs)
    try { $holder = [string]$sr.ReadToEnd() } finally { $sr.Dispose() }
    $holder = $holder.Trim()
  }
  catch { $holder = "" }
  finally { if ($rs) { try { $rs.Dispose() } catch {} } }

  # Is anybody holding it: ask for exactly what a holder asks for.
  $fs = $null
  try {
    $fs = [System.IO.File]::Open($Paths.Lock, [System.IO.FileMode]::Open,
                                 [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
  }
  catch [System.IO.FileNotFoundException] {
    $result.State = "absent"
    $result.Detail = "the lock file went away between the existence check and the open"
    return $result
  }
  catch [System.IO.DirectoryNotFoundException] {
    $result.State = "absent"
    $result.Detail = "the queue root went away between the existence check and the open"
    return $result
  }
  catch [System.IO.IOException] {
    # ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33) are what a live holder produces.
    # Any other IOException is not evidence of a holder and is not reported as one.
    $code = 0
    try { $code = ($_.Exception.HResult -band 0xFFFF) } catch {}
    if ($code -eq 32 -or $code -eq 33) {
      $result.State = "running"
      $result.Detail = "the lock is held (sharing violation when asking for the access a holder asks for)"
      $result.Holder = $holder
      return $result
    }
    $result.State = "unreadable"
    $result.Detail = "could not test the lock: $($_.Exception.Message)"
    return $result
  }
  catch {
    $result.State = "unreadable"
    $result.Detail = "could not test the lock: $($_.Exception.Message)"
    return $result
  }
  try { $fs.Dispose() } catch {}
  $result.State = "idle"
  $result.Detail = "the lock opened for writing, so no process is holding it"
  return $result
}

# ---------------------------------------------------------------------------------------------
# CONSEQUENCE SENTENCES: composed from the observation, never at the call site.
#
# THE SHAPE THAT KEEPS COMING BACK IN THIS FILE. Three consecutive reviews have found the same
# defect here and nowhere else in the repository (R44 findings 14-20, R45 findings 9/11/12/24/25,
# R46 findings 3 and 6): a measurement is made carefully, given three honest states including
# "could not tell" -- and then, one or two lines later, a sentence is written that states the
# INTENDED case as though the measurement had returned it. Get-ExecutionLockState was rewritten
# to stop claiming RUNNING for any failure to open; the very next function said "a drain will
# pick this up" on the branch where it had just established that it could not tell what holds the
# lock. The `finally` that records an aborted batch named "a gate refused mid-batch" for an
# unwind it cannot inspect, on a tool whose documented interrupt is Ctrl-C.
#
# Individually-correct patches do not stop this, because the defect is not in the measuring
# function: it is in the DISTANCE between the measurement and the sentence. So the sentences move
# here, next to the measurement, behind functions with two properties:
#
#   1. They cannot be called without the evidence. Get-ExecutionLockConsequence takes the state
#      object Get-ExecutionLockState returned; Get-BatchAbortRecord takes the two things the
#      unwind path actually observed. There is no argument for "what I think happened".
#   2. Every value the evidence can take has a branch, and the branch for "not established" is
#      the DEFAULT, so a state added later gets the honest sentence rather than the optimistic
#      one that happens to be first.
#
# The call sites then print `$c.Cause` and `$c.Advice` and decide nothing. That is enforceable,
# and it is enforced: the offline suite pins that the distinctive clauses of these sentences
# appear in this file exactly once each, inside these functions.
#
# What this does NOT do is make an unevidenced claim unrepresentable -- PowerShell 5.1 has no
# type that could. Someone can still write a sentence by hand at a call site. The source pin is
# what makes that visible on the next run instead of on the next review.
# ---------------------------------------------------------------------------------------------

# The lock could not be taken. What is known about why, and -- the half that keeps going wrong --
# whether anything was established that will run this batch without a human (R46 finding 6).
#
# RunsUnattended is $true only for the `running` verdict, which is the only one that measured a
# holder: a sharing violation when asking for exactly the access a holder asks for. Every other
# verdict, including "the lock read back idle a millisecond after refusing to open" (a real race:
# the holder released in between), leaves the batch pending with nobody established to run it, so
# the operator is told to start a drain rather than told to do nothing.
function Get-ExecutionLockConsequence {
  param($LockState, [string]$LockPath)
  $out = [pscustomobject]@{
    State = "unreadable"; Cause = ""; RunsUnattended = $false; Advice = ""; Holder = ""
  }
  if ($null -ne $LockState) {
    $out.State = [string]$LockState.State
    $out.Holder = [string]$LockState.Holder
  }
  $detail = ""
  if ($null -ne $LockState) { $detail = [string]$LockState.Detail }

  switch ($out.State) {
    "running" {
      $out.Cause = "a drain is running and holds $LockPath"
      if ($out.Holder) { $out.Cause = $out.Cause + " -- " + $out.Holder }
      $out.RunsUnattended = $true
      # The only place this claim is made. A drain takes capture-only batches on every pass,
      # including while it is waiting on an input batch, so a live drain really will take it.
      $out.Advice = "It is left PENDING, and the drain that holds the lock will run it without anyone doing anything."
      break
    }
    default {
      $out.Cause = "the execution lock $LockPath could not be taken, and it was NOT established that anything is holding it ($detail)"
      $out.RunsUnattended = $false
      $out.Advice = ("It is left PENDING. Nothing was established to be draining this queue, so it will sit there until a drain runs: " +
                     "powershell -NoProfile -ExecutionPolicy Bypass -File tools\observe.ps1 drain")
      break
    }
  }
  return $out
}

# A batch whose process left from inside it. What is KNOWN about that, which is less than the
# `finally` used to say (R46 finding 3).
#
# The two observations, and they are the only two there are:
#   * $Refusal -- $script:LastRefusal, which Fail sets on its way out and Invoke-QueueBatchGuarded
#     clears before each batch. Set means a gate inside this batch refused and named itself; that
#     is the one case where "a gate refused mid-batch" is a measurement. It is currently
#     unreachable (no Fail remains inside Invoke-QueueBatch's call graph) and is kept because the
#     next Fail added there must not silently produce the wrong sentence.
#   * $StepsAttempted -- $script:BatchStepsAttempted, set on the last line before the step loop.
#     False means no step was ever started, which is a real and useful thing to record: nothing
#     was sent. True means the send is genuinely indeterminate.
#
# Everything else about the unwind is unknown here and is written down as unknown. `finally` runs
# for an `exit`, for a pipeline stop and for Ctrl-C -- which docs/observe-tool.md advertises as the
# way to interrupt a drain -- and gives no way to tell them apart. MEASURED on this host, PS 5.1:
# a `finally` runs when a nested function calls `exit` (881d494) and when the pipeline is stopped
# from downstream. Ctrl-C was NOT measured, deliberately: raising a console break here means
# CTRL_C_EVENT against a process group on a machine the user is playing on. It is covered by
# naming it as one of three possibilities rather than by picking one.
function Get-BatchAbortRecord {
  param([bool]$StepsAttempted, $Refusal)
  if ($Refusal -and $Refusal.Message) {
    $cause = "a gate inside this batch refused and ended the whole run (observe exit $($Refusal.Code)): $($Refusal.Message)"
  }
  else {
    $cause = "the process running this batch stopped from inside it -- an exit, a Ctrl-C or a pipeline stop; which of those it was is NOT established"
  }
  if ($StepsAttempted) {
    $sent = "at least one step had been started, so how much of the batch was sent is not known"
  }
  else {
    $sent = "no step had been started, so nothing was sent"
  }
  return [pscustomobject]@{
    Cause          = $cause
    StepsAttempted = $StepsAttempted
    Refused        = [bool]($Refusal -and $Refusal.Message)
    Reason         = "$cause. $sent. The batch is NOT retried."
    LogLine        = "$cause ($sent)"
  }
}

# ---------------------------------------------------------------------------------------------
# Parsing a batch out of -Steps / -StepsFile.
#
# Two accepted spellings, auto-detected. A string starting with '[' is JSON. Anything else is the
# compact form, which exists so the common case is one readable command line:
#
#   -Steps 'key F10; shot; key Escape; shot'
#
# Compact grammar: steps separated by ';'. Each step is an action, then either a single bare
# positional argument (the chord for `key`, the text for `type`, the milliseconds for `wait`) or
# any number of name=value pairs. Double quotes group, and are stripped.
# ---------------------------------------------------------------------------------------------

function Split-QueueText {
  param([string]$Text, [char[]]$Separators)
  $out = New-Object System.Collections.ArrayList
  $sb = New-Object System.Text.StringBuilder
  $inQuote = $false
  $sawQuote = $false
  for ($i = 0; $i -lt $Text.Length; $i++) {
    $c = $Text[$i]
    if ($c -eq '"') { $inQuote = -not $inQuote; $sawQuote = $true; continue }
    if ((-not $inQuote) -and ($Separators -contains $c)) {
      [void]$out.Add([pscustomobject]@{ Text = $sb.ToString(); Quoted = $sawQuote })
      $sb = New-Object System.Text.StringBuilder; $sawQuote = $false; continue
    }
    [void]$sb.Append($c)
  }
  [void]$out.Add([pscustomobject]@{ Text = $sb.ToString(); Quoted = $sawQuote })
  if ($inQuote) { Fail "unbalanced double quote in -Steps" 1 }
  # A token that was quoted keeps its spaces: `type " hello "` means what it says.
  $result = New-Object System.Collections.ArrayList
  foreach ($tok in $out) {
    $t = $tok.Text
    if (-not $tok.Quoted) { $t = $t.Trim() }
    if ($t -ne "" -or $tok.Quoted) { [void]$result.Add($t) }
  }
  return @($result)
}

function ConvertFrom-CompactSteps {
  param([string]$Text)
  $steps = New-Object System.Collections.ArrayList
  $segments = @(Split-QueueText -Text $Text -Separators @(';'))
  foreach ($seg in $segments) {
    if ($seg.Trim() -eq "") { continue }
    $tokens = @(Split-QueueText -Text $seg -Separators @(' ', "`t"))
    if ($tokens.Count -eq 0) { continue }
    $action = $tokens[0].ToLowerInvariant()
    # Check the action before the arguments. Otherwise a misspelled action produces a complaint
    # about its arguments ("'kye' has no bare argument"), which sends the reader looking in the
    # wrong place for a one-character typo.
    if ($script:QueueStepActions -notcontains $action) {
      Fail "step '$seg': unknown action '$action'. Steps may be: $($script:QueueStepActions -join ', ')." 1
    }
    $args = @{}
    $label = $null
    for ($i = 1; $i -lt $tokens.Count; $i++) {
      $tok = $tokens[$i]
      $eq = $tok.IndexOf('=')
      if ($eq -gt 0) {
        $name = $tok.Substring(0, $eq).ToLowerInvariant()
        $val = $tok.Substring($eq + 1)
        if ($name -eq "label") { $label = $val } else { $args[$name] = $val }
      }
      else {
        # A bare argument is the one thing this action obviously takes.
        if ($i -ne 1) { Fail "step '$seg': '$tok' is not name=value. Only one bare argument is allowed, and it must come first." 1 }
        switch ($action) {
          "key"  { $args["value"] = $tok }
          "type" { $args["value"] = $tok }
          "wait" { $args["ms"] = $tok }
          default { Fail "step '$seg': '$action' has no bare argument; use name=value (e.g. 'click x=820 y=512')." 1 }
        }
      }
    }
    [void]$steps.Add([pscustomobject]@{ action = $action; label = $label; args = ([pscustomobject]$args) })
  }
  return @($steps)
}

function ConvertFrom-JsonSteps {
  param([string]$Text)
  $parsed = $null
  try { $parsed = $Text | ConvertFrom-Json }
  catch { Fail "-Steps looked like JSON (it starts with '[') but did not parse: $($_.Exception.Message)" 1 }
  $steps = New-Object System.Collections.ArrayList
  foreach ($s in @($parsed)) {
    $action = $null
    if ($s.PSObject.Properties["action"]) { $action = [string]$s.action }
    if (-not $action) { Fail "every JSON step needs an 'action' field" 1 }
    $label = $null
    if ($s.PSObject.Properties["label"]) { $label = [string]$s.label }
    $args = @{}
    if ($s.PSObject.Properties["args"] -and $null -ne $s.args) {
      foreach ($p in $s.args.PSObject.Properties) { $args[$p.Name.ToLowerInvariant()] = [string]$p.Value }
    }
    # Also accept args written flat alongside the action, which is what people type first.
    foreach ($p in $s.PSObject.Properties) {
      if ($p.Name -in @("action", "label", "args")) { continue }
      $args[$p.Name.ToLowerInvariant()] = [string]$p.Value
    }
    [void]$steps.Add([pscustomobject]@{ action = $action.ToLowerInvariant(); label = $label; args = ([pscustomobject]$args) })
  }
  return @($steps)
}

function Get-StepArgValue {
  param($Step, [string]$Name)
  if ($null -eq $Step.args) { return $null }
  $p = $Step.args.PSObject.Properties[$Name]
  if ($null -eq $p) { return $null }
  return $p.Value
}

function Test-StepArgPresent {
  param($Step, [string]$Name)
  if ($null -eq $Step.args) { return $false }
  return ($null -ne $Step.args.PSObject.Properties[$Name])
}

# Validate and finalize: number the steps, give every one a label, reject unknown actions and
# unknown arguments, apply the child-process quoting rule, and fence every output path. All of it
# at QUEUE time, so a malformed batch is a message now rather than a failure at 3 a.m.
function Initialize-QueueSteps {
  param($Steps, $Paths, [string]$BatchIdValue)
  if ($Steps.Count -eq 0) { Fail "the batch has no steps. Example: -Steps 'key F10; shot; key Escape; shot'" 1 }
  $index = 0
  $out = New-Object System.Collections.ArrayList
  foreach ($s in $Steps) {
    $index++
    if ($script:QueueStepActions -notcontains $s.action) {
      Fail "step ${index}: unknown action '$($s.action)'. Steps may be: $($script:QueueStepActions -join ', ')." 1
    }
    $allowed = $script:QueueStepArgs[$s.action]
    foreach ($p in $s.args.PSObject.Properties) {
      if ($allowed -notcontains $p.Name) {
        Fail "step $index ($($s.action)): unknown argument '$($p.Name)'. '$($s.action)' takes: $($allowed -join ', ')." 1
      }
      # Every step argument is spliced into a child process command line. A double quote there
      # needs Windows' backslash-before-quote escaping, which is the classic place a quoting bug
      # hides. There is no chord, key name or path that needs one, so it is refused outright
      # rather than escaped almost-correctly.
      if ([string]$p.Value -match '"') {
        Fail "step $index ($($s.action)): argument '$($p.Name)' contains a double quote. Step arguments are passed to a child process command line and quotes are refused rather than escaped. Remove it." 1
      }
    }
    $label = $s.label
    if (-not $label) { $label = "{0:D2}-{1}" -f $index, $s.action }
    $label = ConvertTo-SafeFileToken $label

    switch ($s.action) {
      "key"  { if (-not (Get-StepArgValue $s "value")) { Fail "step $index (key): needs a chord, e.g. 'key F10' or 'key value=Ctrl+F9'." 1 } }
      "type" { if (-not (Test-StepArgPresent $s "value")) { Fail "step $index (type): needs text, e.g. 'type `".debug on`"'." 1 } }
      "wait" {
        $ms = Get-StepArgValue $s "ms"
        $n = 0
        if (-not [int]::TryParse([string]$ms, [ref]$n) -or $n -lt 0 -or $n -gt 600000) {
          Fail "step $index (wait): 'ms' must be a whole number of milliseconds between 0 and 600000 (got '$ms')." 1
        }
      }
      "scroll" {
        $d = Get-StepArgValue $s "delta"
        $n = 0
        if (-not [int]::TryParse([string]$d, [ref]$n) -or $n -eq 0) { Fail "step $index (scroll): needs a non-zero 'delta'." 1 }
      }
    }

    # Where a shot lands. A step that names its own -Out is fenced here; one that does not gets a
    # path under the batch's own shots directory, named after the step, so a result file and the
    # picture it describes cannot drift apart.
    $args = @{}
    foreach ($p in $s.args.PSObject.Properties) { $args[$p.Name] = [string]$p.Value }
    if ($s.action -eq "shot") {
      if ($args.ContainsKey("out") -and $args["out"]) {
        try { Assert-NotLiveServerPath $args["out"] } catch { Fail $_.Exception.Message 3 }
        $args["out"] = [System.IO.Path]::GetFullPath($args["out"])
      }
      else {
        $args["out"] = Join-Path (Join-Path $Paths.Shots $BatchIdValue) ("{0}.png" -f $label)
      }
    }

    [void]$out.Add([pscustomobject]@{
      index  = $index
      label  = $label
      action = $s.action
      args   = ([pscustomobject]$args)
    })
  }
  return @($out)
}

# Does this batch have to wait for a usable session?
#
# `needsInput` describes the STEPS and never changes. This describes the RUN, and it is the
# narrower question, because a -DryRun batch contains input steps but sends none of them:
# Enter-InputMode returns before the lock check, the focus grab and the SendInput call. Holding a
# rehearsal back until someone unlocks the machine would protect nothing and would make the one
# safe way to test an input batch against the live client unavailable exactly when it is wanted.
function Test-BatchMustWait {
  param($Batch)
  if ($Batch.dryRun) { return $false }
  return [bool]$Batch.needsInput
}

# ---------------------------------------------------------------------------------------------
# Target identity. pid alone is not identity: Windows reuses pids, and a client that crashed and
# was relaunched can land on the same number. Start time is what makes the pair an identity.
# ---------------------------------------------------------------------------------------------

# The fallible half of the identity, isolated in a scriptblock for the same reason
# $script:LockProbe is (observe.ps1, R44 finding 15): so `selftest -Offline` can substitute one
# that cannot answer and PROVE this guard fails closed, without needing an elevated process on the
# machine to point it at.
#
# Contract: return the process start time as a round-trip string, or throw / return nothing for
# "I cannot tell". It must never invent a value and it must never turn a failure into $null and
# leave the caller to guess -- Get-TargetIdentity and Get-BatchStaleReason are what turn "cannot
# tell" into a refusal, and they can only do that if this half reports the failure honestly.
#
# Process.StartTime throws Win32Exception for a process the caller cannot open with
# PROCESS_QUERY_INFORMATION -- an elevated target, or one owned by another user.
$script:ProcessStartProbe = {
  param([int]$ProcessId)
  return (Get-Process -Id $ProcessId -ErrorAction Stop).StartTime.ToString("o")
}

# Returns @{ Ok = $bool; StartTime = <string>; Error = <string> }. Ok is false when the probe
# could not answer, and Error then says why in a sentence a refusal message can quote.
function Read-ProcessStartTime {
  param([int]$ProcessId)
  $value = $null
  try { $value = & $script:ProcessStartProbe $ProcessId }
  catch { return [pscustomobject]@{ Ok = $false; StartTime = $null; Error = $_.Exception.Message } }
  if ($null -eq $value -or [string]$value -eq "") {
    return [pscustomobject]@{ Ok = $false; StartTime = $null; Error = "the start-time probe returned no answer" }
  }
  return [pscustomobject]@{ Ok = $true; StartTime = [string]$value; Error = $null }
}

function Get-TargetIdentity {
  param($Target)
  # R45 finding 12: this used to be `try { ... } catch {}` and write startTime = null on failure,
  # which made the whole staleness check below silently vanish -- the batch carried NO pid-reuse
  # protection at all and nothing said so. The failure is recorded now, and every reader treats an
  # unrecorded start time as "identity not established", never as "fine".
  $start = Read-ProcessStartTime ([int]$Target.Pid)
  return [pscustomobject]@{
    spec                  = $Window
    pid                   = $Target.Pid
    processName           = $Target.ProcessName
    processPath           = $Target.ProcessPath
    startTime             = $start.StartTime
    startTimeUnavailable  = (-not $start.Ok)
    startTimeError        = $start.Error
    isGame                = $Target.IsGame
    hwndAtQueue           = ("0x{0:X}" -f [int64]$Target.Hwnd)
    title                 = $Target.Title
  }
}

# Returns $null if the batch's world is still there, or a sentence saying why it is not -- or why
# it could not be established that it is, which is the same answer for this guard's purposes.
#
# THIS GUARD FAILS CLOSED (R45 finding 12). Both halves used to swallow their failure and fall
# through to "not stale": an unreadable start time at queue time wrote null and skipped the whole
# comparison, and an unreadable one at drain time skipped it too. Either way the batch fired into
# a possibly-different world, which is the single failure mode this section exists to prevent, and
# nothing in the output said the check had not run. "I could not read it" is not "it is fine".
function Get-BatchStaleReason {
  param($Batch)
  $id = $Batch.target
  $proc = $null
  try { $proc = Get-Process -Id ([int]$id.pid) -ErrorAction Stop } catch {}
  if ($null -eq $proc) {
    return "the target process (pid $($id.pid), $($id.processName)) has exited since this batch was queued"
  }

  $queuedStart = [string]$id.startTime
  if (-not $queuedStart) {
    $why = "unknown reason"
    try { if ($id.PSObject.Properties["startTimeError"] -and $id.startTimeError) { $why = [string]$id.startTimeError } } catch {}
    return ("this batch has NO recorded start time for pid $($id.pid) ($($id.processName)), so its target cannot be identified: $why. " +
            "pid alone is not an identity -- Windows reuses pids -- so there is no way to tell this process from a different one that " +
            "took the same number, and a batch fired into the wrong world gives a confident, wrong answer. Refusing rather than guessing.")
  }

  $now = Read-ProcessStartTime ([int]$id.pid)
  if (-not $now.Ok) {
    return ("pid $($id.pid) is alive but its start time COULD NOT BE READ ($($now.Error)), so it cannot be confirmed as the process this " +
            "batch was queued against (started $queuedStart). This check fails closed: an identity that cannot be established is refused.")
  }
  if ($now.StartTime -ne $queuedStart) {
    return ("pid $($id.pid) is alive but is a DIFFERENT process: it started at $($now.StartTime), the batch was queued against one that started at $queuedStart. " +
            "Windows reuses pids, and a restarted client is a different world -- different character position, different panels, possibly a different build.")
  }
  if ($id.processName -and ($proc.ProcessName -ne [string]$id.processName)) {
    return "pid $($id.pid) is now '$($proc.ProcessName)', not '$($id.processName)'"
  }
  return $null
}

function Get-BatchExpiryReason {
  param($Batch)
  if (-not $Batch.expiresAt) { return $null }
  # Same rule as the staleness check, and the same reason it is a try rather than a bare Parse: an
  # unparseable expiry is not evidence that the batch is fresh, and a throw here used to escape
  # into the drain's own loop, where $ErrorActionPreference = "Stop" ends the whole drain over one
  # corrupt file (R45 finding 9's shape, one field over).
  $exp = $null
  try { $exp = [datetime]::Parse([string]$Batch.expiresAt, $null, [System.Globalization.DateTimeStyles]::RoundtripKind) }
  catch {
    return ("this batch's expiry could not be read ('$($Batch.expiresAt)': $($_.Exception.Message)), so it cannot be established that it is " +
            "still within its TTL. Refusing rather than assuming it is fresh.")
  }
  if ((Get-Date) -gt $exp) {
    $age = "an unknown number of"
    try { $age = [int]((Get-Date) - [datetime]::Parse([string]$Batch.queuedAt, $null, [System.Globalization.DateTimeStyles]::RoundtripKind)).TotalMinutes } catch {}
    return "the batch expired: queued $age minute(s) ago, TTL $($Batch.ttlMinutes) minute(s), expiry $($Batch.expiresAt). Whatever it was verifying is no longer the question being asked."
  }
  return $null
}

# ---------------------------------------------------------------------------------------------
# Resolving a batch's target WITHOUT the power to end the drain.
#
# R45 finding 9. Invoke-QueueBatch used to call Resolve-Target inside a try/catch and test the
# result for $null. Neither half worked. Resolve-Target never returns $null (every failure path
# calls Fail), and Fail ends in `exit`, which raises a flow-control exception a user catch does
# NOT intercept -- measured on this host: `function F { exit 42 }; try { F } catch { }; "AFTER"`
# exits 42 and never prints AFTER. So the documented exit-8 refusal was dead code, and a second
# client window (Resolve-Target's "2 V Rising client processes are running", exit 2) killed the
# whole drain mid-queue: the batch was left `running` with a drainPid, the next drain reaped it as
# `interrupted` and never retried it, and every batch behind it was abandoned unread. A queue
# exists precisely so work survives the machine being busy; a drain that dies partway defeats it.
#
# Resolve-Target -Optional (R44 finding 19) is the non-exiting probe that fix needs. It softens
# the "it is not there" family (exit 2) into $null plus $script:ResolveError, and deliberately
# does NOT soften the two usage errors (exit 1), which still exit -- so those two are pre-empted
# here, before Resolve-Target is called, by checking the spec's shape. A hand-edited batch file is
# the only way to reach them, and it must not be able to end a drain either.
#
# Assert-NotLiveServerPath still THROWS from inside Resolve-Target and is caught here: the live
# server refusal survives intact (the batch is refused and nothing is sent), it simply refuses one
# batch instead of the whole queue.
# ---------------------------------------------------------------------------------------------
function Resolve-BatchTarget {
  param([string]$Spec)
  $spec = [string]$Spec
  if ($spec -ne "client" -and $spec -ne "foreground" -and
      $spec -notlike "hwnd:*" -and $spec -notlike "proc:*" -and $spec -notlike "title:*") {
    return [pscustomobject]@{ Target = $null; Reason = "the batch's -Window spec '$spec' is not one of client, foreground, hwnd:<n>, proc:<name>, title:<regex>" }
  }
  if ($spec -like "hwnd:*") {
    $raw = $spec.Substring(5).Trim()
    $n = 0
    $parsed = $false
    if ($raw -like "0x*") { try { [void][Convert]::ToInt64($raw.Substring(2), 16); $parsed = $true } catch {} }
    else { $parsed = [Int64]::TryParse($raw, [ref]$n) }
    if (-not $parsed) {
      return [pscustomobject]@{ Target = $null; Reason = "the batch's -Window spec '$spec' does not carry a number" }
    }
  }
  $target = $null
  try { $target = Resolve-Target $spec -Optional }
  catch {
    return [pscustomobject]@{ Target = $null; Reason = "resolving the batch's -Window spec '$spec' was refused: $($_.Exception.Message)" }
  }
  if ($null -eq $target) {
    $why = [string]$script:ResolveError
    if (-not $why) { $why = "it no longer resolves to a window" }
    return [pscustomobject]@{ Target = $null; Reason = "the batch's -Window spec '$spec' no longer resolves to a window: $why" }
  }
  return [pscustomobject]@{ Target = $target; Reason = $null }
}

# ---------------------------------------------------------------------------------------------
# The client log range a batch spanned. Recorded so the caller can read what the game did in
# response without going back and working out which lines were new.
# ---------------------------------------------------------------------------------------------

function Get-QueueClientLogPath {
  if ($ClientLogPath) { return $ClientLogPath }
  return (Join-Path $GameDir "BepInEx\LogOutput.log")
}

# Read a log the game still has open. Get-Content will not do it reliably; a share mode that
# includes Delete also survives the file being rolled underneath us.
function Read-SharedTextLines {
  param([string]$Path, [int]$MaxBytes = 67108864)
  if (-not (Test-Path -LiteralPath $Path)) { return $null }
  try {
    $len = (Get-Item -LiteralPath $Path).Length
    if ($len -gt $MaxBytes) { return $null }
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    try {
      $sr = New-Object System.IO.StreamReader($fs)
      try {
        $lines = New-Object System.Collections.ArrayList
        while ($true) { $l = $sr.ReadLine(); if ($null -eq $l) { break }; [void]$lines.Add($l) }
        return @($lines)
      }
      finally { $sr.Dispose() }
    }
    finally { $fs.Dispose() }
  }
  catch { return $null }
}

# The log's identity at one instant: how many lines it has, how many bytes, and what its FIRST
# line says. All three, because a roll is a FILE REPLACEMENT and the line count alone cannot see
# one.
#
# R45 finding 32. The roll test used to be `$logAfter -lt $logBefore`, so it only fired when the
# new log happened to be shorter than the old one at the moment the batch ended. A client that
# rolled its log and then wrote past the old line count -- which is the ordinary case for anything
# but the shortest batch, since a fresh V Rising log passes a thousand lines during startup --
# reported `rolled: false` and a line range computed across two different files, i.e. exactly the
# nonsense range observe-tool.md promised would be reported as a roll. Not readable is a THIRD answer
# and is never folded into "it did not roll".
#
# The first line rather than the creation time, which was the obvious choice and is the wrong one.
# NTFS file TUNNELING re-uses the original creation time when a file of the same name reappears
# in a directory within about 15 seconds -- which is precisely the shape of a log roll -- so a
# creation-time comparison would answer "not rolled" for the case it was added to catch.
# MEASURED on this host (C:, NTFS, 2026-09-04), not read from a manual: write LogOutput.log, wait
# 1.2 s, delete it and write a longer one -- CreationTimeUtc comes back BYTE-IDENTICAL; and again
# with Move-Item of a different file onto the name -- also identical. Both directions tunnel.
# A log is append-only, so its first line does not change while it is the same file.
function Get-LogStamp {
  param([string]$Path)
  $stamp = [pscustomobject]@{ Readable = $false; Lines = -1; Length = -1; FirstLine = $null }
  $lines = Read-SharedTextLines -Path $Path
  if ($null -eq $lines) { return $stamp }
  $stamp.Lines = $lines.Count
  if ($lines.Count -gt 0) { $stamp.FirstLine = [string]$lines[0] }
  try { $stamp.Length = [int64](Get-Item -LiteralPath $Path -ErrorAction Stop).Length }
  catch { return $stamp }
  $stamp.Readable = $true
  return $stamp
}

# $true / $false / $null, and $null means "not established" -- either stamp unreadable.
#
# Any of three facts proves a roll: fewer lines than before, fewer bytes than before, or a
# different first line. The last is the one that catches a log that rolled and then grew past where
# it was, and it is why the stamp carries more than a count. The first line is compared only when
# BOTH stamps have one: a log that was empty when the batch started has no fingerprint, and "it had
# no first line and now it has one" is growth, not a replacement.
function Test-LogRolled {
  param($Before, $After)
  if ($null -eq $Before -or $null -eq $After) { return $null }
  if (-not $Before.Readable -or -not $After.Readable) { return $null }
  if ($After.Lines -lt $Before.Lines) { return $true }
  if ($After.Length -lt $Before.Length) { return $true }
  if (($Before.Lines -gt 0) -and ($After.Lines -gt 0) -and
      ([string]$After.FirstLine -ne [string]$Before.FirstLine)) { return $true }
  return $false
}

# ---------------------------------------------------------------------------------------------
# Running one step: spawn tools/observe.ps1 and read what it says.
# ---------------------------------------------------------------------------------------------

function ConvertTo-ChildArgument {
  param([string]$Value)
  if ($null -eq $Value) { return '""' }
  if ($Value -eq "") { return '""' }
  # throw, not Fail: Fail exits, and this is called from inside a batch, where ending the process
  # abandons every other batch in the queue (R45 finding 9). The caller turns it into a failed
  # STEP, which fails that batch and leaves the drain running.
  if ($Value -match '"') { throw "internal: a step argument reached the command line with a double quote in it ('$Value'). This should have been refused at queue time." }
  if ($Value -notmatch '\s') { return $Value }
  # Trailing backslashes inside a quoted Windows argument would escape the closing quote, so they
  # are doubled. C:\a path\ -> "C:\a path\\".
  $v = $Value
  $m = [regex]::Match($v, '\\+$')
  if ($m.Success) { $v = $v + $m.Value }
  return '"' + $v + '"'
}

# Run powershell.exe once and collect everything it said: exit code, stdout, stderr, and the one
# JSON object -Json prints.
#
# NOT Start-Process. Measured on this host: `Start-Process -PassThru` WITHOUT `-Wait` returns a
# process object whose .ExitCode is EMPTY even after WaitForExit() reports true and HasExited is
# true -- and .Refresh() does not fix it. Adding `-Wait` populates it, but `-Wait` has no timeout,
# and a drain that hangs forever on one step is precisely the failure this tool exists to stop.
# System.Diagnostics.Process gives both a real exit code and a bounded wait.
#
# Both streams are drained by async tasks started before the wait. A redirected pipe that fills
# while nobody reads it blocks the child forever, and that deadlock does not show up until some
# step happens to print more than a pipe buffer.
function Start-ObserveProcess {
  param([string]$ArgumentString, [int]$TimeoutSeconds = 120)
  $res = [pscustomobject]@{ exitCode = -1; stdout = ""; stderr = ""; json = $null; error = $null; timedOut = $false }
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = (Join-Path $PSHOME "powershell.exe")
  $psi.Arguments = $ArgumentString
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.CreateNoWindow = $true
  $proc = $null
  try {
    $proc = [System.Diagnostics.Process]::Start($psi)
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
      try { $proc.Kill() } catch {}
      $res.timedOut = $true
      $res.error = "child did not finish within $TimeoutSeconds s and was killed"
      try { $proc.WaitForExit(5000) | Out-Null } catch {}
    }
    # The no-argument overload after a bounded wait is what flushes the async readers; without it
    # the tasks can still be running when their .Result is read.
    try { $proc.WaitForExit() } catch {}
    try { if ($outTask.Wait(10000)) { $res.stdout = [string]$outTask.Result } } catch {}
    try { if ($errTask.Wait(10000)) { $res.stderr = [string]$errTask.Result } } catch {}
    if (-not $res.timedOut) { $res.exitCode = $proc.ExitCode }
  }
  catch { $res.error = "could not start the child: $($_.Exception.Message)" }
  finally { if ($proc) { try { $proc.Dispose() } catch {} } }

  # observe.ps1 -Json prints exactly one JSON object, last, on stdout. Everything before it is
  # human narration.
  $jsonLine = $null
  foreach ($line in @($res.stdout -split "`r?`n")) {
    $t = $line.Trim()
    if ($t.StartsWith("{") -and $t.EndsWith("}")) { $jsonLine = $t }
  }
  if ($jsonLine) { try { $res.json = ($jsonLine | ConvertFrom-Json) } catch {} }
  return $res
}

function Build-StepArguments {
  param($Step, [string]$WindowSpec, $Batch)
  $a = New-Object System.Collections.ArrayList
  [void]$a.Add("-NoProfile")
  [void]$a.Add("-ExecutionPolicy"); [void]$a.Add("Bypass")
  [void]$a.Add("-File"); [void]$a.Add((ConvertTo-ChildArgument $script:ObserveSelf))
  [void]$a.Add($Step.action)
  [void]$a.Add("-Window"); [void]$a.Add((ConvertTo-ChildArgument $WindowSpec))
  [void]$a.Add("-GameDir"); [void]$a.Add((ConvertTo-ChildArgument ([string]$Batch.gameDir)))
  [void]$a.Add("-Json")
  if ($Batch.allowGameInput -and ($script:QueueInputActions -contains $Step.action)) { [void]$a.Add("-AllowGameInput") }
  if ($Batch.dryRun) { [void]$a.Add("-DryRun") }
  foreach ($p in $Step.args.PSObject.Properties) {
    $name = $p.Name
    $param = $script:QueueArgParam[$name]
    if (-not $param) { continue }
    if ($script:QueueSwitchArgs -contains $name) {
      $on = $true
      $sv = [string]$p.Value
      if ($sv -and ($sv -match '^(false|0|no|off)$')) { $on = $false }
      if ($on) { [void]$a.Add("-$param") }
    }
    else {
      [void]$a.Add("-$param")
      [void]$a.Add((ConvertTo-ChildArgument ([string]$p.Value)))
    }
  }
  return ($a -join " ")
}

function Invoke-QueueStep {
  param($Step, [string]$WindowSpec, $Batch, $Paths)

  $started = Get-Date
  $record = [ordered]@{
    index      = $Step.index
    label      = $Step.label
    action     = $Step.action
    startedAt  = $started.ToString("o")
    finishedAt = $null
    durationMs = 0
    exitCode   = $null
    ok         = $false
    sent       = $null
    command    = $null
    error      = $null
  }

  if ($Step.action -eq "wait") {
    $ms = 0
    if (-not [int]::TryParse([string](Get-StepArgValue $Step "ms"), [ref]$ms) -or $ms -lt 0 -or $ms -gt 600000) {
      $record.error = "step $($Step.index) (wait): 'ms' is not a whole number of milliseconds between 0 and 600000 (got '$(Get-StepArgValue $Step "ms")'). The batch file has been edited since it was queued."
      $record.sent = "nothing"
      $record.exitCode = 1
      $record.finishedAt = (Get-Date).ToString("o")
      return ([pscustomobject]$record)
    }
    $record.command = "wait $ms ms (in the drain itself; nothing is sent and nothing is spawned)"
    $record.sent = "waited $ms ms"
    Start-Sleep -Milliseconds $ms
    $record.exitCode = 0
    $record.ok = $true
    $record.finishedAt = (Get-Date).ToString("o")
    $record.durationMs = [int]((Get-Date) - $started).TotalMilliseconds
    return ([pscustomobject]$record)
  }

  # Building the command line can throw (ConvertTo-ChildArgument's internal invariant). It is a
  # failed STEP, not a dead drain.
  $argString = $null
  try { $argString = Build-StepArguments -Step $Step -WindowSpec $WindowSpec -Batch $Batch }
  catch {
    $record.error = "the step's command line could not be built: $($_.Exception.Message)"
    $record.sent = "nothing"
    $record.exitCode = 1
    $record.finishedAt = (Get-Date).ToString("o")
    $record.durationMs = [int]((Get-Date) - $started).TotalMilliseconds
    return ([pscustomobject]$record)
  }
  $record.command = "powershell.exe $argString"

  $child = Start-ObserveProcess -ArgumentString $argString -TimeoutSeconds $StepTimeoutSeconds
  $record.exitCode = $child.exitCode
  if ($child.timedOut) { $record.error = "the step did not finish within -StepTimeoutSeconds ($StepTimeoutSeconds s) and was killed" }
  elseif ($child.error) { $record.error = $child.error }
  $stderr = $child.stderr

  if ($child.json) {
    $obj = $child.json
    if ($true) {
      $record.stepJson = $obj
      if ($obj.PSObject.Properties["events"] -and $obj.events) { $record.sent = @($obj.events) -join " | " }
      if ($obj.PSObject.Properties["error"] -and $obj.error) { $record.error = [string]$obj.error }
      if ($Step.action -eq "shot" -and $obj.PSObject.Properties["file"]) {
        $record.shot = [ordered]@{
          file           = [string]$obj.file
          method         = [string]$obj.method
          width          = $obj.width
          height         = $obj.height
          mean           = $obj.mean
          stdDev         = $obj.stdDev
          uniqueColours  = $obj.uniqueColours
          dominantColour = [string]$obj.dominantColour
          blank          = $obj.blank
          bytes          = $null
        }
        if ($obj.file -and (Test-Path -LiteralPath ([string]$obj.file))) {
          $record.shot.bytes = (Get-Item -LiteralPath ([string]$obj.file)).Length
        }
      }
    }
  }
  if (-not $record.error -and $stderr) {
    $first = @($stderr -split "`r?`n" | Where-Object { $_.Trim() -ne "" })
    if ($first.Count -gt 0) { $record.error = $first[0].Trim() }
  }
  if (-not $record.sent) {
    if ($record.shot) { $record.sent = "captured $($record.shot.file) via $($record.shot.method) (sent nothing)" }
    else { $record.sent = "$($Step.action) (the step returned no event list)" }
  }

  $record.ok = ($record.exitCode -eq 0)
  $record.finishedAt = (Get-Date).ToString("o")
  $record.durationMs = [int]((Get-Date) - $started).TotalMilliseconds
  return ([pscustomobject]$record)
}

# ---------------------------------------------------------------------------------------------
# Running a whole batch.
# ---------------------------------------------------------------------------------------------

# Every call to this function is a batch reaching a TERMINAL state: refused, expired, done, failed
# or aborted. R45 finding 32: latest.json was copied at three of those sites and at none of the
# four in the drain loop, so `docs/observe-tool.md`'s "a copy of the most recently finished batch file"
# was true for three of the seven ways a batch finishes. A caller polling it after a drain that
# refused batch B read batch A's `ok: true`. The copy lives here now, welded to the state write, so
# a future terminal state cannot be added without it.
#
# The reaper's `interrupted` is deliberately NOT routed through here: it repairs a record some
# EARLIER drain left behind, at start-up, before this drain has run anything. It is not this
# drain's most recent finish and must not displace it.
function Set-BatchState {
  param($Batch, $Paths, [string]$State, [string]$Reason)
  Add-BatchProperty $Batch "state" $State
  if ($Reason) { Add-BatchProperty $Batch "reason" $Reason }
  $path = Get-BatchPath $Paths $Batch.batchId
  Save-JsonFile -Path $path -Object $Batch
  # Never fatal: latest.json is a convenience copy, and losing it must not fail a batch whose
  # own record is already written (the same rule the drain applies to every other decoration).
  try { Copy-Item -LiteralPath $path -Destination $Paths.Latest -Force -ErrorAction Stop }
  catch { Write-DrainLog $Paths "batch $($Batch.batchId): latest.json not updated: $($_.Exception.Message)" }
}

function Invoke-QueueBatch {
  param($Batch, $Paths, [int]$WaitedSeconds = 0)

  # Claim the batch with a full identity, not a bare pid (R45 finding 11). Repair-InterruptedBatches
  # has to decide whether the claimer is still alive, and this file's own opening argument is that a
  # pid is not an identity because Windows reuses them.
  $Batch.state = "running"
  $Batch.drainPid = $PID
  $ownStart = Read-ProcessStartTime $PID
  Add-BatchProperty $Batch "drainStartTime" $ownStart.StartTime
  Add-BatchProperty $Batch "drainStartTimeError" $ownStart.Error
  Save-JsonFile -Path (Get-BatchPath $Paths $Batch.batchId) -Object $Batch

  # Re-resolve the target NOW, immediately before anything is sent, and confirm it is the same
  # process the batch was aimed at. This is the last check before real input.
  $stale = Get-BatchStaleReason $Batch
  if ($stale) {
    Write-DrainLog $Paths "batch $($Batch.batchId): REFUSED as stale -- $stale"
    Say "batch $($Batch.batchId): REFUSED -- $stale"
    $Batch.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $stale; steps = @() }
    Set-BatchState $Batch $Paths "refused" $stale
    return 8
  }

  $resolved = Resolve-BatchTarget ([string]$Batch.target.spec)
  $target = $resolved.Target
  if ($null -eq $target) {
    $why = $resolved.Reason
    Write-DrainLog $Paths "batch $($Batch.batchId): REFUSED -- $why"
    Say "batch $($Batch.batchId): REFUSED -- $why"
    $Batch.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $why; steps = @() }
    Set-BatchState $Batch $Paths "refused" $why
    return 8
  }
  if ($target.Pid -ne [int]$Batch.target.pid) {
    $why = "'$($Batch.target.spec)' now resolves to pid $($target.Pid) ('$($target.ProcessName)'), but the batch was queued against pid $($Batch.target.pid) ('$($Batch.target.processName)')"
    Write-DrainLog $Paths "batch $($Batch.batchId): REFUSED as stale -- $why"
    Say "batch $($Batch.batchId): REFUSED -- $why"
    $Batch.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $why; steps = @() }
    Set-BatchState $Batch $Paths "refused" $why
    return 8
  }

  # Every step is driven at the exact window handle just verified to belong to the verified pid,
  # so the batch cannot drift onto a second window of a different process between steps.
  $windowSpec = "hwnd:0x{0:X}" -f [int64]$target.Hwnd

  # The log to measure is the one the BATCH was queued against. This used to call
  # Get-QueueClientLogPath in the DRAIN's process, which has its own -ClientLogPath (usually none),
  # so a batch queued with -ClientLogPath pointing at one file had a different file measured for it
  # -- the game's BepInEx log by default. Found by running `queue-selftest`: its "log range" check
  # read `lines 1000..1000 (0 new)` off the idle V Rising client log while the batch it was asking
  # about had just driven the scratch window. The batch records clientLogFile at queue time for
  # exactly this reason; an explicit -ClientLogPath on the drain still overrides it.
  $batchLog = $null
  try { $batchLog = [string]$Batch.clientLogFile } catch { }
  if ($ClientLogPath) { $logPath = $ClientLogPath }
  elseif ($batchLog) { $logPath = $batchLog }
  else { $logPath = Get-QueueClientLogPath }
  $logBefore = Get-LogStamp $logPath
  $started = Get-Date
  # Captured here, beside $started, because it is a fact about the batch's start (R45 finding 32).
  $lockedAtStart = Test-SessionLocked
  Write-DrainLog $Paths "batch $($Batch.batchId) ($($Batch.label)): running $($Batch.steps.Count) step(s) against $windowSpec pid $($target.Pid); client log at $($logBefore.Lines) line(s)"
  Say ""
  Say "batch $($Batch.batchId)  '$($Batch.label)'"
  Say "  target   $windowSpec  pid $($target.Pid)  '$($target.Title)'  isGame $($target.IsGame)"
  Say "  steps    $($Batch.steps.Count)   needsInput $($Batch.needsInput)   dryRun $($Batch.dryRun)   waited ${WaitedSeconds}s"

  $records = New-Object System.Collections.ArrayList
  $failedStep = $null
  # R45 finding 32. `drain done: N batch(es) run` counted every batch this function RETURNED from,
  # including the three refusals above, which return before a single step is spawned. A drain that
  # refused one stale batch printed "1 batch(es) run ... 1 refused" for a run in which nothing was
  # sent, and -Json's `ran` was wrong in exactly the case a caller cares about. The counter is a
  # measurement now: it is set here, on the last line before the step loop, and the drain reads it
  # instead of inferring "it ran" from "it came back".
  $script:BatchStepsAttempted = $true
  foreach ($step in $Batch.steps) {
    $r = Invoke-QueueStep -Step $step -WindowSpec $windowSpec -Batch $Batch -Paths $Paths
    [void]$records.Add($r)
    $mark = "ok  "
    if (-not $r.ok) { $mark = "FAIL" }
    $detail = $r.sent
    if ($r.shot) { $detail = "$($r.shot.file)  $($r.shot.width)x$($r.shot.height) mean $($r.shot.mean) stddev $($r.shot.stdDev) dominant $($r.shot.dominantColour) blank $($r.shot.blank)" }
    if ($r.error) { $detail = $r.error }
    Say ("  {0} {1,-3} {2,-8} {3,-16} exit {4,-3} {5} ms  {6}" -f $mark, $r.index, $r.action, $r.label, $r.exitCode, $r.durationMs, $detail)
    Write-DrainLog $Paths "  step $($r.index) $($r.action) '$($r.label)' exit $($r.exitCode) : $detail"
    if (-not $r.ok) {
      $failedStep = $r.index
      if (-not $ContinueOnError) {
        Say "  stopping this batch at step $($r.index) (pass -ContinueOnError to run the rest anyway)"
        Write-DrainLog $Paths "  batch $($Batch.batchId): stopped at step $($r.index)"
        break
      }
    }
  }

  $logAfter = Get-LogStamp $logPath
  $slicePath = $null
  $rolled = Test-LogRolled $logBefore $logAfter
  $newLines = 0
  if ($rolled -eq $false) {
    $newLines = $logAfter.Lines - $logBefore.Lines
    if ($newLines -gt 0) {
      $all = Read-SharedTextLines -Path $logPath
      if ($all) {
        $slice = @($all[$logBefore.Lines..($logAfter.Lines - 1)])
        $slicePath = Join-Path $Paths.Logs "$($Batch.batchId).clientlog.txt"
        [System.IO.File]::WriteAllLines($slicePath, $slice, (New-Object System.Text.UTF8Encoding($false)))
      }
    }
  }

  $Batch.result = [pscustomobject]@{
    ok               = ($null -eq $failedStep)
    refused          = $false
    drainPid         = $PID
    startedAt        = $started.ToString("o")
    finishedAt       = (Get-Date).ToString("o")
    waitedSeconds    = $WaitedSeconds
    # R45 finding 32: read BEFORE the first step, not after the last one. A reader takes this as
    # the state the batch executed in, and a machine that locked at some point during a twelve-step
    # batch would answer that question with the state it was in when the batch ENDED.
    sessionLocked    = $lockedAtStart
    windowSpecUsed   = $windowSpec
    resolvedPid      = $target.Pid
    failedStep       = $failedStep
    steps            = @($records)
    clientLog        = [pscustomobject]@{
      file      = $logPath
      lineBefore = $logBefore.Lines
      lineAfter  = $logAfter.Lines
      newLines   = $newLines
      # $true, $false, or null when either end could not be read. Null is not "it did not roll".
      rolled     = $rolled
      slice      = $slicePath
    }
  }
  $state = "done"
  if ($null -ne $failedStep) { $state = "failed" }
  Set-BatchState $Batch $Paths $state $null

  $rollNote = ""
  if ($rolled -eq $true) { $rollNote = " -- THE LOG ROLLED during the batch; the range is not meaningful" }
  elseif ($null -eq $rolled) { $rollNote = " -- the log could not be read at one or both ends, so whether it rolled is UNKNOWN" }
  Say ("  client log lines {0}..{1} ({2} new){3}" -f $logBefore.Lines, $logAfter.Lines, $newLines, $rollNote)
  if ($slicePath) { Say "  log slice        $slicePath" }
  Say "  result           $(Get-BatchPath $Paths $Batch.batchId)"
  Write-DrainLog $Paths "batch $($Batch.batchId): $state; client log $($logBefore.Lines)..$($logAfter.Lines) ($newLines new)"

  if ($null -ne $failedStep) { return 10 }
  return 0
}

# ---------------------------------------------------------------------------------------------
# The queue itself.
# ---------------------------------------------------------------------------------------------

# Run one batch so that NOTHING it does can take the rest of the queue with it (R45 finding 9).
#
# Two layers, because the two failure shapes are different and PowerShell only lets one of them be
# caught:
#
#   catch    an ordinary terminating error inside the batch -- a corrupt field, an IOException from
#            a colliding writer -- fails THAT batch and returns 10. The drain carries on.
#   finally  `exit` is not catchable, and neither is a pipeline stop or a Ctrl-C. Measured on this
#            host: a finally block DOES still run for all of those, and an `exit` still leaves with
#            the code it chose. So a batch whose process left from inside it is written down as
#            `aborted` -- instead of being left `running` for the next drain to reap as
#            `interrupted`, which reads as "we sent half of it" when nothing may have been sent at
#            all. It is not retried either way: a half-sent input batch must not be replayed blind.
#
#            WHAT THE RECORD MAY SAY (R46 finding 3). This block used to write "a gate refused
#            mid-batch" unconditionally. `finally` cannot see which unwind ran it, and the unwind
#            this tool actually gets is Ctrl-C, which docs/observe-tool.md advertises as the way to
#            interrupt a drain -- so the one guarantee this batch of work exists to enforce, that a
#            record states only what was established, was broken by the record that enforces it.
#            The two observations that ARE available (a Fail's own message, and whether the step
#            loop was ever entered) are cleared below and composed by Get-BatchAbortRecord.
#
#            `aborted` is terminal and deliberately not consumed by any control path: the reaper
#            looks only at `running`, Get-PendingBatches only at `pending`, queue-cancel only at
#            `pending`. It is a REPORT -- queue-status prints it and Set-BatchState copies it to
#            latest.json -- which is why the wording is the whole of the fix. Wiring the reaper to
#            retry it would be wrong: an aborted input batch may have been half-sent.
# The drain's three counters, and the one decision that folds a finished batch into them.
#
# It lives here, out of the loop, for the reason R45 finding 13 gave: the drain loop needs a queue
# root, a real window and a child process, so nothing in the suite can call it, and a counting rule
# that only exists inside it is a rule no test can hold. These two functions need none of that.
#
# `Ran` is "steps were sent", measured from $script:BatchStepsAttempted, which Invoke-QueueBatch
# sets on the last line before its step loop. It used to be `$ran++` after every return, so the
# three refusals that return before any step is spawned were counted as batches that ran, and
# -Json's `ran` was wrong in exactly the case a caller reads it for (R45 finding 32). The three
# numbers deliberately do not partition the batches taken: a refusal is Refused only, and a batch
# that threw before its first step is Failed only.
function New-DrainTally {
  return [pscustomobject]@{ Ran = 0; Failed = 0; Refused = 0 }
}

function Add-BatchOutcome {
  param($Tally, [int]$Code, [bool]$StepsAttempted)
  if ($StepsAttempted) { $Tally.Ran++ }
  if ($Code -eq 8) { $Tally.Refused++ }
  elseif ($Code -eq 10) { $Tally.Failed++ }
}

function Invoke-QueueBatchGuarded {
  param($Batch, $Paths, [int]$WaitedSeconds = 0)
  $finished = $false
  # Cleared here so a throw BEFORE the step loop -- the catch below -- leaves it false and the
  # drain does not count that batch as run (R45 finding 32).
  $script:BatchStepsAttempted = $false
  # ...and the other observation the abort record is composed from (R46 finding 3). Cleared per
  # batch so a Fail from an earlier command in this process cannot be reported as this batch's
  # cause: set here means it was set BETWEEN here and the unwind, which is inside this batch.
  $script:LastRefusal = $null
  try {
    $code = Invoke-QueueBatch -Batch $Batch -Paths $Paths -WaitedSeconds $WaitedSeconds
    $finished = $true
    return $code
  }
  catch {
    $finished = $true
    $why = "this batch ended in an unhandled error and was stopped: $($_.Exception.Message)"
    Say "batch $($Batch.batchId): FAILED -- $why"
    Write-DrainLog $Paths "batch $($Batch.batchId): unhandled error -- $($_.Exception.ToString())"
    try {
      Add-BatchProperty $Batch "result" ([pscustomobject]@{ ok = $false; refused = $false; reason = $why; steps = @() })
      Set-BatchState $Batch $Paths "failed" $why
    } catch {}
    return 10
  }
  finally {
    if (-not $finished) {
      # Composed from the two things this path observed, and from nothing else. See
      # Get-BatchAbortRecord: the sentence changes with the evidence, and the no-evidence
      # sentence names all three unwinds rather than picking the flattering one.
      $abort = Get-BatchAbortRecord -StepsAttempted ([bool]$script:BatchStepsAttempted) -Refusal $script:LastRefusal
      try { Write-DrainLog $Paths "batch $($Batch.batchId): ABORTED -- $($abort.LogLine)" } catch {}
      try {
        Add-BatchProperty $Batch "result" ([pscustomobject]@{ ok = $false; refused = $abort.Refused; reason = $abort.Reason; steps = @() })
        Set-BatchState $Batch $Paths "aborted" $abort.Reason
      } catch {}
    }
  }
}

function Get-PendingBatches {
  param($Paths)
  $files = @(Get-ChildItem -LiteralPath $Paths.Batches -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object Name)
  $out = New-Object System.Collections.ArrayList
  foreach ($f in $files) {
    $b = Read-JsonFile $f.FullName
    if ($null -eq $b) { continue }
    if ([string]$b.state -eq "pending") { [void]$out.Add($b) }
  }
  return @($out)
}

# A drain that was killed mid-batch leaves a batch marked 'running' with a pid that is gone. Say
# so, rather than leaving it to look like work in progress forever.
#
# WHO STILL HOLDS THIS BATCH (R45 finding 11). This used to be `Get-Process -Id $b.drainPid` and
# nothing else -- the exact hazard the target-identity section 400 lines above argues against, and
# the file said so about the target while doing it to itself. A dead drain's pid is reused by any
# process on the machine; the next drain then saw that pid alive, skipped the batch, and the batch
# stayed `running` FOREVER: Get-PendingBatches only returns `pending`, so no drain ever looked at
# it again, queue-cancel skips it because it is not pending, and queue-status printed `running`
# beside a drain that had not existed for days. Neither run, nor retried, nor reportable as lost.
#
# So the claimer's identity is pid AND start time, the same pair the targets use, and the decision
# fails closed the same way: a claimer that cannot be shown to be alive is treated as dead. That
# is safe here for a reason this function can state rather than assume -- it runs only while the
# caller holds the queue's exclusive execution lock, and every path that executes a batch out of
# this root (drain AND queue's inline run) takes that lock first, so no live executor can be
# holding a batch at this moment. -LockHeld makes the caller assert it; without it, nothing is
# reaped and the reason is printed instead of a silent no-op.
function Repair-InterruptedBatches {
  param($Paths, [switch]$LockHeld)
  $files = @(Get-ChildItem -LiteralPath $Paths.Batches -Filter "*.json" -ErrorAction SilentlyContinue)
  foreach ($f in $files) {
    $b = Read-JsonFile $f.FullName
    if ($null -eq $b -or [string]$b.state -ne "running") { continue }

    $claimPid = 0
    try { $claimPid = [int]$b.drainPid } catch {}
    $claimStart = $null
    try { if ($b.PSObject.Properties["drainStartTime"]) { $claimStart = [string]$b.drainStartTime } } catch {}

    $verdict = $null   # $null = dead, a sentence = alive and why we believe it
    $why = $null
    if ($claimPid -le 0) {
      $why = "the batch records no drain pid at all, so nothing can be claiming it"
    }
    else {
      $proc = $null
      try { $proc = Get-Process -Id $claimPid -ErrorAction Stop } catch {}
      if ($null -eq $proc) {
        $why = "the drain that claimed this batch (pid $claimPid) has exited"
      }
      elseif (-not $claimStart) {
        $why = ("pid $claimPid is alive but this batch records no start time for the drain that claimed it, so that process cannot be " +
                "shown to BE that drain -- Windows reuses pids. Treated as gone, because no executor can hold this queue root while the " +
                "lock this drain is holding is held")
      }
      else {
        $now = Read-ProcessStartTime $claimPid
        if (-not $now.Ok) {
          $why = ("pid $claimPid is alive but its start time could not be read ($($now.Error)), so it cannot be shown to be the drain that " +
                  "claimed this batch (started $claimStart). Treated as gone, because no executor can hold this queue root while the lock " +
                  "this drain is holding is held")
        }
        elseif ($now.StartTime -ne $claimStart) {
          $why = ("pid $claimPid is alive but is a DIFFERENT process: it started at $($now.StartTime), the drain that claimed this batch " +
                  "started at $claimStart. The pid was reused")
        }
        else {
          $verdict = "pid $claimPid started at $claimStart, which is the drain that claimed it"
        }
      }
    }

    if ($verdict) {
      # This should be unreachable while the execution lock is held; if it happens, say so rather
      # than silently skipping, because it means the lock did not mean what this code thinks.
      Say "batch $($b.batchId) is still claimed by a LIVE process ($verdict) -- left alone."
      Write-DrainLog $Paths "batch $($b.batchId): left running; $verdict"
      continue
    }
    if (-not $LockHeld) {
      Say "batch $($b.batchId) looks abandoned ($why) but the execution lock is not held here, so it is left alone."
      continue
    }

    $reason = "$why; the batch was interrupted mid-run and is NOT retried automatically -- a half-sent input batch must not be replayed blind"
    $b.state = "interrupted"
    Add-BatchProperty $b "reason" $reason
    Save-JsonFile -Path $f.FullName -Object $b
    Say "reaped $($b.batchId): $reason"
    Write-DrainLog $Paths "reaped $($b.batchId) as interrupted: $why"
  }
}

# ---------------------------------------------------------------------------------------------
# Actions.
# ---------------------------------------------------------------------------------------------

function Invoke-Queue {
  $Paths = Get-QueuePaths

  $text = $null
  if ($StepsFile) {
    try { Assert-NotLiveServerPath $StepsFile } catch { Fail $_.Exception.Message 3 }
    if (-not (Test-Path -LiteralPath $StepsFile)) { Fail "-StepsFile '$StepsFile' does not exist" 1 }
    $text = (Get-Content -LiteralPath $StepsFile -Raw)
  }
  elseif ($Steps) { $text = $Steps }
  else {
    Fail ("usage: observe.ps1 queue -Steps '<steps>'   e.g.  -Steps 'key F10; shot; key Escape; shot'" + [Environment]::NewLine +
          "       steps are separated by ';'; each is one of: " + ($script:QueueStepActions -join ", ") + [Environment]::NewLine +
          "       arguments are name=value (label=<name> names the step); key/type/wait take one bare argument." + [Environment]::NewLine +
          "       a -Steps string beginning '[' is read as JSON instead. -StepsFile <path> reads either form from a file.") 1
  }

  $parsed = $null
  if ($text.TrimStart().StartsWith("[")) { $parsed = ConvertFrom-JsonSteps $text }
  else { $parsed = ConvertFrom-CompactSteps $text }

  $batchId = New-QueueBatchId
  $steps = Initialize-QueueSteps -Steps $parsed -Paths $Paths -BatchIdValue $batchId

  # Resolving the target here is what makes the batch mean something: it pins the world the steps
  # were written for. Resolve-Target also runs the live-server fence on the process path.
  $target = Resolve-Target $Window
  $identity = Get-TargetIdentity $target

  # No start time, no identity, no batch (R45 finding 12). A batch whose target cannot be pinned
  # would be refused by Get-BatchStaleReason at every drain anyway; refusing it HERE makes that a
  # loud message now instead of a blank where a timestamp should be in a line nobody reads twice.
  if ($identity.startTimeUnavailable) {
    Fail ("the start time of the target process (pid $($identity.pid), $($identity.processName)) could not be read: $($identity.startTimeError). " +
          "A batch is pinned to its target by pid AND start time, because Windows reuses pids -- with no start time there is no way to tell " +
          "this process from a different one that later takes the same number, and a batch fired into the wrong world gives a confident, " +
          "wrong answer. Nothing is queued. Run the client as this user (an elevated or other-user process cannot be identified from here), " +
          "or capture with a plain 'shot', which pins nothing and claims nothing.") 8
  }

  $needsInput = $false
  foreach ($s in $steps) { if ($script:QueueInputActions -contains $s.action) { $needsInput = $true } }

  if ($needsInput -and $target.IsGame -and -not $AllowGameInput -and -not $DryRun) {
    Fail ("this batch sends input to the V Rising client (pid $($target.Pid)) and -AllowGameInput was not passed. " +
          "The gate is applied HERE, when a human writes the batch, as well as in the tool the drain spawns -- " +
          "an unattended drain must never be the thing that decides it is allowed to touch a live world. " +
          "Re-queue with -AllowGameInput, or with -DryRun for a rehearsal batch that sends nothing.") 3
  }

  $now = Get-Date
  $label = $Label
  if (-not $label) { $label = ($steps | ForEach-Object { $_.action }) -join "-" }

  $batch = [pscustomobject]@{
    schema         = $script:QueueSchema
    batchId        = $batchId
    label          = $label
    state          = "pending"
    queuedAt       = $now.ToString("o")
    ttlMinutes     = $TtlMinutes
    expiresAt      = $now.AddMinutes($TtlMinutes).ToString("o")
    queuedBy       = "$env:USERNAME on $env:COMPUTERNAME, pid $PID"
    needsInput     = $needsInput
    allowGameInput = [bool]$AllowGameInput
    dryRun         = [bool]$DryRun
    gameDir        = $GameDir
    target         = $identity
    clientLogFile  = (Get-QueueClientLogPath)
    steps          = @($steps)
    drainPid       = $null
    reason         = $null
    result         = $null
  }

  # THE SPLIT (below) runs a capture-only or -DryRun batch here, inline. That run is an EXECUTION
  # of a batch out of this queue root, so it takes the same exclusive lock a drain does, and it
  # takes it BEFORE the file is written (R45 finding 24).
  #
  # It used to take no lock at all. The batch file went to disk as `pending` and was then run
  # inline several lines later, and everything in between -- five Say calls, seven result
  # assignments, then the run's own target resolution and a line count over a possibly-64 MB log
  # -- was a window in which a live drain's Get-PendingBatches could read the same file and run it
  # too. Both processes then executed the batch: two `shot` children wrote the same PNG path, the
  # recorded result was whichever finished last, and the client-log range was nonsense for both.
  # Worse, both wrote the batch file through Save-JsonFile, whose temp name was a fixed
  # "$Path.tmp", so the loser threw an unhandled IOException -- fatal to whichever process it
  # landed in, the drain half the time.
  #
  # If a drain already holds the lock, the batch is simply left pending and the drain runs it: a
  # drain takes capture-only batches on every pass, including while it is waiting on an input
  # batch, so the "captures never wait" property is kept.
  $wantsInline = ((-not $needsInput -or $DryRun) -and -not $Defer)
  $inlineLock = $null
  # WHO WILL RUN IT IS PART OF THE MEASUREMENT (R46 finding 6). This branch used to build the
  # cause here and then print the same two following lines either way, so on the branch where
  # Get-ExecutionLockState had just established that it could NOT tell what holds the lock, the
  # operator was told a drain would pick the batch up "without anyone doing anything" -- and if no
  # drain was running, it sat pending until a human noticed. Both halves now come from
  # Get-ExecutionLockConsequence, which has the lock state in its hand.
  $lockOut = $null
  if ($wantsInline) {
    $inlineLock = Open-ExecutionLock $Paths "queue pid $PID running batch $batchId inline since $(Get-Date -Format 'o')"
    if ($null -eq $inlineLock) {
      $lockOut = Get-ExecutionLockConsequence (Get-ExecutionLockState $Paths) $Paths.Lock
    }
  }

  $path = Get-BatchPath $Paths $batchId
  Save-JsonFile -Path $path -Object $batch

  Say "queued      $batchId  '$label'"
  Say "  steps     $($steps.Count): $(($steps | ForEach-Object { $_.action }) -join ', ')"
  Say "  target    $($identity.spec) -> pid $($identity.pid) '$($identity.processName)' started $($identity.startTime)  isGame $($identity.isGame)"
  Say "  input     needsInput $needsInput   allowGameInput $($batch.allowGameInput)   dryRun $($batch.dryRun)"
  Say "  expires   $($batch.expiresAt)  (TTL $TtlMinutes min)"
  Say "  file      $path"

  $script:Result.batchId = $batchId
  $script:Result.batchFile = $path
  $script:Result.needsInput = $needsInput
  $script:Result.stepCount = $steps.Count
  $script:Result.queueRoot = $Paths.Root
  $script:Result.expiresAt = $batch.expiresAt
  $script:Result.targetPid = $identity.pid

  # THE SPLIT. A batch with no input step is safe on a locked machine and is most valuable there
  # (evidence 174), so it does not sit in the queue waiting for a human. It runs now.
  if ($wantsInline -and $inlineLock) {
    $locked = Test-SessionLocked
    $why = "capture-only batch (no input steps)"
    if ($DryRun) { $why = "rehearsal batch (-DryRun: every input step prints its SendInput records and sends none of them)" }
    Say ""
    Say "$why : running NOW, regardless of the session lock. Session locked: $locked."
    Write-DrainLog $Paths "queue ${batchId}: $why, running inline (sessionLocked=$locked)"
    $code = 1
    try { $code = Invoke-QueueBatchGuarded -Batch $batch -Paths $Paths -WaitedSeconds 0 }
    finally { Close-ExecutionLock $inlineLock }
    $script:Result.ranInline = $true
    $script:Result.state = $batch.state
    $script:Result.batchResult = $batch.result
    Finish $code
  }
  if ($wantsInline) {
    # Wanted to run inline and could not take the lock. Say exactly that, and say who -- if
    # anyone was established to exist -- will run it.
    Say ""
    Say "This batch would have run inline, but $($lockOut.Cause)."
    Say $lockOut.Advice
    Write-DrainLog $Paths "queue ${batchId}: left pending, not run inline -- $($lockOut.Cause) (runsUnattended=$($lockOut.RunsUnattended))"
    $script:Result.ranInline = $false
    $script:Result.deferredReason = $lockOut.Cause
    # A -Json caller asked the same question the sentence answers, and used to be given only the
    # cause: whether anything will run this batch is the actionable half.
    $script:Result.runsUnattended = $lockOut.RunsUnattended
    $script:Result.drainLockState = $lockOut.State
    Finish 0
  }

  if ($needsInput -and -not $DryRun) {
    $locked = Test-SessionLocked
    $script:Result.sessionLocked = $locked
    Say ""
    if ($locked) {
      Say "This batch contains input steps and $(Get-LockReason), so it will NOT run now."
      Say "Start a drain and leave it: it waits for the lock to clear, then runs the batch."
    }
    else {
      Say "This batch contains input steps and the session is usable now. Run the drain to send it."
    }
    Say "  powershell -NoProfile -ExecutionPolicy Bypass -File tools\observe.ps1 drain"
  }
  $script:Result.ranInline = $false
  Finish 0
}

function Invoke-Drain {
  $Paths = Get-QueuePaths

  # One executor at a time. The lock is an exclusively-opened file held for the holder's lifetime,
  # so a holder that dies releases it the moment its handle is closed by the OS -- there is no
  # stale lock to clean up and no timestamp heuristic to get wrong. `queue`'s inline run takes the
  # same lock (R45 finding 24), so the refusal below names what is actually holding it.
  $stamp = "drain pid $PID started $(Get-Date -Format 'o')"
  $lockStream = Open-ExecutionLock $Paths $stamp
  if ($null -eq $lockStream) {
    # Not routed through Get-ExecutionLockConsequence, on purpose: that composer answers "who
    # will run this pending batch", and the consequence here is the opposite one -- this process
    # refuses to do anything at all. Both branches below already state only what was established
    # (R45 finding 25 rewrote them), which is why they are left alone.
    $state = Get-ExecutionLockState $Paths
    if ($state.State -eq "running") {
      Fail ("another process is executing this queue and holds $($Paths.Lock)" + $(if ($state.Holder) { " -- $($state.Holder)" } else { "" }) +
            ". A second drain would run the same queued batches a second time, so this one is refusing. " +
            "Wait for it, or stop it by creating $($Paths.Stop).") 7
    }
    Fail ("the execution lock $($Paths.Lock) could not be taken and it could not be established that anything is holding it: " +
          "$($state.Detail). Refusing rather than running the queue a second time on an assumption.") 7
  }

  $overall = 0
  try {
    if (Test-Path -LiteralPath $Paths.Stop) {
      Remove-Item -LiteralPath $Paths.Stop -Force -ErrorAction SilentlyContinue
      Say "cleared a leftover stop file from a previous drain: $($Paths.Stop)"
    }

    Write-DrainLog $Paths "=== $stamp; poll ${PollSeconds}s, max wait ${MaxWaitMinutes} min, root $($Paths.Root)"
    Say "drain       pid $PID   queue $($Paths.Root)"
    Say "  poll      every $PollSeconds s while waiting; giving up after $MaxWaitMinutes min"
    Say "  stop by   creating $($Paths.Stop)   (or Ctrl-C)"
    Say "  log       $($Paths.DrainLog)"

    Repair-InterruptedBatches -Paths $Paths -LockHeld

    $drainStart = Get-Date
    $tally = New-DrainTally


    $gaveUp = $false

    while ($true) {
      if (Test-Path -LiteralPath $Paths.Stop) {
        Remove-Item -LiteralPath $Paths.Stop -Force -ErrorAction SilentlyContinue
        Say "stop file seen; stopping."
        Write-DrainLog $Paths "stopped by stop file"
        $gaveUp = $true
        break
      }

      $pending = Get-PendingBatches $Paths

      # Capture-only batches never wait for anything. Take all of them, every pass, before any
      # decision about the lock is even made.
      $captureOnly = @($pending | Where-Object { -not (Test-BatchMustWait $_) })
      foreach ($b in $captureOnly) {
        $exp = Get-BatchExpiryReason $b
        if ($exp) {
          Say "batch $($b.batchId): EXPIRED -- $exp"
          Write-DrainLog $Paths "batch $($b.batchId): expired"
          $b.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $exp; steps = @() }
          Set-BatchState $b $Paths "expired" $exp
          $tally.Refused++
          continue
        }
        $code = Invoke-QueueBatchGuarded -Batch $b -Paths $Paths -WaitedSeconds 0

        Add-BatchOutcome $tally $code $script:BatchStepsAttempted
      }

      $inputBatches = @(Get-PendingBatches $Paths | Where-Object { Test-BatchMustWait $_ })
      if ($inputBatches.Count -eq 0) {
        if ($captureOnly.Count -eq 0) { break }
        continue
      }

      $b = $inputBatches[0]

      $exp = Get-BatchExpiryReason $b
      if ($exp) {
        Say "batch $($b.batchId): EXPIRED -- $exp"
        Write-DrainLog $Paths "batch $($b.batchId): expired"
        $b.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $exp; steps = @() }
        Set-BatchState $b $Paths "expired" $exp
        $tally.Refused++
        continue
      }
      $stale = Get-BatchStaleReason $b
      if ($stale) {
        Say "batch $($b.batchId): REFUSED -- $stale"
        Write-DrainLog $Paths "batch $($b.batchId): refused as stale before waiting -- $stale"
        $b.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $stale; steps = @() }
        Set-BatchState $b $Paths "refused" $stale
        $tally.Refused++
        continue
      }

      # Wait for the session to become usable. This is the whole reason the queue exists, and it
      # is a WAIT: there is no path here that delivers input to a locked session.
      $waitStart = Get-Date
      $waited = 0
      $announced = $false
      $stopped = $false
      while (Test-SessionLocked) {
        if (-not $announced) {
          Say ""
          Say "batch $($b.batchId) needs input and $(Get-LockReason). Waiting for that to clear."
          Say "  Nothing is sent while locked. This is a wait, not a workaround."
          Write-DrainLog $Paths "batch $($b.batchId): waiting for unlock"
          $announced = $true
        }
        # Sleep in one-second slices so the stop file and Ctrl-C are noticed promptly, while the
        # expensive checks (lock, staleness, new capture batches) happen once per -PollSeconds.
        for ($i = 0; $i -lt $PollSeconds; $i++) {
          if (Test-Path -LiteralPath $Paths.Stop) { $stopped = $true; break }
          Start-Sleep -Seconds 1
        }
        if ($stopped) { break }
        $waited = [int]((Get-Date) - $waitStart).TotalSeconds

        if ($waited -ge ($MaxWaitMinutes * 60)) {
          $why = "waited $([int]($waited/60)) minute(s) for the session to be unlocked, which is past -MaxWaitMinutes ($MaxWaitMinutes). Refusing to keep holding this batch: what it was verifying is stale by now."
          Say "batch $($b.batchId): GIVING UP -- $why"
          Write-DrainLog $Paths "batch $($b.batchId): gave up waiting -- $why"
          $b.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $why; steps = @() }
          Set-BatchState $b $Paths "expired" $why
          $tally.Refused++
          $stopped = $true
          break
        }
        $exp = Get-BatchExpiryReason $b
        if ($exp) {
          Say "batch $($b.batchId): EXPIRED while waiting -- $exp"
          Write-DrainLog $Paths "batch $($b.batchId): expired while waiting"
          $b.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $exp; steps = @() }
          Set-BatchState $b $Paths "expired" $exp
          $tally.Refused++
          $stopped = $true
          break
        }
        $stale = Get-BatchStaleReason $b
        if ($stale) {
          Say "batch $($b.batchId): REFUSED while waiting -- $stale"
          Write-DrainLog $Paths "batch $($b.batchId): refused as stale while waiting -- $stale"
          $b.result = [pscustomobject]@{ ok = $false; refused = $true; reason = $stale; steps = @() }
          Set-BatchState $b $Paths "refused" $stale
          $tally.Refused++
          $stopped = $true
          break
        }
        # A capture batch queued while we wait must not be held up by an input batch that is.
        foreach ($cb in @(Get-PendingBatches $Paths | Where-Object { -not (Test-BatchMustWait $_) })) {
          Say "  (a capture-only batch arrived while waiting; running it now)"
          $code = Invoke-QueueBatchGuarded -Batch $cb -Paths $Paths -WaitedSeconds 0

          Add-BatchOutcome $tally $code $script:BatchStepsAttempted
        }
        if (($waited % 300) -lt $PollSeconds) { Write-DrainLog $Paths "still waiting for unlock ($waited s)" }
      }

      if ($stopped) {
        if (Test-Path -LiteralPath $Paths.Stop) {
          Remove-Item -LiteralPath $Paths.Stop -Force -ErrorAction SilentlyContinue
          Say "stop file seen while waiting; stopping with work still queued."
          Write-DrainLog $Paths "stopped by stop file while waiting"
          $gaveUp = $true
          break
        }
        continue
      }

      if ($announced) {
        Say "session is unlocked after ${waited}s; running batch $($b.batchId)."
        Write-DrainLog $Paths "batch $($b.batchId): unlocked after ${waited}s"
      }
      $code = Invoke-QueueBatchGuarded -Batch $b -Paths $Paths -WaitedSeconds $waited

      Add-BatchOutcome $tally $code $script:BatchStepsAttempted
    }

    Say ""
    # "run" means steps were sent. A batch refused before its first step is counted in $refused
    # only, and one that threw before its first step lands in $failed only, so the three numbers
    # do not have to add up to the number of batches taken and must not be read as if they did.
    Say "drain done: $($tally.Ran) batch(es) ran steps, $($tally.Failed) with a failed step or an error, $($tally.Refused) refused or expired before any step."
    Write-DrainLog $Paths "=== drain pid $PID finished: ran $($tally.Ran), failed $($tally.Failed), refused $($tally.Refused)"
    $script:Result.ran = $tally.Ran
    $script:Result.failed = $tally.Failed
    $script:Result.refused = $tally.Refused
    $script:Result.queueRoot = $Paths.Root
    $script:Result.drainLog = $Paths.DrainLog
    $script:Result.latest = $Paths.Latest

    # Precedence, worst first: stopped with work owed beats a failed step beats a refusal,
    # because they need different things done about them and the caller sees one number.
    if ($gaveUp) { $overall = 9 }
    elseif ($tally.Failed -gt 0) { $overall = 10 }
    elseif ($tally.Refused -gt 0) { $overall = 8 }
    else { $overall = 0 }
  }
  finally {
    Close-ExecutionLock $lockStream
  }
  Finish $overall
}

function Invoke-QueueStatus {
  $Paths = Get-QueuePaths
  $files = @(Get-ChildItem -LiteralPath $Paths.Batches -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object Name)
  $locked = Test-SessionLocked
  Say "queue root  $($Paths.Root)"
  Say "session     $(if ($locked) { "$($script:LockState.ToUpperInvariant()) -- capture batches still run; input batches wait ($script:LockDetail)" } else { 'unlocked' })"

  # Is a drain alive? ASKING MUST NOT CHANGE THE ANSWER (R45 finding 25). This used to open the
  # lock OpenOrCreate/FileShare.None -- so the status command CREATED a zero-byte drain.lock on a
  # queue root that had never been drained and held it exclusively while it did, and a drain
  # starting in that instant refused with exit 7 and the sentence "a drain is already running", a
  # positive claim about a state that was never true. It also reported RUNNING for ANY failure to
  # open, so an ACL denial or an antivirus handle printed `drain RUNNING`. Get-ExecutionLockState
  # creates nothing and only says RUNNING for a sharing violation, which is what a live holder
  # actually produces.
  $lockState = Get-ExecutionLockState $Paths
  $draining = ($lockState.State -eq "running")
  $holder = [string]$lockState.Holder
  switch ($lockState.State) {
    "running"    {
      $tail = " -- the lock is held, and the holder left no name in it"
      if ($holder) { $tail = " -- " + $holder }
      Say ("drain       RUNNING" + $tail)
    }
    "idle"       { Say "drain       not running (the lock opened, so nobody is holding it)" }
    "absent"     { Say "drain       not running (no lock file: nothing has executed a batch in this queue root)" }
    default      { Say "drain       *** LOCK STATE UNREADABLE *** -- $($lockState.Detail). This is NOT a claim that a drain is running; it is a claim that this command could not tell." }
  }
  Say ""

  $rows = New-Object System.Collections.ArrayList
  foreach ($f in $files) {
    $b = Read-JsonFile $f.FullName
    if ($null -eq $b) { continue }
    if ($BatchId -and ([string]$b.batchId -ne $BatchId)) { continue }
    [void]$rows.Add([pscustomobject]@{
      BatchId = [string]$b.batchId
      State   = [string]$b.state
      Input   = [bool]$b.needsInput
      Steps   = @($b.steps).Count
      Pid     = $b.target.pid
      Queued  = [string]$b.queuedAt
      Label   = [string]$b.label
      Reason  = [string]$b.reason
    })
  }
  if ($rows.Count -eq 0) { Say "(no batches)" }
  else { $rows | Format-Table -AutoSize | Out-String -Width 240 | Write-Host }

  Say "batches     $($Paths.Batches)"
  Say "shots       $($Paths.Shots)"
  Say "log slices  $($Paths.Logs)"
  Say "latest      $($Paths.Latest)"
  Say "drain log   $($Paths.DrainLog)"

  $script:Result.queueRoot = $Paths.Root
  $script:Result.sessionLocked = $locked
  $script:Result.draining = $draining
  $script:Result.drainLockState = $lockState.State
  $script:Result.drainLockDetail = $lockState.Detail
  $script:Result.batches = @($rows | ForEach-Object { "$($_.BatchId) $($_.State) input=$($_.Input) steps=$($_.Steps) pid=$($_.Pid) '$($_.Label)'" })
  $script:Result.pending = @($rows | Where-Object { $_.State -eq "pending" }).Count
  Finish 0
}

function Invoke-QueueCancel {
  $Paths = Get-QueuePaths
  if (-not $BatchId -and -not $All) { Fail "usage: observe.ps1 queue-cancel -BatchId <id>   or   observe.ps1 queue-cancel -All   (cancels every PENDING batch; nothing is deleted, the files stay as evidence)" 1 }
  $files = @(Get-ChildItem -LiteralPath $Paths.Batches -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object Name)
  $n = 0
  foreach ($f in $files) {
    $b = Read-JsonFile $f.FullName
    if ($null -eq $b) { continue }
    if ($BatchId -and ([string]$b.batchId -ne $BatchId)) { continue }
    if ([string]$b.state -ne "pending") { continue }
    $b.state = "cancelled"
    $b.reason = "cancelled by hand at $(Get-Date -Format 'o')"
    Save-JsonFile -Path $f.FullName -Object $b
    Say "cancelled $($b.batchId) '$($b.label)'"
    Write-DrainLog $Paths "cancelled $($b.batchId)"
    $n++
  }
  if ($n -eq 0) { Say "nothing pending matched." }
  $script:Result.cancelled = $n
  Finish 0
}

<#
.SYNOPSIS
  End-to-end self-test for the observe.ps1 batch queue. Drives the queue against the scratch
  WinForms window in tools/observe/selftest-target.ps1 and never touches the game.

.DESCRIPTION
  Dot-sourced by tools/observe.ps1, like Queue.ps1. Run it with:

      tools\observe.ps1 queue-selftest

  Everything here runs against a throwaway queue root under %TEMP%, so it cannot disturb the real
  queue at <workspace>\_observe\queue, and against a window this script starts and owns, so it
  cannot disturb the game.

  Checks that need nothing but a desktop session (they run while the workstation is LOCKED):

    parse       the compact step grammar, and the queue-time rejections
    capture     a capture-only batch runs INLINE at queue time regardless of the lock
    stale-gone  a batch whose target process has exited is refused, exit 8
    stale-pid   a batch whose target pid is alive but is a DIFFERENT process is refused, exit 8
    stale-nostart  a batch with NO recorded target start time is refused, exit 8 (R45 finding 12)
    expiry      a batch past its TTL is refused, exit 8
    onedrain    a second drain refuses while one holds the lock, exit 7
    stopfile    a waiting drain stops on the stop file, exit 9, with the batch still pending

  Checks that need an UNLOCKED session, because they send real input:

    input       a queued key/type/shot batch is drained, the scratch window records what it
                actually received, and the batch's log line range covers it

  When the workstation is locked the input check is SKIPPED and said so, loudly, with the one
  command to run once the machine is unlocked. It is not silently passed.
#>

function Start-QueueSelfTestTarget {
  param([int]$TimeoutSeconds = 30)
  $target = Join-Path $PSScriptRoot "selftest-target.ps1"
  if (-not (Test-Path $target)) { Fail "self-test target missing: $target" 1 }
  $log = Join-Path $env:TEMP ("progenitor-queue-selftest-" + [Guid]::NewGuid().ToString("N") + ".log")
  $proc = Start-Process -FilePath (Join-Path $PSHOME "powershell.exe") `
    -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-WindowStyle", "Hidden", "-File", ('"' + $target + '"'), "-LogPath", ('"' + $log + '"')) `
    -PassThru
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    if (Test-Path $log) {
      $text = (Get-Content $log -Raw -ErrorAction SilentlyContinue)
      if ($text -and $text -match "READY") {
        return [pscustomobject]@{ Process = $proc; Log = $log }
      }
    }
    Start-Sleep -Milliseconds 200
  }
  try { if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } } catch {}
  Fail "the scratch window never reported READY within $TimeoutSeconds s (log $log)" 6
}

function Stop-QueueSelfTestTarget {
  param($Target)
  if ($null -eq $Target) { return }
  try { if (-not $Target.Process.HasExited) { Stop-Process -Id $Target.Process.Id -Force -ErrorAction SilentlyContinue } } catch {}
  try { $Target.Process.WaitForExit(5000) | Out-Null } catch {}
}

function Invoke-QueueSelfTest {
  $base = Join-Path $env:TEMP ("progenitor-observe-queue-selftest-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
  New-Item -ItemType Directory -Force -Path $base | Out-Null
  $self = $script:ObserveSelf
  $titleSpec = "title:^Progenitor observe self-test$"
  $locked = Test-SessionLocked

  $failures = New-Object System.Collections.ArrayList
  $skipped = New-Object System.Collections.ArrayList
  $passed = 0
  function QCheck {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    if ($Ok) { Say ("  PASS  {0,-14} {1}" -f $Name, $Detail); $script:QueueSelfTestPassed++ }
    else { Say ("  FAIL  {0,-14} {1}" -f $Name, $Detail); [void]$script:QueueSelfTestFailures.Add($Name) }
  }
  $script:QueueSelfTestFailures = $failures
  $script:QueueSelfTestPassed = 0

  function QRun {
    param([string[]]$ObserveArgs, [int]$TimeoutSeconds = 180)
    $a = New-Object System.Collections.ArrayList
    [void]$a.Add("-NoProfile"); [void]$a.Add("-ExecutionPolicy"); [void]$a.Add("Bypass")
    [void]$a.Add("-File"); [void]$a.Add((ConvertTo-ChildArgument $self))
    foreach ($x in $ObserveArgs) { [void]$a.Add((ConvertTo-ChildArgument $x)) }
    [void]$a.Add("-Json")
    return (Start-ObserveProcess -ArgumentString ($a -join " ") -TimeoutSeconds $TimeoutSeconds)
  }

  Say "queue self-test"
  Say "  scratch queue roots under $base"
  Say "  scratch window  'Progenitor observe self-test' (never the game)"
  Say "  session         $(if ($locked) { 'LOCKED -- the input check will be SKIPPED, not passed' } else { 'unlocked' })"
  Say ""

  $target = $null
  try {
    # -----------------------------------------------------------------------------------------
    Say "grammar and queue-time rejections (no window needed):"
    $r = QRun @("queue", "-Steps", "key F10; shot; key Escape; shot", "-Window", "foreground", "-QueueRoot", (Join-Path $base "parse"), "-Defer")
    $ok = ($r.exitCode -eq 0) -and $r.json -and ($r.json.stepCount -eq 4) -and ($r.json.needsInput -eq $true)
    QCheck "parse" $ok "'key F10; shot; key Escape; shot' -> $(if ($r.json) { "$($r.json.stepCount) steps, needsInput $($r.json.needsInput)" } else { "exit $($r.exitCode)" })"

    $r = QRun @("queue", "-Steps", "kye F10", "-Window", "foreground", "-QueueRoot", (Join-Path $base "parse"))
    QCheck "bad action" ($r.exitCode -eq 1) "'kye F10' -> exit $($r.exitCode) ($(($r.stderr -split "`r?`n")[0]))"

    $r = QRun @("queue", "-Steps", "key value=F10 nosuch=1", "-Window", "foreground", "-QueueRoot", (Join-Path $base "parse"))
    QCheck "bad arg" ($r.exitCode -eq 1) "unknown step argument -> exit $($r.exitCode)"

    # A double quote cannot be passed on the command line -- ConvertTo-ChildArgument refuses it
    # before the child is even started, which is the outer half of the same rule -- so the inner
    # half is exercised through a JSON steps file, where a quote survives to the parser.
    $quoteFile = Join-Path $base "quote-steps.json"
    [System.IO.File]::WriteAllText($quoteFile, '[{"action":"type","args":{"value":"a\"b"}}]', (New-Object System.Text.UTF8Encoding($false)))
    $r = QRun @("queue", "-StepsFile", $quoteFile, "-Window", "foreground", "-QueueRoot", (Join-Path $base "parse"))
    QCheck "quote refused" (($r.exitCode -eq 1) -and ($r.stderr -match "double quote")) "a double quote in a step argument -> exit $($r.exitCode)"

    # -----------------------------------------------------------------------------------------
    Say ""
    Say "against the scratch window:"
    $target = Start-QueueSelfTestTarget
    Say "  scratch window pid $($target.Process.Id), log $($target.Log)"

    # THE SPLIT. A capture-only batch must run at queue time whatever the lock says.
    $capRoot = Join-Path $base "capture"
    $r = QRun @("queue", "-Steps", "shot label=first; wait 200; shot label=second", "-Window", $titleSpec, "-QueueRoot", $capRoot, "-ClientLogPath", $target.Log)
    $capOk = ($r.exitCode -eq 0) -and $r.json -and ($r.json.ranInline -eq $true) -and ([string]$r.json.state -eq "done")
    $shots = @(Get-ChildItem -Path (Join-Path $capRoot "shots") -Filter "*.png" -Recurse -ErrorAction SilentlyContinue)
    $capOk = $capOk -and ($shots.Count -eq 2)
    QCheck "capture" $capOk "capture-only batch ran inline with the session locked=$locked; exit $($r.exitCode), state $(if ($r.json) { $r.json.state } else { '?' }), $($shots.Count) PNG(s)"

    # Requirement 5, the obvious half: a batch aimed at a process that no longer exists.
    $goneRoot = Join-Path $base "stale-gone"
    $r = QRun @("queue", "-Steps", "key F9", "-Window", $titleSpec, "-QueueRoot", $goneRoot)
    $queuedOk = ($r.exitCode -eq 0)
    $deadPid = $target.Process.Id
    Stop-QueueSelfTestTarget $target
    $target = $null
    $r = QRun @("drain", "-QueueRoot", $goneRoot, "-PollSeconds", "1")
    $b = @(Get-ChildItem -Path (Join-Path $goneRoot "batches") -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object { Read-JsonFile $_.FullName })
    $staleOk = $queuedOk -and ($r.exitCode -eq 8) -and ($b.Count -eq 1) -and ([string]$b[0].state -eq "refused") -and ([string]$b[0].reason -match "has exited")
    QCheck "stale-gone" $staleOk "queued against pid $deadPid, killed it, drained -> exit $($r.exitCode), state $(if ($b.Count) { $b[0].state } else { '?' })"

    # The other half: pid alive, but it is not the same process any more. Forced by rewriting the
    # recorded start time, because a real pid collision cannot be arranged on demand.
    $target = Start-QueueSelfTestTarget
    $pidRoot = Join-Path $base "stale-pid"
    $r = QRun @("queue", "-Steps", "key F9", "-Window", $titleSpec, "-QueueRoot", $pidRoot)
    $bf = @(Get-ChildItem -Path (Join-Path $pidRoot "batches") -Filter "*.json" -ErrorAction SilentlyContinue)
    $edited = $false
    if ($bf.Count -eq 1) {
      $bo = Read-JsonFile $bf[0].FullName
      $bo.target.startTime = "1999-01-01T00:00:00.0000000+00:00"
      Save-JsonFile -Path $bf[0].FullName -Object $bo
      $edited = $true
    }
    $r = QRun @("drain", "-QueueRoot", $pidRoot, "-PollSeconds", "1")
    $b = @(Get-ChildItem -Path (Join-Path $pidRoot "batches") -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object { Read-JsonFile $_.FullName })
    $pidOk = $edited -and ($r.exitCode -eq 8) -and ($b.Count -eq 1) -and ([string]$b[0].state -eq "refused") -and ([string]$b[0].reason -match "DIFFERENT process")
    QCheck "stale-pid" $pidOk "same pid, different start time -> exit $($r.exitCode), state $(if ($b.Count) { $b[0].state } else { '?' })"

    # The case R45 finding 12 named: a batch with NO recorded start time. The check above only
    # ever exercised the branch where one is present, because line 154 writes one in by hand --
    # so a guard that silently fell through to "not stale" whenever Process.StartTime could not be
    # read was invisible here. Forced the same way, by nulling the field rather than setting it.
    # The scratch window from the check above is still running and is the target here too.
    $noStartRoot = Join-Path $base "stale-nostart"
    $r = QRun @("queue", "-Steps", "key F9", "-Window", $titleSpec, "-QueueRoot", $noStartRoot)
    $bf = @(Get-ChildItem -Path (Join-Path $noStartRoot "batches") -Filter "*.json" -ErrorAction SilentlyContinue)
    $nulled = $false
    if ($bf.Count -eq 1) {
      $bo = Read-JsonFile $bf[0].FullName
      $bo.target.startTime = $null
      Save-JsonFile -Path $bf[0].FullName -Object $bo
      $nulled = $true
    }
    $r = QRun @("drain", "-QueueRoot", $noStartRoot, "-PollSeconds", "1")
    $b = @(Get-ChildItem -Path (Join-Path $noStartRoot "batches") -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object { Read-JsonFile $_.FullName })
    $noStartOk = $nulled -and ($r.exitCode -eq 8) -and ($b.Count -eq 1) -and ([string]$b[0].state -eq "refused") -and ($b[0].reason -match "NO recorded start time")
    QCheck "stale-nostart" $noStartOk "no recorded start time -> exit $($r.exitCode), state $(if ($b.Count) { $b[0].state } else { '?' })"

    # A batch that has been waiting past its bound is refused rather than fired late.
    $ttlRoot = Join-Path $base "expiry"
    $r = QRun @("queue", "-Steps", "key F9", "-Window", $titleSpec, "-QueueRoot", $ttlRoot, "-TtlMinutes", "0")
    Start-Sleep -Milliseconds 1200
    $r = QRun @("drain", "-QueueRoot", $ttlRoot, "-PollSeconds", "1")
    $b = @(Get-ChildItem -Path (Join-Path $ttlRoot "batches") -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object { Read-JsonFile $_.FullName })
    $ttlOk = ($r.exitCode -eq 8) -and ($b.Count -eq 1) -and ([string]$b[0].state -eq "expired")
    QCheck "expiry" $ttlOk "-TtlMinutes 0 -> exit $($r.exitCode), state $(if ($b.Count) { $b[0].state } else { '?' })"

    # -----------------------------------------------------------------------------------------
    # One drain at a time, the wait loop, and the stop file. -PretendLocked makes the first drain
    # believe the session is locked so it waits; the hook can only ever cause more refusal.
    $lockRoot = Join-Path $base "onedrain"
    $r = QRun @("queue", "-Steps", "key F9", "-Window", $titleSpec, "-QueueRoot", $lockRoot)
    $queued = ($r.exitCode -eq 0)
    $dArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (ConvertTo-ChildArgument $self),
               "drain", "-QueueRoot", (ConvertTo-ChildArgument $lockRoot), "-PollSeconds", "2", "-PretendLocked", "-Json") -join " "
    # System.Diagnostics.Process, for the reason spelled out on Start-ObserveProcess: a
    # Start-Process object returns an EMPTY .ExitCode unless -Wait was used, and this drain must
    # be started WITHOUT waiting -- the whole check is that it is still running.
    $dpsi = New-Object System.Diagnostics.ProcessStartInfo
    $dpsi.FileName = (Join-Path $PSHOME "powershell.exe")
    $dpsi.Arguments = $dArgs
    $dpsi.UseShellExecute = $false
    $dpsi.RedirectStandardOutput = $true
    $dpsi.RedirectStandardError = $true
    $dpsi.CreateNoWindow = $true
    $drain1 = [System.Diagnostics.Process]::Start($dpsi)
    $d1Out = $drain1.StandardOutput.ReadToEndAsync()
    $d1Err = $drain1.StandardError.ReadToEndAsync()
    # Wait for it to actually hold the lock before asking a second one to refuse.
    #
    # Through Get-ExecutionLockState, which neither creates the file nor opens it exclusively
    # (R45 finding 25): this loop polled it four times a second with FileShare.None, so the
    # self-test could knock over the very drain it was waiting to observe.
    $held = $false
    $deadline = (Get-Date).AddSeconds(30)
    $lockPaths = [pscustomobject]@{ Lock = (Join-Path $lockRoot "drain.lock") }
    while ((Get-Date) -lt $deadline) {
      if ((Get-ExecutionLockState $lockPaths).State -eq "running") { $held = $true; break }
      Start-Sleep -Milliseconds 250
    }
    $r2 = QRun @("drain", "-QueueRoot", $lockRoot, "-PollSeconds", "1") 60
    QCheck "onedrain" ($queued -and $held -and ($r2.exitCode -eq 7)) "a second drain while one holds the lock -> exit $($r2.exitCode) (held=$held)"

    New-Item -ItemType File -Force -Path (Join-Path $lockRoot "stop-drain") | Out-Null
    $stoppedInTime = $drain1.WaitForExit(60000)
    $d1Exit = -1
    if ($stoppedInTime) { $drain1.WaitForExit(); $d1Exit = $drain1.ExitCode } else { try { $drain1.Kill() } catch {} }
    try { $d1Out.Wait(5000) | Out-Null; $d1Err.Wait(5000) | Out-Null } catch {}
    $b = @(Get-ChildItem -Path (Join-Path $lockRoot "batches") -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object { Read-JsonFile $_.FullName })
    $stopOk = $stoppedInTime -and ($d1Exit -eq 9) -and ($b.Count -eq 1) -and ([string]$b[0].state -eq "pending")
    QCheck "stopfile" $stopOk "waiting drain stopped on the stop file -> exit $d1Exit, batch left $(if ($b.Count) { $b[0].state } else { '?' }) (not lost)"

    # -----------------------------------------------------------------------------------------
    # Real input. Needs an unlocked session, because a queued input step reaches the same refusal
    # a hand-typed one does.
    Say ""
    if ($locked) {
      Say "input delivery: SKIPPED -- the workstation is LOCKED."
      Say "  A queued input batch waits for the lock to clear; it is never delivered to a locked"
      Say "  session. Run this once the machine is unlocked to cover the remaining check:"
      Say "      powershell -NoProfile -ExecutionPolicy Bypass -File tools\observe.ps1 queue-selftest"
      [void]$skipped.Add("input")
    }
    else {
      Say "input delivery through the queue (real SendInput, scratch window only):"
      $inRoot = Join-Path $base "input"
      $probe = "progenitor-queue-42"
      $steps = "key F9; wait 300; type $probe; wait 300; shot label=after"
      $r = QRun @("queue", "-Steps", $steps, "-Window", $titleSpec, "-QueueRoot", $inRoot, "-ClientLogPath", $target.Log)
      $queuedOk = ($r.exitCode -eq 0) -and $r.json -and ($r.json.needsInput -eq $true) -and ($r.json.ranInline -eq $false)
      QCheck "queued" $queuedOk "input batch queued and NOT run inline; exit $($r.exitCode)"

      $r = QRun @("drain", "-QueueRoot", $inRoot, "-PollSeconds", "2")
      $b = @(Get-ChildItem -Path (Join-Path $inRoot "batches") -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object { Read-JsonFile $_.FullName })
      $drained = ($r.exitCode -eq 0) -and ($b.Count -eq 1) -and ([string]$b[0].state -eq "done")
      QCheck "drained" $drained "drain -> exit $($r.exitCode), state $(if ($b.Count) { $b[0].state } else { '?' })"

      $text = ""
      try { $text = [string](Get-Content $target.Log -Raw -ErrorAction SilentlyContinue) } catch {}
      QCheck "key landed" ($text -match "KEYDOWN vk=120") "the scratch window logged KEYDOWN vk=120 (F9)"
      $typed = ""
      foreach ($l in @(Get-Content $target.Log -ErrorAction SilentlyContinue)) { if ($l -match "^CHAR (.)$") { $typed += $Matches[1] } }
      QCheck "text landed" ($typed -match [regex]::Escape($probe)) "the scratch window received '$typed'"

      $res = $null
      if ($b.Count -eq 1) { $res = $b[0].result }
      $shotOk = $false; $shotDetail = "no shot step recorded"
      if ($res) {
        foreach ($s in @($res.steps)) {
          if ($s.action -eq "shot" -and $s.PSObject.Properties["shot"] -and $s.shot) {
            $shotDetail = "$($s.shot.file) $($s.shot.width)x$($s.shot.height) stddev $($s.shot.stdDev) blank $($s.shot.blank)"
            $shotOk = ($s.exitCode -eq 0) -and (Test-Path -LiteralPath ([string]$s.shot.file)) -and ($s.shot.blank -eq $false)
          }
        }
      }
      QCheck "shot recorded" $shotOk $shotDetail

      $logOk = $false; $logDetail = "no clientLog block recorded"
      if ($res -and $res.clientLog) {
        $cl = $res.clientLog
        $logDetail = "lines $($cl.lineBefore)..$($cl.lineAfter) ($($cl.newLines) new), slice $($cl.slice)"
        $sliceText = ""
        if ($cl.slice -and (Test-Path -LiteralPath ([string]$cl.slice))) { $sliceText = [string](Get-Content -LiteralPath ([string]$cl.slice) -Raw) }
        $logOk = ($cl.lineAfter -gt $cl.lineBefore) -and ($sliceText -match "KEYDOWN vk=120")
      }
      QCheck "log range" $logOk $logDetail
    }
  }
  finally {
    Stop-QueueSelfTestTarget $target
  }

  Say ""
  $script:Result.failures = @($failures)
  $script:Result.skipped = @($skipped)
  $script:Result.passed = $script:QueueSelfTestPassed
  $script:Result.scratchRoot = $base
  $script:Result.sessionLocked = $locked
  Say "scratch roots left in place for inspection: $base"
  if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("observe: queue self-test FAILED: $($failures -join ', ')")
    Finish 6
  }
  if ($skipped.Count -gt 0) {
    Say "queue self-test PASSED $($script:QueueSelfTestPassed) check(s), SKIPPED $($skipped.Count) ($($skipped -join ', ')) because the workstation is locked."
  }
  else { Say "queue self-test PASSED ($($script:QueueSelfTestPassed) checks, nothing skipped)." }
  Finish 0
}

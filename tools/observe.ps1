<#
.SYNOPSIS
  Screenshot the running V Rising client and send it synthetic keyboard / mouse input.
  The host-side half of the dev loop: the part a human used to do by hand.

.DESCRIPTION
  ############################################################################################
  ##  READ THIS BEFORE RUNNING ANY INPUT SUBCOMMAND.                                        ##
  ##                                                                                        ##
  ##  `key`, `type`, `move`, `click` and `scroll` drive the REAL keyboard and mouse of the   ##
  ##  logged-in desktop session through Win32 SendInput. There is no per-window delivery:    ##
  ##  the events go to whatever window has focus, so this tool FOCUSES THE TARGET WINDOW     ##
  ##  FIRST, which TAKES FOCUS AWAY FROM WHATEVER THE USER IS DOING. It also MOVES THE       ##
  ##  MOUSE POINTER.                                                                        ##
  ##                                                                                        ##
  ##  Do not run an input subcommand while the user is typing somewhere else. Anything they  ##
  ##  type during the send interleaves with ours, and anything we send during a focus change ##
  ##  lands in their window. Use `-DryRun` (alias `-WhatIf`) to see the exact SendInput       ##
  ##  batch without sending it.                                                             ##
  ##                                                                                        ##
  ##  `shot` sends nothing and steals nothing. It is safe to run at any time.                ##
  ############################################################################################

  WHY THIS IS A HOST-SIDE SCRIPT AND NOT A DEV BRIDGE OP

  1. It works when the mod is broken, half-loaded, or not loaded at all -- which is precisely
     when a screenshot is worth having.
  2. It cannot destabilize the game process. Nothing runs inside VRising.exe.
  3. Changing it needs no build, no deploy and no Core swap.
  4. Input goes through the real OS input stack, so it exercises the same path a human hand
     does. A synthetic in-process path would bypass exactly the detector under test: research
     evidence 161 is a keybind detector that looked correct, passed every unit test against a
     fake input source, and did not see real presses. A test that cannot reproduce the bug is
     not a test.

  RUNTIME

  Windows PowerShell 5.1 supplies the supported System.Drawing compiler references. Calling this
  script from PowerShell 7 automatically re-enters 5.1 with the same bound parameter values and
  exit status. The handoff uses serialized data, so paths and quoted text are not evaluated.
  SCREENSHOT METHOD

  `-Method PrintWindow` asks the window to redraw itself into a bitmap (PW_RENDERFULLCONTENT).
  The received wisdom is that this returns SOLID BLACK for a D3D/DXGI swapchain because there is
  no GDI content to redraw. MEASURED ON THE V RISING CLIENT, THAT IS FALSE: it returns a full,
  correct game frame -- world, HUD, minimap, hotbar -- while a different window covered 66% of
  it, with no focus taken and nothing moved. That makes it the right method for unattended
  capture here: it does not read the desktop, so occlusion, Z-order and the lock screen are all
  irrelevant to it. It cannot capture -Region monitor or virtual, which are not one window.

  `-Method Screen` is Graphics.CopyFromScreen over the window's screen rectangle: a BitBlt out of
  the desktop, so it is the truth about what is on the glass -- including anything drawn OVER the
  game. Its cost is that the pixels must BE on the glass, so the window has to be un-minimized,
  on-screen and unoccluded, and the session must not be locked. The script checks all four and
  refuses rather than saving a picture of whatever was on top.

  `-Method Auto` (explicit opt-in) uses PrintWindow, and falls back to Screen only if PrintWindow returns
  a blank image. The direction of that fallback is deliberate: falling back TO PrintWindow would
  hide a black frame behind a second attempt, whereas falling back FROM it is gated by the blank
  check below and every step is printed.

  Either way the capture is analyzed before it is accepted: mean and standard deviation of
  luminance, distinct color count and the share of the most common color. A near-uniform
  image is reported as a FAILURE with exit code 5, not written as a silently black PNG.

  THE LOCKED-WORKSTATION CASE -- READ THIS ONE

  If the machine is locked, the lock screen is what is on the glass. Every capture is a picture
  of the PIN prompt; the game is running underneath and putting nothing on the desktop. This is
  not hypothetical: it is the state this tool was built in, and it is the state an unattended
  overnight run will usually find. Both paths check for it (LogonUI running in this session):
  `shot -Method Screen` refuses with exit 4 unless -AllowLockedSession, and the input subcommands
  refuse with exit 3 and NO override at all -- a synthetic key press on a locked machine is typed
  into the Windows credential box.

  `shot -Method PrintWindow` (the default) is NOT gated on the lock,
  and measurably works: research evidence 174 is a full game frame captured from a locked
  workstation. A locked session stops compositing without stopping the swapchain.

  THE LOCK GUARD FAILS CLOSED. Test-SessionLocked answers LOCKED when it cannot tell -- when the
  process enumeration throws, or errors in a way that is not "no such process". It used to end
  `catch { return $false }`, which reported every such failure as UNLOCKED and printed
  `session: unlocked` for a state it had not measured. Evidence 174's rule is that READING the
  screen is safe while locked and WRITING input is not, so a writing guard whose unknown answer is
  "safe to write" has inverted exactly that asymmetry. An unknown answer costs a round trip; the
  other direction costs a password typed into a chat box. `selftest -Offline` proves the direction
  by swapping the probe for one that cannot answer and asserting the guard refuses.

  THE QUEUE -- WHAT TO DO INSTEAD OF STOPPING WHEN THE MACHINE IS LOCKED

  A verification run that reaches an input step on a locked machine used to stop, leaving the
  check owed until a human came back. `queue` writes the batch down; `drain` runs it when the
  session can take it.

    observe.ps1 queue -Steps 'key F10; shot; key Escape; shot' -AllowGameInput
    observe.ps1 drain

  A batch whose steps are ALL captures (`shot`, `wait`) runs IMMEDIATELY, lock or no lock, at
  queue time and on every pass of a drain. Only a batch containing a real input step waits. The
  two capabilities have different safety requirements and do not share a gate.

  There is NO mechanism here for delivering input to a locked session, and there must never be
  one: no desktop switching, no message synthesis, no unlocking, no credentials. A queued input
  batch WAITS. The refusal costs nothing real -- Unity's Input System reads raw input, which
  Windows delivers to the foreground window on the ACTIVE desktop, so input synthesized at a
  locked session would not drive the game even if this tool were willing to try.

  A queued batch records the pid AND the start time of the process it was aimed at, plus an
  expiry. A client that has restarted since is a different world, and the drain refuses it
  (exit 8) rather than firing a batch into it and producing a confident, wrong answer.

  See tools/observe/Queue.ps1, docs/observe-tool.md and docs/observe-capture.md.

.PARAMETER Window
  Which window to act on.
    client            (default) the V Rising client: VRising.exe under -GameDir.
    foreground        whatever currently has focus. Input to it never changes focus.
    hwnd:<n>          an explicit window handle, decimal or 0x-hex.
    proc:<name>       the main window of a process by name, e.g. proc:notepad.
    title:<regex>     the first visible titled window whose title matches (case-insensitive).

.PARAMETER MaxWidth
  Maximum saved PNG width for shot, preserving aspect ratio without upscaling. Use 1920 for a
  compact review image; 0 (default) keeps original resolution. Blank detection still analyses the
  original capture. JSON reports sourceWidth/sourceHeight and saved width/height separately.
.PARAMETER AllowGameInput
  Required to send input to the V Rising client. Without it, input subcommands that resolve to
  the game refuse with exit code 3. Screenshots never need it. The gate exists because a stray
  keystroke into a live session is a real action in a real world with a real character in it.

.PARAMETER DryRun
  Report the exact SendInput batch that WOULD be sent -- every keydown/keyup, every scan code,
  every normalized mouse coordinate -- and send nothing. Aliased to -WhatIf. The dry run prints
  the same batch the real path sends, because both are produced by the same builder.

  Honored by key, type, move, click and scroll (all of which funnel through Enter-InputMode) and
  by queue/drain. `selftest` REFUSES it with exit 1 rather than obeying it: the self-test exists to
  send input and read back what a window received, so a dry run of it would assert nothing. It used
  to ignore the flag silently and press Ctrl+F9, type 21 characters, click and scroll for real.
  `selftest -Offline` is the mode that sends nothing and needs no desktop.

.PARAMETER ScreenCoords
  Treat -X / -Y as absolute virtual-desktop coordinates. The default is coordinates relative to
  the target window's CLIENT area (top-left of the drawable area = 0,0), which is what a UI
  layout bug is measured in.

.EXAMPLE
  # What is on screen right now, and where is the window? Never sends input.
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 windows
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 shot -Out <workspace>\_observe\before.png

.EXAMPLE
  # Press F10 in the client, wait for the panel to open, screenshot the result.
  tools/observe.ps1 key F10 -AllowGameInput
  tools/observe.ps1 shot -Out <workspace>\_observe\after-f10.png

.EXAMPLE
  # See what a chord would send, without sending it.
  tools/observe.ps1 key "Ctrl+F9" -DryRun
  # '+' is the separator when a name precedes it and the plus KEY when nothing does, so all of
  # these mean the plus key: "+", "Ctrl++", "Ctrl+Shift++"  (and "plus" / "equals" by name).

.EXAMPLE
  # Mouse, in client-relative coordinates unless -ScreenCoords is passed.
  tools/observe.ps1 click -X 820 -Y 512 -AllowGameInput
  tools/observe.ps1 scroll -Delta -3 -AllowGameInput
  tools/observe.ps1 type ".debug on" -AllowGameInput

.EXAMPLE
  # Prove the input path works against a window that is NOT the game. Creates its own
  # WinForms target, drives it, reads back what the target actually received, and reports.
  tools/observe.ps1 selftest

.EXAMPLE
  # Queue a batch now, run it whenever the session can take it. Nothing waits on a human.
  tools/observe.ps1 queue -Steps 'key F10; shot; key Escape; shot' -AllowGameInput -Label f10-toggle
  tools/observe.ps1 drain          # runs it now if unlocked; otherwise waits for the unlock
  tools/observe.ps1 queue-status   # what is queued, what ran, where the results are

.EXAMPLE
  # Captures never wait. This runs immediately even on a locked workstation, because
  # PrintWindow asks the window to render rather than reading the glass (evidence 174).
  tools/observe.ps1 queue -Steps 'shot; wait 2000; shot'

.EXAMPLE
  # Prove the queue end to end against the scratch WinForms window. Never touches the game.
  tools/observe.ps1 queue-selftest

.NOTES
  EXIT CODES
    0  success
    1  usage or unexpected error -- including `selftest -DryRun`, which is refused rather than
       silently ignored
    2  target window not found, or not a window any more. NOT produced by `windows`: that
       subcommand exists to list what is there, so an unresolvable -Window is a line in its output
       and it still exits 0. (A misspelled -Window is still exit 1 everywhere, including there.)
    3  refused by a safety gate: game input without -AllowGameInput, input while the
       workstation is locked, or a path under the live server
    4  the window could not be made ready: workstation locked, minimized, off-screen,
       occluded, or focus was requested and did not actually arrive (input is NOT sent then)
    5  the capture came back blank / near-uniform -- see -Method above
    6  a self-test assertion failed
    7  drain: another drain already holds the queue lock (it would double-run the queue)
    8  drain: a batch was refused as STALE (target process gone, or pid alive but a different
       process) or EXPIRED (past its TTL, or past -MaxWaitMinutes with the session still locked).
       That batch is finished with; it is not owed any more.
    9  drain: STOPPED with work still pending -- the stop file. The batch is left pending, so a
       later drain will pick it up.
   10  drain: a queued step failed; its own exit code is in the batch's result file

  If a drain hits several of these, the one it returns is the worst: 9 beats 10 beats 8, because
  they need different things done about them and the caller sees one number.

  Every failure prints one line beginning "observe: " to stderr and exits non-zero. Nothing
  here is interactive, so it is safe to drive unattended -- with the focus caveat above.
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet("windows", "shot", "key", "type", "move", "click", "scroll", "selftest",
               "queue", "drain", "queue-status", "queue-cancel", "queue-selftest")]
  [string]$Action = "windows",

  # `key` -> the chord(s). `type` -> the literal text. Unused by the others.
  [Parameter(Position = 1)]
  [Alias("Keys", "Text")]
  [string]$Value,

  [string]$Window = "client",
  [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\VRising",
  [int]$ProcessId = 0,

  # --- shot ---
  [string]$Out,
  # Downsize the saved PNG proportionally; 0 preserves the captured dimensions.
  [ValidateRange(0, 16384)]
  [int]$MaxWidth = 0,
  [ValidateSet("window", "client", "monitor", "virtual")]
  [string]$Region = "window",
  [ValidateSet("Auto", "Screen", "PrintWindow")]
  [string]$Method = "PrintWindow",
  # Standard deviation of luminance below this = "this image is blank". 2.0 passes any real UI
  # and fails a solid fill; a loading screen that is genuinely one color will trip it, which is
  # the correct answer to give a caller who asked for a screenshot of a UI.
  [double]$MinStdDev = 2.0,
  # The other half of the blank check, and the half that matters. Standard deviation ALONE is not
  # enough: a capture of the Windows lock screen -- black except for "Enter your PIN" -- scores a
  # stddev of 6.07 and sails past a 2.0 floor while being, for every practical purpose, blank. A
  # frame in which one color covers this share of the pixels is blank no matter how bright the
  # remaining sliver is.
  [double]$MaxDominantShare = 0.98,
  [switch]$AllowOccluded,
  [switch]$AllowLockedSession,
  [switch]$NoBlankCheck,

  # --- input, shared ---
  [Nullable[int]]$X,
  [Nullable[int]]$Y,
  [switch]$ScreenCoords,
  [Alias("WhatIf")]
  [switch]$DryRun,
  [switch]$AllowGameInput,
  [switch]$NoFocus,
  [int]$SettleMs = 250,
  [int]$DelayMs = 0,
  [int]$HoldMs = 45,
  [int]$RepeatCount = 1,
  [int]$GapMs = 120,

  # --- click / move / scroll ---
  [ValidateSet("left", "right", "middle")]
  [string]$Button = "left",
  [int]$Clicks = 1,
  [int]$Dx = 0,
  [int]$Dy = 0,
  [int]$Delta = 0,
  [switch]$Horizontal,

  # selftest only: run the structural half (struct layout, scan codes, coordinate maths) and skip
  # everything that needs a usable desktop. This is the half that still runs on a locked machine.
  [switch]$Offline,

  # --- queue / drain (tools/observe/Queue.ps1) ---
  # The batch, as a string. Compact form: steps separated by ';'.  'key F10; shot; key Escape; shot'
  # A string beginning '[' is read as JSON instead.
  [string]$Steps,
  [string]$StepsFile,
  # Defaults to <workspace>\_observe\queue (tools/common/Workspace.ps1).
  [string]$QueueRoot = "",
  [string]$Label,
  [string]$BatchId,
  [switch]$All,
  # How long a queued batch stays meaningful. Past this it is refused rather than fired into a
  # world that has moved on. Four hours is long enough for an overnight-ish wait and short enough
  # that a batch never outlives the question it was asking.
  [int]$TtlMinutes = 240,
  # At least 1 (R45 finding 35). The drain's wait loop sleeps in one-second slices, `for ($i = 0;
  # $i -lt $PollSeconds; $i++)`, so 0 -- or any negative value, which a bare [int] accepted --
  # made that body never execute and turned the enclosing `while (Test-SessionLocked)` into a
  # sleepless spin: two process enumerations and a full queue-directory walk per iteration,
  # pegging a core for as long as the machine stayed locked (up to -MaxWaitMinutes, default 720).
  # The documentation says flatly that it does not spin.
  [ValidateRange(1, 3600)]
  [int]$PollSeconds = 15,
  [int]$MaxWaitMinutes = 720,
  # queue: keep a capture-only batch in the queue instead of running it immediately.
  [switch]$Defer,
  [switch]$ContinueOnError,
  [int]$StepTimeoutSeconds = 120,
  # Where the batch reads its "what did the game do" line range from. Defaults to the client's
  # BepInEx log under -GameDir.
  [string]$ClientLogPath,

  # TEST HOOK, and read which direction it points. -PretendLocked makes Test-SessionLocked answer
  # TRUE when the session is in fact unlocked, so the queue's waiting and refusal behavior can be
  # exercised without locking the user's machine. It is OR-ed with the real signal, so it can only
  # ever produce MORE refusal, never less. There is no switch anywhere in this tool that does the
  # reverse, and there must never be one.
  #
  # The other half of the same discipline is $script:LockProbe, which `selftest -Offline`
  # substitutes in-process to prove the guard fails CLOSED on an unreadable session. That is a
  # variable, not a command-line switch, precisely so no caller can reach it: a probe that lies in
  # the "unlocked" direction is the one thing this design must not offer.
  [switch]$PretendLocked,

  [switch]$Json
)

$ErrorActionPreference = "Stop"
# Observe.Native.cs uses the .NET Framework System.Drawing compiler references. PowerShell 7
# forwards Bitmap to System.Drawing.Common, which its Add-Type reference set cannot resolve.
# Re-enter the supported runtime before loading interop. Serialize parameters as data so quoted
# paths, switches, Unicode and literal command-like text retain their meaning without evaluation.
if ($PSVersionTable.PSEdition -eq "Core") {
  $windowsPowerShell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
  if (-not (Test-Path -LiteralPath $windowsPowerShell)) {
    [Console]::Error.WriteLine("observe: Windows PowerShell 5.1 is required for screenshot interop; runtime not found: $windowsPowerShell")
    exit 1
  }
  $forwardFile = [IO.Path]::GetTempFileName()
  try {
    $forwardParameters = @{}
    foreach ($key in $PSBoundParameters.Keys) {
      $valueToForward = $PSBoundParameters[$key]
      if ($valueToForward -is [System.Management.Automation.SwitchParameter]) { $valueToForward = [bool]$valueToForward }
      $forwardParameters[$key] = $valueToForward
    }
    @{ Script = $PSCommandPath; Parameters = $forwardParameters; WorkingDirectory = (Get-Location).Path } |
      Export-Clixml -LiteralPath $forwardFile -Depth 10
    $forwardLiteral = $forwardFile.Replace("'", "''")
    $forwardCommand = "`$ProgressPreference = 'SilentlyContinue'; `$call = Import-Clixml -LiteralPath '$forwardLiteral'; Set-Location -LiteralPath `$call.WorkingDirectory; `$invoke = `$call.Parameters; & `$call.Script @invoke; exit `$LASTEXITCODE"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($forwardCommand))
    [Console]::Error.WriteLine("observe: using Windows PowerShell 5.1 for System.Drawing capture compatibility")
    & $windowsPowerShell -NoLogo -NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand $encodedCommand
    $forwardExit = $LASTEXITCODE
  }
  catch { [Console]::Error.WriteLine("observe: runtime handoff failed: " + $_.Exception.Message); $forwardExit = 1 }
  finally { Remove-Item -LiteralPath $forwardFile -Force -ErrorAction SilentlyContinue }
  exit $forwardExit
}

# The one fence (tools/common/Fence.ps1). This script writes PNG files at a caller-supplied
# path and reads process image paths, so -Out goes through the fence like any other root, and a
# window whose process lives under the live server is refused outright.
. (Join-Path $PSScriptRoot "common\Fence.ps1")
# Fence.ps1 dot-sources common\Workspace.ps1. The default queue lives beside the main checkout.
if (-not $QueueRoot) { $QueueRoot = Resolve-ProgenitorWorkspacePath -Child "_observe\queue" -Parameter "-QueueRoot" }

$script:Result = [ordered]@{ action = $Action; ok = $false; runtime = $PSVersionTable.PSVersion.ToString(); maxWidth = $MaxWidth }

# The last refusal this process made, or $null if it has not refused anything.
#
# EVIDENCE, not decoration (R46 finding 3). `Fail` ends in `exit`, and `exit` unwinds through
# every enclosing `finally` without giving it any way to ask what happened -- so the queue's
# abort record used to NAME a gate refusal it had not observed, on an unwind that is far more
# often a Ctrl-C. This is the observation that makes the difference sayable: set on the way out
# of the only function that refuses, cleared by the queue before each batch, and read by
# Get-BatchAbortRecord, which composes one sentence when it is set and a different one when it is
# not. Nothing else may claim a cause for an abort.
$script:LastRefusal = $null

function Fail {
  param([string]$Message, [int]$Code = 1)
  $script:LastRefusal = [pscustomobject]@{ Message = $Message; Code = $Code; At = (Get-Date) }
  [Console]::Error.WriteLine("observe: $Message")
  if ($Json) {
    $script:Result.ok = $false
    $script:Result.error = $Message
    $script:Result.exitCode = $Code
    # [Console]::Out, not Write-Output: Fail and Finish are called from inside functions whose
    # own output is piped to Out-Null (Enter-InputMode), and Write-Output there lands in that
    # pipeline and is discarded -- measured: -Json printed nothing on any refusal path.
    [Console]::Out.WriteLine((ConvertTo-Json -InputObject $script:Result -Compress -Depth 6))
  }
  exit $Code
}

function Say { param([string]$Message) if ($Json) { [Console]::Error.WriteLine($Message) } else { Write-Host $Message } }

# Is this desktop session showing the lock screen?
#
# This is not a nicety. Measured on this machine while building the tool: with the workstation
# locked, EVERY screen capture returns the Windows PIN prompt -- the game is running underneath
# and none of it is on the glass -- and the first version of the blank check PASSED that capture
# (stddev 6.07 > 2.0) because the "Enter your PIN" text supplied enough variance. Worse, an input
# batch sent in that state goes to the CREDENTIAL PROMPT. Both paths refuse when this is true.
#
# LogonUI.exe exists exactly while the lock screen / credential UI is up for a session and exits
# on unlock. Filtered to OUR session id, so another user's locked session on the same machine
# does not make this one look locked. Note that OpenInputDesktop is NOT a usable signal here: it
# returned the 'Default' desktop on Windows 11 with the machine plainly locked.
#
# -PretendLocked is OR-ed in, never AND-ed. It can force this function to say LOCKED when the
# machine is not; it cannot make it say unlocked when the machine is. Every gate downstream reads
# more refusal out of a true here, which is why a test hook in this direction is safe.
#
# THIS GUARD FAILS CLOSED (R44 finding 15). It used to end `catch { return $false }`, so any
# failure to enumerate processes was reported as UNLOCKED -- the one direction this particular
# guard must never fail in. Evidence 174's rule is that reading the screen is safe while locked
# and writing input is not; a writing guard whose unknown answer is "safe to write" has inverted
# exactly that asymmetry. An unknown answer costs a round trip; the other direction costs a
# password typed into a chat box. So: cannot tell => LOCKED, and the reason is carried out with
# it so no caller has to invent one.

# The state the last Test-SessionLocked call actually measured, so callers can say what they know
# instead of asserting LogonUI when they never saw it:
#   pretend    -PretendLocked forced it
#   logonui    LogonUI was measured running in this session
#   clear      the probe ran and measured no LogonUI in this session
#   unreadable the probe could not answer -- treated as LOCKED
$script:LockState = "unknown"
$script:LockDetail = "Test-SessionLocked has not run yet"

# The fallible half, isolated in a scriptblock so `selftest -Offline` can substitute one that
# throws and PROVE the guard above fails closed, without breaking the machine's process table.
# Contract: return $true (locked), $false (not locked), or throw / return nothing for "I cannot
# tell". It must never turn a failure into $false -- Test-SessionLocked is what turns "cannot
# tell" into refusal, and it can only do that if this half reports the failure honestly.
$script:LockProbe = {
  $mySession = (Get-Process -Id $PID -ErrorAction Stop).SessionId
  $probeErrors = $null
  $logon = @(Get-Process -Name LogonUI -ErrorAction SilentlyContinue -ErrorVariable probeErrors)
  if ($probeErrors) {
    foreach ($e in $probeErrors) {
      # "no process with that name" IS the ordinary unlocked answer, not a failure. Anything else
      # means the enumeration itself did not work, which is not an answer -- and swallowing it
      # under -ErrorAction SilentlyContinue was a second way to fail open, below the catch.
      if ($e.FullyQualifiedErrorId -notlike "NoProcessFoundForGivenName*") {
        throw "could not enumerate LogonUI: $($e.Exception.Message)"
      }
    }
  }
  $mine = @($logon | Where-Object { $_.SessionId -eq $mySession })
  return [bool]($mine.Count -gt 0)
}

function Test-SessionLocked {
  if ($PretendLocked) {
    $script:LockState = "pretend"; $script:LockDetail = "-PretendLocked was passed"
    return $true
  }
  $state = $null
  try { $state = & $script:LockProbe }
  catch {
    $script:LockState = "unreadable"; $script:LockDetail = $_.Exception.Message
    return $true
  }
  if ($null -eq $state) {
    $script:LockState = "unreadable"; $script:LockDetail = "the lock probe returned no answer"
    return $true
  }
  if ($state) {
    $script:LockState = "logonui"; $script:LockDetail = "LogonUI is running in this session"
    return $true
  }
  $script:LockState = "clear"; $script:LockDetail = "no LogonUI is running in this session"
  return $false
}

# One sentence naming what Test-SessionLocked actually measured, for a refusal message. Never
# asserts LogonUI on a run that did not see it.
function Get-LockReason {
  switch ($script:LockState) {
    "pretend"    { return "-PretendLocked was passed, which forces this guard to answer LOCKED" }
    "logonui"    { return "the workstation is LOCKED (LogonUI is running in this session)" }
    "unreadable" { return ("the workstation's lock state COULD NOT BE READ ($script:LockDetail), and this " +
                           "guard fails closed: an unknown lock state is treated as LOCKED") }
    default      { return "the workstation is LOCKED" }
  }
}

function Finish {
  param([int]$Code = 0)
  if ($Json) {
    $script:Result.ok = ($Code -eq 0)
    $script:Result.exitCode = $Code
    # [Console]::Out, not Write-Output: Fail and Finish are called from inside functions whose
    # own output is piped to Out-Null (Enter-InputMode), and Write-Output there lands in that
    # pipeline and is discarded -- measured: -Json printed nothing on any refusal path.
    [Console]::Out.WriteLine((ConvertTo-Json -InputObject $script:Result -Compress -Depth 6))
  }
  exit $Code
}

# ---------------------------------------------------------------------------------------------
# Interop. The C# lives in tools/observe/Observe.Native.cs, not in a here-string: a here-string
# is undiffable and this repo has already paid for PowerShell escaping mistakes. C# 5 only --
# Add-Type on Windows PowerShell 5.1 is the CodeDom compiler.
# ---------------------------------------------------------------------------------------------
if (-not ("Progenitor.Observe.Native" -as [type])) {
  $nativeSource = Join-Path $PSScriptRoot "observe\Observe.Native.cs"
  if (-not (Test-Path $nativeSource)) { Fail "interop source missing: $nativeSource" 1 }
  Add-Type -AssemblyName System.Drawing | Out-Null
  Add-Type -TypeDefinition ([System.IO.File]::ReadAllText($nativeSource)) `
           -ReferencedAssemblies "System.Drawing", "System.Core" | Out-Null
}
$script:DpiMode = [Progenitor.Observe.Windows]::MakeDpiAware()

# Send a queued SendInput batch, turning a blocked write into an "observe: ..." line and a
# non-zero exit instead of a red PowerShell exception. Callers must never call Send() directly.
function Send-Batch {
  param($Injector)
  try { return $Injector.Send() }
  catch { Fail $_.Exception.Message 1 }
}

# ---------------------------------------------------------------------------------------------
# Key names -> virtual key codes.
# ---------------------------------------------------------------------------------------------
$script:Vk = @{
  "ctrl" = 0xA2; "control" = 0xA2; "lctrl" = 0xA2; "rctrl" = 0xA3
  "shift" = 0xA0; "lshift" = 0xA0; "rshift" = 0xA1
  "alt" = 0xA4; "lalt" = 0xA4; "ralt" = 0xA5; "menu" = 0xA4
  "win" = 0x5B; "lwin" = 0x5B; "rwin" = 0x5C; "apps" = 0x5D
  "esc" = 0x1B; "escape" = 0x1B
  "enter" = 0x0D; "return" = 0x0D
  "space" = 0x20; "spacebar" = 0x20
  "tab" = 0x09; "back" = 0x08; "backspace" = 0x08
  "del" = 0x2E; "delete" = 0x2E; "ins" = 0x2D; "insert" = 0x2D
  "home" = 0x24; "end" = 0x23
  "pgup" = 0x21; "pageup" = 0x21; "pgdn" = 0x22; "pagedown" = 0x22
  "up" = 0x26; "down" = 0x28; "left" = 0x25; "right" = 0x27
  "capslock" = 0x14; "numlock" = 0x90; "scrolllock" = 0x91
  "prtsc" = 0x2C; "printscreen" = 0x2C; "pause" = 0x13
  "tilde" = 0xC0; "grave" = 0xC0; "backtick" = 0xC0
  "minus" = 0xBD; "equals" = 0xBB; "plus" = 0xBB
  "lbracket" = 0xDB; "rbracket" = 0xDD; "backslash" = 0xDC
  "semicolon" = 0xBA; "quote" = 0xDE; "apostrophe" = 0xDE
  "comma" = 0xBC; "period" = 0xBE; "dot" = 0xBE; "slash" = 0xBF
}
for ($i = 1; $i -le 24; $i++) { $script:Vk["f$i"] = 0x6F + $i }
foreach ($c in [char[]]"abcdefghijklmnopqrstuvwxyz") { $script:Vk["$c"] = [int][char]([string]$c).ToUpperInvariant() }
for ($i = 0; $i -le 9; $i++) { $script:Vk["$i"] = 0x30 + $i; $script:Vk["numpad$i"] = 0x60 + $i }
$script:ModifierVks = @(0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C)

function Resolve-KeyName {
  param([string]$Name)
  $key = $Name.Trim().ToLowerInvariant()
  # There used to be an `if ($key -eq "") { $key = "plus" }` here, to make "Ctrl++" work. It never
  # did (R44 finding 14): "Ctrl++".Split('+') is THREE tokens, @("Ctrl", "", ""), so the second
  # empty hit "names more than one non-modifier key" and the plus key was unsendable in every
  # spelling that contains a '+'. The '+' is now resolved by Split-ChordTokens, which knows where
  # a separator ends and a literal key begins; an empty name reaching here is a real error.
  if ($key -eq "") { Fail "empty key name in a chord" 1 }
  if ($script:Vk.ContainsKey($key)) { return [int]$script:Vk[$key] }
  Fail "unknown key name '$Name'. Known: f1-f24, a-z, 0-9, numpad0-9, ctrl/shift/alt/win, esc, enter, space, tab, backspace, delete, insert, home, end, pgup, pgdn, arrows, tilde, minus, equals, brackets, semicolon, quote, comma, period, slash." 1
}

# Split a chord on '+' where '+' is a SEPARATOR, and keep it where it is the plus KEY.
#
# `.Split('+')` cannot tell those apart, which is R44 finding 14: "Ctrl++" splits to three tokens
# @("Ctrl", "", ""), "+" to two @("", ""), "++" to three. Every one of them mapped two empties to
# the plus key and died on "names more than one non-modifier key", so no spelling containing a '+'
# could send the plus key -- in the one case the code had a comment explaining.
#
# The rule, one line: a '+' with a name accumulated before it is the SEPARATOR that ends that name;
# a '+' with nothing before it is the PLUS KEY ITSELF.
#
#   "Ctrl+F9"    -> Ctrl, F9        (separator, then a name)
#   "Ctrl++"     -> Ctrl, plus      (separator, then the key)
#   "+"          -> plus
#   "Ctrl+Shift++" -> Ctrl, Shift, plus
#   "++"         -> plus, plus      -> Parse-Chord refuses it as two non-modifier keys, truthfully
#   "Ctrl+"      -> Ctrl            -> Parse-Chord refuses it as modifiers with no key. A trailing
#                                      separator is a typo and is now said so instead of guessed at.
function Split-ChordTokens {
  param([string]$Spec)
  $tokens = New-Object System.Collections.ArrayList
  $acc = ""
  foreach ($ch in [char[]]$Spec) {
    if ($ch -ne '+') { $acc += $ch; continue }
    if ($acc.Trim() -eq "") { [void]$tokens.Add("plus") } else { [void]$tokens.Add($acc.Trim()) }
    $acc = ""
  }
  if ($acc.Trim() -ne "") { [void]$tokens.Add($acc.Trim()) }
  return @($tokens)
}

# "Ctrl+Shift+F9" -> @{ Modifiers = @(0xA2,0xA0); Key = 0x78; Text = "Ctrl+Shift+F9" }
function Parse-Chord {
  param([string]$Spec)
  # @(...) is load-bearing: a function returning a one-element array is unrolled to the scalar on
  # the way out, and $parts.Length on a bare string is its CHARACTER count -- "F10" then parsed as
  # three keys. Measured, the first time this ran.
  $parts = @(Split-ChordTokens $Spec)
  if ($parts.Count -eq 0) { Fail "chord '$Spec' names no keys" 1 }
  $mods = @()
  $main = $null
  for ($i = 0; $i -lt $parts.Count; $i++) {
    $vk = Resolve-KeyName $parts[$i]
    $isMod = ($script:ModifierVks -contains $vk)
    if ($isMod -and $i -lt ($parts.Count - 1)) { $mods += $vk; continue }
    if ($null -ne $main) { Fail "chord '$Spec' names more than one non-modifier key; send them as a comma-separated sequence instead" 1 }
    $main = $vk
  }
  if ($null -eq $main) { Fail "chord '$Spec' has no key, only modifiers" 1 }
  return [pscustomobject]@{ Modifiers = $mods; Key = [int]$main; Text = $Spec.Trim() }
}

# ---------------------------------------------------------------------------------------------
# Target window resolution.
# ---------------------------------------------------------------------------------------------
function Get-ProcessPathSafe {
  param([int]$Id)
  try { return (Get-Process -Id $Id -ErrorAction Stop).Path } catch { return $null }
}

function Get-ProcessNameSafe {
  param([int]$Id)
  try { return (Get-Process -Id $Id -ErrorAction Stop).ProcessName } catch { return "<unknown>" }
}

# The message from the last -Optional resolution that came back $null.
$script:ResolveError = $null

# THE WINDOW FENCE (R46 finding 7).
#
# Every window this tool touches is reached through Resolve-Target: `client`, `foreground`,
# `hwnd:`, `proc:` and `title:` are the whole vocabulary, the subcommands take a resolved target
# object rather than a spec, and the queue resolves through Resolve-BatchTarget, which calls this
# and nothing else. So one gate at the top of this function is a gate on all of it.
#
# It exists for the offline self-test. That suite ran code paths that CAN resolve a window while
# the user is in the game, and what kept it off the client was that a write failed a few lines
# earlier -- an ordering, in a different function, that nobody was maintaining as a safety
# property. `Save-JsonFile` growing a "create the directory" line, which is an obvious
# improvement, would have quietly turned that check into one that resolves the running V Rising
# client. The fence replaces the ordering: while it is armed, this function reads no process
# list, calls no EnumWindows and returns no handle, whatever it is asked for, so "the offline
# suite touches no window" is a property of the code rather than of an accident downstream.
#
# It fails the way the caller asked it to fail: -Optional (which is what the queue uses) gets
# $null and a reason, exactly as an unresolvable spec does, so the code under test takes its
# normal refusal path and the check still measures something. Without -Optional it throws rather
# than calling Fail, because Fail exits and a self-test that kills its own process reports
# nothing (R45 finding 9's mechanism, one file over).
#
# $script:WindowFenceHits counts what the fence stopped, so a check can assert that the fence was
# REACHED rather than assuming the call it made would have gone that way.
$script:WindowFence = $null
$script:WindowFenceHits = 0

# -Optional softens the "it is not there" family (the exit-2 cases) into a $null return with the
# message in $script:ResolveError, for a caller whose job is to REPORT what is there rather than to
# act on it (R44 finding 19: `windows` printed its whole table and then exited 2 because the game
# was not running). It deliberately does NOT soften:
#   * the usage errors (exit 1) -- a misspelled -Window is still a misspelled -Window; and
#   * Assert-NotLiveServerPath, which is not routed through here at all and still throws, so the
#     refusal for a window whose process lives under the live server survives -Optional intact.
function Resolve-Target {
  param([string]$Spec, [switch]$Optional)

  $script:ResolveError = $null

  # The fence, before the first Get-Process and before the first native call. See its
  # declaration above: while it is armed nothing here reads a process list or a window.
  if ($script:WindowFence) {
    $script:WindowFenceHits++
    $m = "window resolution is fenced off ($script:WindowFence): '$Spec' was NOT resolved, no process list was read and no window was touched"
    $script:ResolveError = $m
    if ($Optional) { return $null }
    throw $m
  }

  $hwnd = [IntPtr]::Zero
  $how = $Spec

  if ($Spec -eq "client") {
    $procs = @(Get-Process -Name VRising -ErrorAction SilentlyContinue | Where-Object {
      $p = $null; try { $p = $_.Path } catch {}
      $p -and $p.StartsWith($GameDir, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($ProcessId -gt 0) { $procs = @($procs | Where-Object { $_.Id -eq $ProcessId }) }
    if ($procs.Count -eq 0) {
      $m = "no V Rising client process found under '$GameDir'. Start it with tools/launch-client.ps1, or pass -Window proc:<name> / -Window hwnd:<n>."
      if ($Optional) { $script:ResolveError = $m; return $null }
      Fail $m 2
    }
    if ($procs.Count -gt 1) {
      $m = "$($procs.Count) V Rising client processes are running (pids $($procs.Id -join ', ')); pass -ProcessId to choose one (tools/extra-client.ps1 starts extra instances)."
      if ($Optional) { $script:ResolveError = $m; return $null }
      Fail $m 2
    }
    $hwnd = $procs[0].MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) {
      $m = "the V Rising client (pid $($procs[0].Id)) has no main window yet -- it is probably still starting. Wait for the bridge (tools/launch-client.ps1) and retry."
      if ($Optional) { $script:ResolveError = $m; return $null }
      Fail $m 2
    }
    $how = "client pid $($procs[0].Id)"
  }
  elseif ($Spec -eq "foreground") {
    $hwnd = [Progenitor.Observe.Native]::GetForegroundWindow()
    if ($hwnd -eq [IntPtr]::Zero) {
      if ($Optional) { $script:ResolveError = "there is no foreground window"; return $null }
      Fail "there is no foreground window" 2
    }
    $how = "foreground window"
  }
  elseif ($Spec -like "hwnd:*") {
    $raw = $Spec.Substring(5).Trim()
    $n = 0
    if ($raw -like "0x*") { $n = [Convert]::ToInt64($raw.Substring(2), 16) }
    elseif (-not [Int64]::TryParse($raw, [ref]$n)) { Fail "'-Window $Spec' is not a number" 1 }
    $hwnd = [IntPtr]$n
    $how = "hwnd $raw"
  }
  elseif ($Spec -like "proc:*") {
    $name = $Spec.Substring(5).Trim()
    $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
    if ($procs.Count -eq 0) {
      if ($Optional) { $script:ResolveError = "no running process named '$name' has a main window"; return $null }
      Fail "no running process named '$name' has a main window" 2
    }
    $hwnd = $procs[0].MainWindowHandle
    $how = "process '$name' pid $($procs[0].Id)"
  }
  elseif ($Spec -like "title:*") {
    $pattern = $Spec.Substring(6)
    $rows = @([Progenitor.Observe.Windows]::Enumerate($true, $true) | Where-Object { $_.Title -match $pattern -and -not $_.Cloaked })
    if ($rows.Count -eq 0) {
      $m = "no visible window title matches '$pattern' (run 'observe.ps1 windows' to list them)"
      if ($Optional) { $script:ResolveError = $m; return $null }
      Fail $m 2
    }
    $hwnd = $rows[0].Hwnd
    $how = "title match '$pattern'"
  }
  else {
    Fail "unrecognised -Window '$Spec'. Use client, foreground, hwnd:<n>, proc:<name> or title:<regex>." 1
  }

  if (-not [Progenitor.Observe.Native]::IsWindow($hwnd)) {
    $m = "$how resolved to handle $hwnd, which is not a window"
    if ($Optional) { $script:ResolveError = $m; return $null }
    Fail $m 2
  }

  $pid32 = 0
  [void][Progenitor.Observe.Native]::GetWindowThreadProcessId($hwnd, [ref]$pid32)
  $path = Get-ProcessPathSafe ([int]$pid32)

  # Nothing in this repo talks to the live server, including through a window handle.
  if ($path) { Assert-NotLiveServerPath $path }

  $isGame = $false
  if ($path -and $path.StartsWith($GameDir, [System.StringComparison]::OrdinalIgnoreCase)) { $isGame = $true }

  $vis = [Progenitor.Observe.Windows]::VisibleBounds($hwnd)
  $cli = [Progenitor.Observe.Windows]::ClientBounds($hwnd)

  return [pscustomobject]@{
    Hwnd        = $hwnd
    Pid         = [int]$pid32
    ProcessName = (Get-ProcessNameSafe ([int]$pid32))
    ProcessPath = $path
    Title       = [Progenitor.Observe.Windows]::TextOf($hwnd)
    Class       = [Progenitor.Observe.Windows]::ClassOf($hwnd)
    IsGame      = $isGame
    Minimised   = [Progenitor.Observe.Native]::IsIconic($hwnd)
    Cloaked     = [Progenitor.Observe.Windows]::IsCloaked($hwnd)
    Foreground  = ([Progenitor.Observe.Native]::GetForegroundWindow() -eq $hwnd)
    WindowRect  = $vis
    ClientRect  = $cli
    How         = $how
  }
}

function Format-Rect {
  param($R)
  return "$($R.Left),$($R.Top) $($R.Right - $R.Left)x$($R.Bottom - $R.Top)"
}

function Test-OnScreen {
  param($R)
  $vx = [Progenitor.Observe.Native]::GetSystemMetrics(76)
  $vy = [Progenitor.Observe.Native]::GetSystemMetrics(77)
  $vw = [Progenitor.Observe.Native]::GetSystemMetrics(78)
  $vh = [Progenitor.Observe.Native]::GetSystemMetrics(79)
  # Any overlap at all counts as on-screen; a fully off-desktop window is the failure.
  return -not ($R.Right -le $vx -or $R.Bottom -le $vy -or $R.Left -ge ($vx + $vw) -or $R.Top -ge ($vy + $vh))
}

# ---------------------------------------------------------------------------------------------
# Coordinates.
# ---------------------------------------------------------------------------------------------
function Resolve-Point {
  param($Target, [Nullable[int]]$Px, [Nullable[int]]$Py, [switch]$Absolute)
  if ($null -eq $Px -or $null -eq $Py) { return $null }
  if ($Absolute) { return [pscustomobject]@{ X = [int]$Px; Y = [int]$Py; Origin = "screen" } }
  $c = $Target.ClientRect
  $sx = $c.Left + [int]$Px
  $sy = $c.Top + [int]$Py
  $w = $c.Right - $c.Left
  $h = $c.Bottom - $c.Top
  if ([int]$Px -lt 0 -or [int]$Py -lt 0 -or [int]$Px -ge $w -or [int]$Py -ge $h) {
    Fail "client-relative point ($Px,$Py) is outside the target's client area (${w}x${h}). Pass -ScreenCoords for absolute virtual-desktop coordinates." 1
  }
  return [pscustomobject]@{ X = $sx; Y = $sy; Origin = "client($Px,$Py)" }
}

# ---------------------------------------------------------------------------------------------
# The input gate. Everything that sends real input goes through here first.
# ---------------------------------------------------------------------------------------------
function Enter-InputMode {
  param($Target, [string]$What)

  # Before anything else, and with no override switch anywhere in this tool: a locked session
  # puts the credential prompt in front of every window, so a synthetic key press would be typed
  # into the PIN box. There is no legitimate reason to do that.
  if (-not $DryRun -and (Test-SessionLocked)) {
    Fail ("$(Get-LockReason). Synthetic input would go " +
          "to the Windows credential prompt, not to '$($Target.Title)'. Refusing. Unlock the machine " +
          "and run this again. (-DryRun still works while locked.)") 3
  }

  if ($Target.IsGame -and -not $AllowGameInput -and -not $DryRun) {
    Fail ("refusing to send input to the V Rising client (pid $($Target.Pid)). This drives the real " +
          "keyboard and mouse of a live session with a connected character in it. Pass -AllowGameInput " +
          "to mean it, or -DryRun to see the batch without sending it.") 3
  }

  if ($DryRun) {
    Say "DRY RUN -- nothing will be sent."
    return $true
  }

  Say "############ SENDING REAL INPUT ############"
  Say "  target : $($Target.Title) [$($Target.ProcessName) pid $($Target.Pid)] hwnd $($Target.Hwnd)"
  Say "  action : $What"
  Say "  This takes over the keyboard and mouse of this desktop session. Hands off until it finishes."

  if ($DelayMs -gt 0) {
    Say "  waiting $DelayMs ms before sending (-DelayMs)"
    Start-Sleep -Milliseconds $DelayMs
  }

  if ($NoFocus) {
    Say "  -NoFocus: NOT bringing the window forward. Input goes to whatever has focus right now."
  }
  else {
    $already = ([Progenitor.Observe.Native]::GetForegroundWindow() -eq $Target.Hwnd)
    if ($already) {
      Say "  focus  : target is already foreground; nothing stolen."
    }
    else {
      Say "  focus  : *** BRINGING THE TARGET WINDOW TO THE FOREGROUND -- STEALING FOCUS FROM THE USER ***"
      $ok = [Progenitor.Observe.Windows]::Focus($Target.Hwnd, 3000)
      if (-not $ok) {
        $fg = [Progenitor.Observe.Native]::GetForegroundWindow()
        Fail ("could not bring the target to the foreground within 3 s (foreground is still hwnd $fg, " +
              "'$([Progenitor.Observe.Windows]::TextOf($fg))'). NOT sending input: it would have gone " +
              "to that window instead. A full-screen exclusive app or an elevated window will do this.") 4
      }
      Say "  focus  : target is foreground."
    }
    if ($SettleMs -gt 0) { Start-Sleep -Milliseconds $SettleMs }
    # Re-check after the settle: a focus that bounced back is the case that would silently type
    # into the user's window, and it is exactly what happens when a launcher steals focus back.
    if ([Progenitor.Observe.Native]::GetForegroundWindow() -ne $Target.Hwnd) {
      Fail "focus bounced away from the target during the $SettleMs ms settle; NOT sending input." 4
    }
  }
  return $true
}

function Complete-Input {
  param($Injector, [string]$Summary)
  $steps = @($Injector.Steps)
  if ($DryRun) {
    # $Injector.Count is the real INPUT record count; $steps is the readable summary, and one
    # step can be two records (a typed character is a down and an up).
    Say "would send $($Injector.Count) SendInput record(s) in $($steps.Count) step(s):"
    foreach ($s in $steps) { Say ("  {0,-14} {1}" -f $s.Kind, $s.Detail) }
    $script:Result.dryRun = $true
    $script:Result.events = @($steps | ForEach-Object { "$($_.Kind) $($_.Detail)" })
    $Injector.Clear()
    Say "sent nothing (-DryRun)."
    return
  }
  $sent = Send-Batch $Injector
  $script:Result.eventsSent = $sent
  $script:Result.events = @($steps | ForEach-Object { "$($_.Kind) $($_.Detail)" })
  Say "sent $sent event(s): $Summary"
}

# ---------------------------------------------------------------------------------------------
# Subcommands.
# ---------------------------------------------------------------------------------------------

function Invoke-Windows {
  $rows = @([Progenitor.Observe.Windows]::Enumerate($true, $true))
  Say "dpi awareness: $script:DpiMode"
  $locked = Test-SessionLocked
  $script:Result.sessionLocked = $locked
  $script:Result.sessionLockState = $script:LockState
  if ($locked -and $script:LockState -eq "unreadable") {
    # Not "locked", which we did not measure, and emphatically not "unlocked", which is what this
    # line used to print for a lock probe that failed (R44 finding 15).
    Say "session:       *** LOCK STATE UNREADABLE *** ($script:LockDetail)"
    Say "               Treated as LOCKED: input is refused outright, Screen captures refuse."
  }
  elseif ($locked) {
    Say "session:       *** LOCKED *** ($script:LockDetail). Screen captures return the lock screen;"
    Say "               input is refused outright. Unlock the machine before driving anything."
  }
  else { Say "session:       unlocked (measured: $script:LockDetail)" }
  $vx = [Progenitor.Observe.Native]::GetSystemMetrics(76)
  $vy = [Progenitor.Observe.Native]::GetSystemMetrics(77)
  $vw = [Progenitor.Observe.Native]::GetSystemMetrics(78)
  $vh = [Progenitor.Observe.Native]::GetSystemMetrics(79)
  Say "virtual desktop: origin $vx,$vy size ${vw}x${vh}"
  Say ""
  $table = foreach ($r in $rows) {
    if ($r.Cloaked) { continue }
    [pscustomobject]@{
      Hwnd  = "0x{0:X}" -f [int64]$r.Hwnd
      Pid   = $r.ProcessId
      Proc  = (Get-ProcessNameSafe $r.ProcessId)
      Rect  = "$($r.Left),$($r.Top) $($r.Width)x$($r.Height)"
      Flags = (@(
        $(if ($r.Foreground) { "FOREGROUND" })
        $(if ($r.Minimised) { "minimised" })
      ) | Where-Object { $_ }) -join " "
      Title = $r.Title
    }
  }
  $table | Format-Table -AutoSize | Out-String -Width 240 | Write-Host

  # Then the detail for whatever -Window resolves to, which is the thing the caller will act on.
  #
  # -Optional, and the whole command still exits 0 if it does not resolve (R44 finding 19). This
  # subcommand's job is to LIST what is there. It used to print the table above in full and then
  # exit 2 because the default -Window client could not be resolved, so "the game is not running"
  # -- an ordinary, expected state, and one of the things a caller runs `windows` to find out --
  # came back as a failure to whatever was calling it. A misspelled -Window is still exit 1.
  $t = Resolve-Target $Window -Optional
  if ($null -eq $t) {
    Say "target (-Window $Window) -> NOT RESOLVED: $script:ResolveError"
    Say "                             The window list above is still the answer; this is a listing"
    Say "                             command, so an absent target is a line, not an exit code."
    $script:Result.targetResolved = $false
    $script:Result.targetError = $script:ResolveError
    Finish 0
  }
  $script:Result.targetResolved = $true
  Say "target (-Window $Window) -> $($t.How)"
  Say ("  hwnd        0x{0:X}  pid {1}  {2}" -f [int64]$t.Hwnd, $t.Pid, $t.ProcessName)
  Say "  title       '$($t.Title)'   class '$($t.Class)'"
  Say "  window rect $(Format-Rect $t.WindowRect)"
  Say "  client rect $(Format-Rect $t.ClientRect)"
  Say "  minimised   $($t.Minimised)   foreground $($t.Foreground)   is-the-game $($t.IsGame)"
  $covered = 0; $total = 0
  $blockers = [Progenitor.Observe.Windows]::OcclusionReport($t.Hwnd, $t.WindowRect, 12, [ref]$covered, [ref]$total)
  if ($total -gt 0) {
    $pct = [math]::Round(100.0 * $covered / $total, 1)
    if ($covered -eq 0) { Say "  occlusion   none (0 of $total probe points covered)" }
    else { Say "  occlusion   $pct% of probe points covered by: $blockers" }
  }
  else {
    # OcclusionReport returns an empty report with total 0 when it could not run the probe at all
    # -- an empty rectangle, or a Z-order walk that never reached the target because it is not a
    # top-level window. "none" would be a claim; this is the measurement that was not taken.
    Say "  occlusion   unmeasured (no probe points; the target may not be a top-level window)"
  }
  $script:Result.target = @{
    hwnd = "0x{0:X}" -f [int64]$t.Hwnd; pid = $t.Pid; process = $t.ProcessName; title = $t.Title
    windowRect = (Format-Rect $t.WindowRect); clientRect = (Format-Rect $t.ClientRect)
    minimised = $t.Minimised; foreground = $t.Foreground; isGame = $t.IsGame
    occludedPoints = $covered; probePoints = $total; blockers = $blockers
  }
  $script:Result.windows = @($table | ForEach-Object { "$($_.Hwnd) $($_.Pid) $($_.Proc) $($_.Rect) $($_.Title)" })
  Finish 0
}

# Capture analysis uses the original frame. This helper only bounds the delivered image and
# never captures a window, changes focus or alters the source bitmap. Caller owns its result.
function Resize-CaptureBitmap {
  param([System.Drawing.Bitmap]$Bitmap, [int]$WidthLimit)
  if ($WidthLimit -lt 0 -or $WidthLimit -gt 16384) { throw "Width limit must be between 0 and 16384." }
  if ($WidthLimit -eq 0 -or $Bitmap.Width -le $WidthLimit) { return $Bitmap }
  $height = [Math]::Max(1, [int][Math]::Round($Bitmap.Height * ($WidthLimit / [double]$Bitmap.Width)))
  $resized = New-Object System.Drawing.Bitmap $WidthLimit, $height
  $graphics = $null
  try {
    $graphics = [System.Drawing.Graphics]::FromImage($resized)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($Bitmap, 0, 0, $WidthLimit, $height)
    return $resized
  }
  catch { $resized.Dispose(); throw }
  finally { if ($graphics) { $graphics.Dispose() } }
}
function Invoke-Shot {
  $t = Resolve-Target $Window

  if (-not $Out) {
    $dir = Resolve-ProgenitorWorkspacePath -Child "_observe" -Parameter "-Out"
    $Out = Join-Path $dir ("shot-" + (Get-Date -Format "yyyyMMdd-HHmmss-fff") + ".png")
  }
  # A caller-supplied output path is a root like any other: through the fence before any write.
  Assert-NotLiveServerPath $Out
  $outDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($Out))
  if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

  # Which rectangle.
  $rect = $null
  switch ($Region) {
    "window"  { $rect = $t.WindowRect }
    "client"  { $rect = $t.ClientRect }
    "monitor" {
      Add-Type -AssemblyName System.Windows.Forms | Out-Null
      $mid = [System.Drawing.Point]::new($t.WindowRect.Left + 1, $t.WindowRect.Top + 1)
      $scr = [System.Windows.Forms.Screen]::FromPoint($mid)
      $rect = New-Object Progenitor.Observe.RECT
      $rect.Left = $scr.Bounds.Left; $rect.Top = $scr.Bounds.Top
      $rect.Right = $scr.Bounds.Right; $rect.Bottom = $scr.Bounds.Bottom
    }
    "virtual" {
      $rect = New-Object Progenitor.Observe.RECT
      $rect.Left = [Progenitor.Observe.Native]::GetSystemMetrics(76)
      $rect.Top = [Progenitor.Observe.Native]::GetSystemMetrics(77)
      $rect.Right = $rect.Left + [Progenitor.Observe.Native]::GetSystemMetrics(78)
      $rect.Bottom = $rect.Top + [Progenitor.Observe.Native]::GetSystemMetrics(79)
    }
  }
  $w = $rect.Right - $rect.Left
  $h = $rect.Bottom - $rect.Top
  if ($w -le 0 -or $h -le 0) { Fail "target rectangle is empty ($(Format-Rect $rect)) -- the window has no visible area" 4 }

  Say ("target      {0}: '{1}' hwnd 0x{2:X} pid {3}" -f $t.How, $t.Title, [int64]$t.Hwnd, $t.Pid)
  Say "region      $Region $(Format-Rect $rect)  method $Method  dpi $script:DpiMode"
  if ($Method -eq "PrintWindow" -and ($Region -eq "monitor" -or $Region -eq "virtual")) {
    Fail "-Method PrintWindow asks one window to redraw itself; it cannot capture -Region $Region. Use -Method Screen." 1
  }

  # Auto resolves to PrintWindow where PrintWindow can work at all, and falls back to Screen if
  # the image it returns is blank. The direction matters: falling back TO PrintWindow would hide
  # a black frame, whereas falling back FROM it is gated by the blank check and every step is
  # printed. -Region monitor/virtual is not one window, so it is always Screen.
  $effective = $Method
  if ($Method -eq "Auto") {
    if ($Region -eq "monitor" -or $Region -eq "virtual") { $effective = "Screen" }
    else { $effective = "PrintWindow" }
  }

  $bmp = $null
  $stats = $null

  if ($effective -eq "PrintWindow") {
    # No lock, minimize, off-screen or occlusion guard here: PrintWindow asks the WINDOW to
    # render, so none of those conditions apply to it. Measured on the V Rising client while
    # another window covered 66% of it -- a full, correct game frame came back.
    try {
      $bmp = [Progenitor.Observe.Capture]::FromPrintWindow($t.Hwnd, $rect)
      $stats = [Progenitor.Observe.Capture]::Analyse($bmp, 4)
    }
    catch {
      # Analyse() can throw with $bmp already assigned (LockBits on a bitmap the capture left in a
      # bad state), and this used to drop the reference without disposing it -- the outer finally
      # cannot dispose what it has just been told is $null (R44 finding 20).
      if ($bmp) { try { $bmp.Dispose() } catch { } }
      $bmp = $null; $stats = $null
      if ($Method -eq "Auto") { Say "method      PrintWindow failed ($($_.Exception.Message)); falling back to -Method Screen" }
      else { Fail $_.Exception.Message 1 }
    }
    if ($bmp -and $Method -eq "Auto") {
      $pwBlank = ($stats.StdDev -lt $MinStdDev) -or ($stats.TopColourShare -ge $MaxDominantShare)
      if ($pwBlank) {
        Say ("method      PrintWindow returned a near-uniform image (dominant {0} at {1:P1}) -- the " -f $stats.TopColour, $stats.TopColourShare)
        Say "            classic D3D/DXGI swapchain result. Falling back to -Method Screen."
        $bmp.Dispose(); $bmp = $null; $stats = $null
      }
      else { Say "method      PrintWindow(PW_RENDERFULLCONTENT) -- no focus taken, occlusion irrelevant" }
    }
    elseif ($bmp) { Say "method      PrintWindow(PW_RENDERFULLCONTENT) -- no focus taken, occlusion irrelevant" }
    if (-not $bmp) { $effective = "Screen" }
  }

  if ($effective -eq "Screen" -and (Test-SessionLocked) -and -not $AllowLockedSession) {
    Fail ("$(Get-LockReason). What is on the glass is probably the " +
          "Windows lock/PIN screen, so a CopyFromScreen capture would be a picture of that, not of " +
          "'$($t.Title)' -- the game is running underneath and compositing nothing to the desktop. " +
          "Unlock the machine, or use -Method PrintWindow, which asks the window to render and does " +
          "not read the glass at all. Pass -AllowLockedSession to capture the lock screen " +
          "deliberately (that is how this failure mode was found).") 4
  }

  # Which readiness checks actually RAN. The blank-failure message below used to name a cause it
  # had not diagnosed (R44 finding 18); it now says only what was measured, and these are the
  # measurements. The Screen path runs them; the PrintWindow path skips them by construction,
  # which is exactly why it must not go on to assert why an image came back flat.
  $guardsRun = @()

  if ($effective -eq "Screen" -and $Region -ne "virtual") {
    if ($t.Minimised) {
      Fail ("the target window is MINIMISED, so its pixels are not on screen and a CopyFromScreen " +
            "capture would be of whatever is behind it. Restore the window, or pass -Method PrintWindow " +
            "(which for a D3D/DXGI game usually returns solid black -- see the tool header).") 4
    }
    $guardsRun += "minimised"
    if (-not (Test-OnScreen $rect)) {
      Fail "the target window is entirely off the virtual desktop ($(Format-Rect $rect)); there is nothing on screen to capture." 4
    }
    $guardsRun += "off-screen"
    if ($Region -ne "monitor") {
      $covered = 0; $total = 0
      $blockers = [Progenitor.Observe.Windows]::OcclusionReport($t.Hwnd, $rect, 12, [ref]$covered, [ref]$total)
      if ($covered -gt 0) {
        $pct = [math]::Round(100.0 * $covered / $total, 1)
        if ($AllowOccluded) {
          Say "occlusion   WARNING: $pct% of probe points are covered by: $blockers (-AllowOccluded, capturing anyway)"
        }
        else {
          Fail ("$pct% of the target window is covered by another window ($blockers). A CopyFromScreen " +
                "capture would contain that window, not the game. Move it, or pass -AllowOccluded to " +
                "capture anyway, or -Method PrintWindow.") 4
        }
      }
      elseif ($total -gt 0) { Say "occlusion   none (0 of $total probe points covered)" }
      else {
        # total 0 means the probe did not run -- an empty rectangle, or a Z-order walk that never
        # reached the target (Observe.Native.cs deliberately returns an empty report rather than
        # claiming total occlusion). "none ($total probe points clear)" reported a result from zero
        # samples; this reports the absence of the measurement instead (R44 finding 18).
        Say "occlusion   unmeasured (no probe points; the target may not be a top-level window)"
      }
      if ($total -gt 0) { $guardsRun += "occlusion" }
    }
  }

  try {
    if (-not $bmp) {
      $bmp = [Progenitor.Observe.Capture]::FromScreen($rect)
      $stats = [Progenitor.Observe.Capture]::Analyse($bmp, 4)
    }

    $script:Result.method = $effective
    $blank = ($stats.StdDev -lt $MinStdDev) -or ($stats.TopColourShare -ge $MaxDominantShare)
    $line = ("pixels      mean {0:F1}  stddev {1:F2}  colours {2}  dominant {3} at {4:P1} of {5} samples" -f `
      $stats.Mean, $stats.StdDev, $stats.UniqueColours, $stats.TopColour, $stats.TopColourShare, $stats.Sampled)
    Say $line

    $script:Result.sourceWidth = $bmp.Width
    $script:Result.sourceHeight = $bmp.Height
    $outputBitmap = Resize-CaptureBitmap -Bitmap $bmp -WidthLimit $MaxWidth
    if (-not [object]::ReferenceEquals($bmp, $outputBitmap)) {
      $bmp.Dispose()
      $bmp = $outputBitmap
      Say "resize      $($script:Result.sourceWidth)x$($script:Result.sourceHeight) -> $($bmp.Width)x$($bmp.Height) (original frame used for blank checks)"
    }
    $script:Result.file = [System.IO.Path]::GetFullPath($Out)
    $script:Result.width = $bmp.Width
    $script:Result.height = $bmp.Height
    $script:Result.mean = [math]::Round($stats.Mean, 2)
    $script:Result.stdDev = [math]::Round($stats.StdDev, 3)
    $script:Result.uniqueColours = $stats.UniqueColours
    $script:Result.dominantColour = $stats.TopColour
    $script:Result.blank = $blank

    if ($blank -and -not $NoBlankCheck) {
      # Still write the file: a blank PNG is evidence about WHAT went wrong, and deleting it
      # would make the failure harder to diagnose, not easier.
      $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
      # Say which HALF of the check fired, and say what was and was not measured. This message used
      # to assert "PrintWindow could not read this window's swapchain" whenever the effective method
      # was PrintWindow -- a diagnosis the code had not made, on the one path where every guard that
      # could have made it was skipped by construction (R44 finding 18).
      $half = @()
      if ($stats.StdDev -lt $MinStdDev) { $half += ("flat -- stddev {0:F2} is under the {1} floor" -f $stats.StdDev, $MinStdDev) }
      if ($stats.TopColourShare -ge $MaxDominantShare) { $half += ("concentrated -- {0} covers {1:P1}, at or over the {2:P0} ceiling" -f $stats.TopColour, $stats.TopColourShare, $MaxDominantShare) }
      if ($effective -eq "PrintWindow") {
        $why = ("The cause was NOT diagnosed: PrintWindow does not read the desktop, so no minimise, " +
                "off-screen or occlusion check ran before this capture. A window that renders nothing " +
                "to GDI gives exactly this, and so does a window genuinely showing one flat colour; " +
                "-Method Screen reads the glass and its guards would tell those apart")
      }
      elseif ($guardsRun.Count -gt 0) {
        $why = ("The Screen path's $($guardsRun -join ', ') check(s) passed before this capture, so " +
                "this flat image is what was on the glass")
      }
      else {
        $why = ("The cause was NOT diagnosed: no readiness check runs for -Region $Region, so nothing " +
                "here measured whether the pixels were reachable")
      }
      $msg = ("the capture is near-uniform ($($half -join '; ')): stddev {0:F2} (floor {1}), dominant colour {2} covers {3:P1} of it (ceiling {4:P0}). " -f $stats.StdDev, $MinStdDev, $stats.TopColour, $stats.TopColourShare, $MaxDominantShare)
      $msg += "This is the silent-black-PNG failure, reported instead of hidden. $why. "
      $msg += "The image was still written to $Out so you can look at it. Pass -NoBlankCheck to accept it."
      Fail $msg 5
    }

    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  }
  finally {
    if ($bmp) { $bmp.Dispose() }
  }

  $size = (Get-Item $Out).Length
  Say "wrote       $([System.IO.Path]::GetFullPath($Out))  ($($script:Result.width)x$($script:Result.height), $([int]($size/1KB)) KB)"
  Finish 0
}

function Invoke-Key {
  if (-not $Value) { Fail "usage: observe.ps1 key <chord>[,<chord>...]   e.g. F10   or   'Ctrl+F9'   or   'F10,Escape'" 1 }
  $t = Resolve-Target $Window
  $chords = @($Value.Split(',') | Where-Object { $_.Trim() -ne "" } | ForEach-Object { Parse-Chord $_.Trim() })
  $summary = ($chords | ForEach-Object { $_.Text }) -join ", "
  $summary = "$summary x$RepeatCount"
  Enter-InputMode $t "key $summary" | Out-Null

  $inj = New-Object Progenitor.Observe.Injector
  for ($rep = 0; $rep -lt $RepeatCount; $rep++) {
    foreach ($chord in $chords) {
      foreach ($m in $chord.Modifiers) { $inj.KeyDown([uint16]$m) }
      $inj.KeyDown([uint16]$chord.Key)
      if (-not $DryRun) { Send-Batch $inj | Out-Null; Start-Sleep -Milliseconds $HoldMs }
      $inj.KeyUp([uint16]$chord.Key)
      $rev = @($chord.Modifiers); [array]::Reverse($rev)
      foreach ($m in $rev) { $inj.KeyUp([uint16]$m) }
      if (-not $DryRun) { Send-Batch $inj | Out-Null; Start-Sleep -Milliseconds $GapMs }
    }
  }
  if ($DryRun) { Complete-Input $inj $summary }
  else {
    $script:Result.events = @($inj.Steps | ForEach-Object { "$($_.Kind) $($_.Detail)" })
    Say "sent $summary as $($inj.Steps.Count) SendInput event(s), hold ${HoldMs} ms, gap ${GapMs} ms"
  }
  Finish 0
}

function Invoke-Type {
  # NOT $PSBoundParameters: inside a function that is the FUNCTION's bound parameters, which are
  # empty here, so the guard fired on every call. An empty string is a legal (no-op) argument, so
  # the test is against $null, not against emptiness.
  if ($null -eq $Value) { Fail "usage: observe.ps1 type '<text>'" 1 }
  $t = Resolve-Target $Window
  Enter-InputMode $t "type $($Value.Length) character(s)" | Out-Null
  $inj = New-Object Progenitor.Observe.Injector
  foreach ($ch in [char[]]$Value) { $inj.Char($ch) }
  Complete-Input $inj "typed '$Value' (KEYEVENTF_UNICODE, layout-independent)"
  Finish 0
}

function Invoke-Move {
  $t = Resolve-Target $Window
  $pt = Resolve-Point $t $X $Y -Absolute:$ScreenCoords
  if ($null -eq $pt -and $Dx -eq 0 -and $Dy -eq 0) {
    Fail "usage: observe.ps1 move -X <n> -Y <n>   (client-relative; add -ScreenCoords for absolute)   or   observe.ps1 move -Dx <n> -Dy <n>   (relative delta, the only kind a raw-input camera reacts to)" 1
  }
  $what = ""
  if ($pt) { $what = "move to $($pt.X),$($pt.Y) [$($pt.Origin)]" } else { $what = "move by $Dx,$Dy (relative)" }
  Enter-InputMode $t $what | Out-Null
  $inj = New-Object Progenitor.Observe.Injector
  if ($pt) { $inj.MoveAbsolute($pt.X, $pt.Y) } else { $inj.MoveRelative($Dx, $Dy) }
  Complete-Input $inj $what
  Finish 0
}

function Invoke-Click {
  $t = Resolve-Target $Window
  $pt = Resolve-Point $t $X $Y -Absolute:$ScreenCoords
  $what = "$Button click x$Clicks"
  if ($pt) { $what = "$what at $($pt.X),$($pt.Y) [$($pt.Origin)]" } else { $what = "$what at the current pointer position" }
  Enter-InputMode $t $what | Out-Null
  $inj = New-Object Progenitor.Observe.Injector
  if ($pt) { $inj.MoveAbsolute($pt.X, $pt.Y) }
  for ($i = 0; $i -lt $Clicks; $i++) {
    $inj.MouseButton($Button, $false)
    $inj.MouseButton($Button, $true)
  }
  Complete-Input $inj $what
  Finish 0
}

function Invoke-Scroll {
  if ($Delta -eq 0) { Fail "usage: observe.ps1 scroll -Delta <notches>   (negative = toward the user / zoom out; add -Horizontal for a tilt wheel)" 1 }
  $t = Resolve-Target $Window
  $pt = Resolve-Point $t $X $Y -Absolute:$ScreenCoords
  $axis = "vertical"; if ($Horizontal) { $axis = "horizontal" }
  $what = "$axis wheel $Delta notch(es)"
  if ($pt) { $what = "$what at $($pt.X),$($pt.Y) [$($pt.Origin)]" }
  Enter-InputMode $t $what | Out-Null
  $inj = New-Object Progenitor.Observe.Injector
  if ($pt) { $inj.MoveAbsolute($pt.X, $pt.Y) }
  $inj.Wheel($Delta, [bool]$Horizontal)
  Complete-Input $inj $what
  Finish 0
}

# The structural half of the self-test: everything that can be checked without a usable desktop.
# It proves the interop is shaped correctly -- which is the failure mode that produces a SendInput
# call that returns success and does nothing -- but it cannot prove delivery to a window. Run the
# full `selftest` for that, on an unlocked machine.
function Invoke-SelfTestOffline {
  $failures = New-Object System.Collections.ArrayList
  # Same rule as the live self-test: the count in the verdict is derived from the checks that ran.
  $ochecks = New-Object System.Collections.ArrayList
  function OCheck {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    [void]$ochecks.Add($Name)
    if ($Ok) { Say ("  PASS  {0,-26} {1}" -f $Name, $Detail) }
    else { Say ("  FAIL  {0,-26} {1}" -f $Name, $Detail); [void]$failures.Add($Name) }
  }

  # THE GUARANTEE, ARMED (R46 finding 7). This suite is the one that may be run while the user is
  # in the game, and its license to do that is "it touches no window and sends no input". Until
  # now the second half was structural -- nothing here calls Injector.Send(), which is the only
  # SendInput call site -- and the FIRST half was an accident: the batch check below hands
  # Invoke-QueueBatchGuarded a batch whose target spec is `client`, and what kept it off the
  # running game was that a Save-JsonFile into a directory that does not exist threw a few lines
  # before the resolve. A different function's failure ordering was doing the safety work.
  #
  # Now the fence does it. Resolve-Target is the only way any window is reached from this script,
  # and while $script:WindowFence is set it reads no process list and returns no handle for ANY
  # spec. The check named "client spec is fenced" below proves the fence fires on the most
  # dangerous spec there is, and a source pin proves the fence is the first thing that function
  # does -- so the sentence at the end of this suite is a property of the code.
  $script:WindowFence = "offline self-test (selftest -Offline): this suite may run while the game is being played"
  $script:WindowFenceHits = 0

  Say "Screenshot output sizing (synthetic pixels only):"
  $sourceBitmap = New-Object System.Drawing.Bitmap 3840, 1080
  $smallBitmap = $null
  $sourceGraphics = $null
  $roundTrip = $null
  $imageCheckPath = [IO.Path]::GetTempFileName()
  try {
    $sourceGraphics = [System.Drawing.Graphics]::FromImage($sourceBitmap)
    $sourceGraphics.Clear([System.Drawing.Color]::Blue)
    $sourceGraphics.FillRectangle([System.Drawing.Brushes]::Green, 1920, 0, 1920, 1080)
    $sourceGraphics.Dispose(); $sourceGraphics = $null
    $smallBitmap = Resize-CaptureBitmap $sourceBitmap 1920
    OCheck "thumbnail dimensions" ($smallBitmap.Width -eq 1920 -and $smallBitmap.Height -eq 540) "3840x1080 becomes 1920x540"
    OCheck "thumbnail pixels" ($smallBitmap.GetPixel(480,270).ToArgb() -eq [System.Drawing.Color]::Blue.ToArgb() -and $smallBitmap.GetPixel(1440,270).ToArgb() -eq [System.Drawing.Color]::Green.ToArgb()) "both halves retain their source colours"
    OCheck "source remains owned" ($sourceBitmap.Width -eq 3840 -and $sourceBitmap.Height -eq 1080) "resizing does not dispose or mutate the source"
    $smallBitmap.Save($imageCheckPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $roundTrip = [System.Drawing.Image]::FromFile($imageCheckPath)
    OCheck "thumbnail PNG readback" ($roundTrip.Width -eq 1920 -and $roundTrip.Height -eq 540) "saved PNG has the bounded dimensions"
    OCheck "no thumbnail upscale" ([object]::ReferenceEquals($smallBitmap, (Resize-CaptureBitmap $smallBitmap 3840))) "a larger bound returns the existing bitmap"
    OCheck "zero keeps original" ([object]::ReferenceEquals($sourceBitmap, (Resize-CaptureBitmap $sourceBitmap 0))) "omitting MaxWidth preserves full resolution"
    $thinBitmap = New-Object System.Drawing.Bitmap 3840, 1
    $thinOutput = $null
    try {
      $thinOutput = Resize-CaptureBitmap $thinBitmap 1
      OCheck "thumbnail height floor" ($thinOutput.Width -eq 1 -and $thinOutput.Height -eq 1) "extreme aspect ratios never produce zero-height bitmaps"
    }
    finally { if ($thinOutput) { $thinOutput.Dispose() }; $thinBitmap.Dispose() }
  }
  finally {
    if ($roundTrip) { $roundTrip.Dispose() }
    if ($sourceGraphics) { $sourceGraphics.Dispose() }
    if ($smallBitmap) { $smallBitmap.Dispose() }
    $sourceBitmap.Dispose()
    Remove-Item -LiteralPath $imageCheckPath -Force -ErrorAction SilentlyContinue
  }
  Say "offline self-test (no desktop required; proves shape, not delivery)"
  Say ""
  Say "INPUT struct layout -- a wrong size makes SendInput fail or, worse, misread fields:"
  $is64 = [Environment]::Is64BitProcess
  $expIn = 28; $expMi = 24; $expKi = 16
  if ($is64) { $expIn = 40; $expMi = 32; $expKi = 24 }
  $szIn = [Runtime.InteropServices.Marshal]::SizeOf([type]"Progenitor.Observe.INPUT")
  $szMi = [Runtime.InteropServices.Marshal]::SizeOf([type]"Progenitor.Observe.MOUSEINPUT")
  $szKi = [Runtime.InteropServices.Marshal]::SizeOf([type]"Progenitor.Observe.KEYBDINPUT")
  OCheck "sizeof(INPUT)" ($szIn -eq $expIn) "$szIn (Win32 expects $expIn on a 64-bit=$is64 host)"
  OCheck "sizeof(MOUSEINPUT)" ($szMi -eq $expMi) "$szMi (expects $expMi)"
  OCheck "sizeof(KEYBDINPUT)" ($szKi -eq $expKi) "$szKi (expects $expKi)"

  Say ""
  Say "Scan codes -- a virtual key sent with scan 0 is invisible to a raw-input reader:"
  foreach ($name in @("f9", "f10", "ctrl", "shift", "alt", "escape", "a", "space", "up")) {
    $vk = Resolve-KeyName $name
    $scan = [Progenitor.Observe.Native]::MapVirtualKey([uint32]$vk, 0)
    OCheck "scan($name)" ($scan -ne 0) ("vk=0x{0:X2} -> scan=0x{1:X2}" -f $vk, $scan)
  }

  Say ""
  Say "Absolute-coordinate normalisation, inverted the way Windows inverts it:"
  $vx = [Progenitor.Observe.Native]::GetSystemMetrics(76)
  $vy = [Progenitor.Observe.Native]::GetSystemMetrics(77)
  $vw = [Progenitor.Observe.Native]::GetSystemMetrics(78)
  $vh = [Progenitor.Observe.Native]::GetSystemMetrics(79)
  $points = @()
  $points += , @($vx, $vy)
  $points += , @(($vx + $vw - 1), ($vy + $vh - 1))
  $points += , @(($vx + [int]($vw / 2)), ($vy + [int]($vh / 2)))
  foreach ($p in $points) {
    $inj = New-Object Progenitor.Observe.Injector
    $inj.MoveAbsolute($p[0], $p[1])
    $detail = $inj.Steps[0].Detail
    $inj.Clear()
    if ($detail -match "normalised (\d+),(\d+)") {
      $nx = [int]$Matches[1]; $ny = [int]$Matches[2]
      $bx = [int][math]::Round($nx * ($vw - 1) / 65535.0) + $vx
      $by = [int][math]::Round($ny * ($vh - 1) / 65535.0) + $vy
      $ok = ([math]::Abs($bx - $p[0]) -le 1) -and ([math]::Abs($by - $p[1]) -le 1)
      OCheck "round trip $($p[0]),$($p[1])" $ok "-> $nx,$ny -> $bx,$by"
    }
    else { OCheck "round trip $($p[0]),$($p[1])" $false "could not parse '$detail'" }
  }

  Say ""
  Say "Chord parsing:"
  $c = Parse-Chord "Ctrl+F9"
  OCheck "Ctrl+F9" (($c.Modifiers.Count -eq 1) -and ($c.Modifiers[0] -eq 0xA2) -and ($c.Key -eq 0x78)) "1 modifier (LCtrl 0xA2) + F9 0x78"
  $c2 = Parse-Chord "F10"
  OCheck "F10" (($c2.Modifiers.Count -eq 0) -and ($c2.Key -eq 0x79)) "no modifiers, F10 0x79"

  Say ""
  Say "The plus key, which no spelling containing a '+' could send (R44 finding 14):"
  # Split-ChordTokens is the pure half and can be asserted on the cases Parse-Chord REFUSES too,
  # which Parse-Chord itself cannot be (it calls Fail, which exits).
  $tk = { param($s) @(Split-ChordTokens $s) -join "|" }
  OCheck "tokens 'Ctrl+F9'" ((& $tk "Ctrl+F9") -eq "Ctrl|F9") "separator between two names"
  OCheck "tokens 'Ctrl++'" ((& $tk "Ctrl++") -eq "Ctrl|plus") "separator, then the plus KEY -- this was @('Ctrl','','')"
  OCheck "tokens '+'" ((& $tk "+") -eq "plus") "the bare plus key -- this was @('','')"
  OCheck "tokens '++'" ((& $tk "++") -eq "plus|plus") "two plus keys; Parse-Chord refuses it, truthfully"
  OCheck "tokens 'Ctrl+'" ((& $tk "Ctrl+") -eq "Ctrl") "a trailing separator is a typo, not an implied plus"
  $cp = Parse-Chord "Ctrl++"
  OCheck "chord 'Ctrl++'" (($cp.Modifiers.Count -eq 1) -and ($cp.Modifiers[0] -eq 0xA2) -and ($cp.Key -eq 0xBB)) ("LCtrl 0xA2 + plus 0x{0:X2}" -f $cp.Key)
  $cp2 = Parse-Chord "+"
  OCheck "chord '+'" (($cp2.Modifiers.Count -eq 0) -and ($cp2.Key -eq 0xBB)) ("no modifiers, plus 0x{0:X2}" -f $cp2.Key)
  $cp3 = Parse-Chord "Ctrl+Shift++"
  OCheck "chord 'Ctrl+Shift++'" (($cp3.Modifiers.Count -eq 2) -and ($cp3.Key -eq 0xBB)) "two modifiers + plus"

  Say ""
  Say "The lock guard, and the DIRECTION it fails in (R44 finding 15, evidence 174):"
  # This is the regression test for the fail-open guard. It swaps the fallible probe for one that
  # cannot answer and asserts the guard says LOCKED. Against the old `catch { return $false }` --
  # or any future edit that reintroduces it -- the first two checks FAIL, which is the point: the
  # whole safety story of this tool is that input refuses on a locked session, so the case where
  # the guard cannot tell must land on refusal, not on "carry on typing".
  $savedProbe = $script:LockProbe
  try {
    $script:LockProbe = { throw "simulated probe failure (offline self-test)" }
    $throwLocked = Test-SessionLocked
    OCheck "unreadable -> LOCKED" ($throwLocked -eq $true) "a probe that throws is treated as locked, state '$script:LockState'"
    OCheck "unreadable is named" ($script:LockState -eq "unreadable") "the reason is carried out, not invented by the caller: '$script:LockDetail'"

    $script:LockProbe = { }
    $silentLocked = Test-SessionLocked
    OCheck "no answer -> LOCKED" ($silentLocked -eq $true) "a probe that returns nothing is treated as locked, state '$script:LockState'"

    # ...and the hook still cannot make the tool LESS refusing, in either direction of the OR.
    $script:LockProbe = { return $false }
    $clear = Test-SessionLocked
    if ($PretendLocked) {
      OCheck "clear + -PretendLocked" ($clear -eq $true) "-PretendLocked is OR-ed in: a clear probe still reads LOCKED ('$script:LockState')"
    }
    else {
      OCheck "measured clear -> unlocked" ($clear -eq $false) "a probe that MEASURED no LogonUI reads unlocked ('$script:LockState')"
    }
    $script:LockProbe = { return $true }
    OCheck "measured LogonUI -> LOCKED" ((Test-SessionLocked) -eq $true) "state '$script:LockState'"
  }
  finally { $script:LockProbe = $savedProbe }

  # -------------------------------------------------------------------------------------------
  # The queue's own guards (R45 findings 9, 11, 12, 24, 25, 35).
  #
  # Queue.ps1 is dot-sourced into this script's scope, so every function below is callable here.
  # None of these touch a window, spawn a child or send anything: they exercise the decisions the
  # drain makes, which is where all six defects lived. Scratch directories go under %TEMP%.
  # -------------------------------------------------------------------------------------------
  Say ""
  Say "The queue's target resolution, which must not be able to end a drain (R45 finding 9):"
  $qbase = Join-Path $env:TEMP ("progenitor-observe-offline-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
  New-Item -ItemType Directory -Force -Path $qbase | Out-Null
  try {
    # Against the old code every one of these EXITED this process, because Resolve-Target's
    # failure paths call Fail and Fail ends in `exit`, which a catch does not intercept. The
    # observable proof is simply that the checks below run at all.
    #
    # THE FENCE SHADOWS PART OF THIS CHECK, and saying so is the point (R46 finding 7). The
    # window fence is armed for this whole suite, so Resolve-Target now refuses before it reaches
    # its own "no process named X" path: what runs below is the fence's -Optional return, not the
    # one this check was written for. That is the right trade -- the alternative is a suite whose
    # safety depends on the spec being one that happens not to match -- but it means the behavior
    # this check used to measure has to be pinned in the source instead, which the two pins after
    # it do, more exactly than the behavioral check did.
    $miss = Resolve-BatchTarget "proc:progenitor-no-such-process-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
    OCheck "unresolvable -> null" (($null -eq $miss.Target) -and $miss.Reason) "returned instead of exiting: '$($miss.Reason)'"
    # The fence itself fails the way each caller asked: $null for -Optional, a catchable throw
    # otherwise. Neither is an exit, so neither can end this suite the way a Fail would.
    $optNull = Resolve-Target "client" -Optional
    OCheck "fenced -Optional -> null" (($null -eq $optNull) -and ($script:ResolveError -match "fenced off")) "'$script:ResolveError'"
    $threw = $false
    try { Resolve-Target "client" | Out-Null } catch { $threw = $true }
    OCheck "fenced, not optional -> throw" $threw "a throw is catchable; an exit is not, and a self-test that exits reports nothing"
    # Pin 1: the queue asks for the non-exiting probe. A revert to the bare call is invisible
    # behaviorally while the fence is armed -- it would throw and be caught -- so it is pinned.
    $qsrc0 = @([System.IO.File]::ReadAllLines((Join-Path $PSScriptRoot "observe\Queue.ps1")))
    $optCalls = 0; $bareCalls = 0
    foreach ($l in $qsrc0) {
      $t = $l.Trim()
      if ($t.StartsWith("#")) { continue }
      if ($t -notmatch 'Resolve-Target\s+\$spec') { continue }
      if ($t -match 'Resolve-Target\s+\$spec\s+-Optional\b') { $optCalls++ } else { $bareCalls++ }
    }
    OCheck "queue resolves -Optional" (($optCalls -eq 1) -and ($bareCalls -eq 0)) "$optCalls -Optional call, $bareCalls bare call in Queue.ps1"
    # Pin 2: inside Resolve-Target, every "it is not there" refusal (exit 2) is preceded by the
    # -Optional escape, and the two usage errors (exit 1) deliberately are not. That is the whole
    # of R45 finding 9's contract, and it is the half the fence can no longer exercise here.
    $osrc0 = @([System.IO.File]::ReadAllLines($script:ObserveSelf))
    $inFn = $false; $twos = 0; $softened = 0; $prev = ""
    foreach ($l in $osrc0) {
      $t = $l.Trim()
      if ($t -eq 'function Resolve-Target {') { $inFn = $true; $prev = ""; continue }
      if ($inFn -and $t -eq 'function Format-Rect {') { break }
      if (-not $inFn) { continue }
      if ($t.StartsWith("#") -or $t -eq "") { continue }
      if ($t -match '^Fail\s+.*\s2$') {
        $twos++
        if (($prev -match '^if \(\$Optional\)') -and ($prev -match 'return \$null')) { $softened++ }
      }
      $prev = $t
    }
    OCheck "exit-2 refusals softened" (($twos -ge 4) -and ($softened -eq $twos)) "$softened of $twos 'it is not there' refusals are preceded by the -Optional return"
    $bad = Resolve-BatchTarget "not-a-spec"
    OCheck "bad spec -> null" (($null -eq $bad.Target) -and ($bad.Reason -match "is not one of")) "'$($bad.Reason)'"
    $badh = Resolve-BatchTarget "hwnd:zz"
    OCheck "bad hwnd -> null" (($null -eq $badh.Target) -and ($badh.Reason -match "does not carry a number")) "'$($badh.Reason)'"

    # A batch that throws fails THAT batch and returns 10; the caller lives to run the next one.
    $brokenPaths = [pscustomobject]@{
      Root = $qbase; Batches = (Join-Path $qbase "no-such-dir\deeper"); Shots = $qbase; Logs = $qbase
      Latest = (Join-Path $qbase "latest.json"); DrainLog = (Join-Path $qbase "drain.log")
      Lock = (Join-Path $qbase "drain.lock"); Stop = (Join-Path $qbase "stop-drain")
    }
    $fakeBatch = [pscustomobject]@{ batchId = "b-offline"; label = "offline"; state = "pending"; reason = $null; result = $null
                                    drainPid = $null; steps = @()
                                    target = ([pscustomobject]@{ spec = "client"; pid = $PID; processName = "x"; startTime = "2020-01-01T00:00:00.0000000Z" }) }
    $guarded = Invoke-QueueBatchGuarded -Batch $fakeBatch -Paths $brokenPaths -WaitedSeconds 0
    OCheck "throwing batch -> 10" ($guarded -eq 10) "an unwritable batch file failed the batch, not the drain (returned $guarded, still running)"

    Say ""
    Say "Target identity: a start time that cannot be read is NOT 'fine' (R45 finding 12):"
    $savedStart = $script:ProcessStartProbe
    try {
      $fresh = [pscustomobject]@{ target = [pscustomobject]@{ pid = $PID; processName = (Get-Process -Id $PID).ProcessName; startTime = $null; startTimeError = "simulated" } }
      OCheck "no start time -> refused" ((Get-BatchStaleReason $fresh) -match "NO recorded start time") "a batch with startTime null is refused, not run"

      $script:ProcessStartProbe = { param([int]$ProcessId) throw "simulated start-time failure (offline self-test)" }
      $pinned = [pscustomobject]@{ target = [pscustomobject]@{ pid = $PID; processName = (Get-Process -Id $PID).ProcessName; startTime = "2020-01-01T00:00:00.0000000Z" } }
      OCheck "unreadable -> refused" ((Get-BatchStaleReason $pinned) -match "COULD NOT BE READ") "a probe that throws refuses the batch"
      $ident = Get-TargetIdentity ([pscustomobject]@{ Pid = $PID; ProcessName = "x"; ProcessPath = "x"; Hwnd = [IntPtr]::Zero; Title = "x"; IsGame = $false })
      OCheck "identity records why" (($ident.startTimeUnavailable -eq $true) -and $ident.startTimeError) "queue time writes the reason down: '$($ident.startTimeError)'"

      $script:ProcessStartProbe = { }
      OCheck "no answer -> refused" ((Get-BatchStaleReason $pinned) -match "COULD NOT BE READ") "a probe that returns nothing refuses the batch"

      # ...and a guard that refuses everything proves nothing, so the measured-match case must pass.
      $script:ProcessStartProbe = { param([int]$ProcessId) return "2020-01-01T00:00:00.0000000Z" }
      OCheck "measured match -> fresh" ($null -eq (Get-BatchStaleReason $pinned)) "a start time that MATCHES is not stale"
      $script:ProcessStartProbe = { param([int]$ProcessId) return "2021-06-06T06:06:06.0000000Z" }
      OCheck "measured differ -> refused" ((Get-BatchStaleReason $pinned) -match "DIFFERENT process") "a start time that differs is a different world"
    }
    finally { $script:ProcessStartProbe = $savedStart }

    OCheck "unreadable expiry -> refused" ((Get-BatchExpiryReason ([pscustomobject]@{ expiresAt = "not a date"; queuedAt = "also not"; ttlMinutes = 1 })) -match "could not be read") "an unparseable expiry refuses instead of throwing into the drain loop"

    Say ""
    Say "The execution lock: asking must not change the answer (R45 findings 24 and 25):"
    OCheck "temp name is per-process" ((Get-JsonTempPath "C:\q\b.json") -eq "C:\q\b.json.$PID.tmp") "two writers cannot collide on one .tmp (was a fixed `$Path.tmp)"

    $lockRoot = Join-Path $qbase "lock"
    New-Item -ItemType Directory -Force -Path $lockRoot | Out-Null
    $lp = [pscustomobject]@{ Lock = (Join-Path $lockRoot "drain.lock") }
    $st = Get-ExecutionLockState $lp
    OCheck "status creates nothing" (($st.State -eq "absent") -and -not (Test-Path -LiteralPath $lp.Lock)) "a queue root that has never been drained still has no drain.lock after asking"
    $held = Open-ExecutionLock $lp "offline self-test pid $PID"
    $st = Get-ExecutionLockState $lp
    OCheck "held -> RUNNING" (($null -ne $held) -and ($st.State -eq "running") -and ($st.Holder -match "offline self-test")) "state '$($st.State)', holder '$($st.Holder)'"
    OCheck "second open refused" ($null -eq (Open-ExecutionLock $lp "second")) "a second executor cannot take the lock, so queue's inline run and drain cannot both run a batch"
    Close-ExecutionLock $held
    $st = Get-ExecutionLockState $lp
    OCheck "released -> idle, no name" (($st.State -eq "idle") -and -not $st.Holder) "the holder string is truncated on release, so it cannot name a process that exited hours ago"

    # A lock that cannot be opened for an unrelated reason is not a running drain. A directory
    # opened as a file throws UnauthorizedAccessException, which is a real one of those.
    $dirLock = [pscustomobject]@{ Lock = $lockRoot }
    $st = Get-ExecutionLockState $dirLock
    OCheck "unreadable is not RUNNING" ($st.State -eq "unreadable") "state '$($st.State)': $($st.Detail)"

    Say ""
    Say "Reaping a dead drain by identity, not by pid (R45 finding 11):"
    $rRoot = Join-Path $qbase "reap"
    $rPaths = [pscustomobject]@{ Root = $rRoot; Batches = (Join-Path $rRoot "batches"); DrainLog = (Join-Path $rRoot "drain.log") }
    New-Item -ItemType Directory -Force -Path $rPaths.Batches | Out-Null
    $myStart = (Get-Process -Id $PID).StartTime.ToString("o")
    function WriteReapBatch { param([string]$Id, $DrainStart, [bool]$WithStart = $true)
      $o = [pscustomobject]@{ batchId = $Id; state = "running"; reason = $null; drainPid = $PID }
      if ($WithStart) { Add-BatchProperty $o "drainStartTime" $DrainStart }
      Save-JsonFile -Path (Join-Path $rPaths.Batches "$Id.json") -Object $o
    }
    WriteReapBatch "reused" "1999-01-01T00:00:00.0000000Z" $true
    WriteReapBatch "nostart" $null $false
    WriteReapBatch "live" $myStart $true
    Repair-InterruptedBatches -Paths $rPaths -LockHeld | Out-Null
    $reused = Read-JsonFile (Join-Path $rPaths.Batches "reused.json")
    $nostart = Read-JsonFile (Join-Path $rPaths.Batches "nostart.json")
    $live = Read-JsonFile (Join-Path $rPaths.Batches "live.json")
    OCheck "reused pid -> reaped" (([string]$reused.state -eq "interrupted") -and ($reused.reason -match "DIFFERENT process")) "pid $PID is alive, but it is not the drain that claimed it: state '$($reused.state)'"
    OCheck "no drain identity -> reaped" (([string]$nostart.state -eq "interrupted") -and ($nostart.reason -match "no start time")) "state '$($nostart.state)'"
    OCheck "live drain left alone" ([string]$live.state -eq "running") "a claimer that IS this process is not reaped: state '$($live.state)'"

    Say ""
    Say "The drain's poll interval cannot be a hot spin (R45 finding 35):"
    $poll = $null
    try { $poll = (Get-Command -Name $script:ObserveSelf -CommandType ExternalScript).Parameters["PollSeconds"] } catch {}
    $vr = $null
    if ($poll) { $vr = @($poll.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] })[0] }
    OCheck "-PollSeconds floor" (($null -ne $vr) -and ([int]$vr.MinRange -ge 1)) $(if ($vr) { "ValidateRange($($vr.MinRange), $($vr.MaxRange)): 0 and negatives are refused before the sleepless loop is reachable" } else { "no ValidateRange on -PollSeconds" })

    # ---------------------------------------------------------------------------------------
    # R45 finding 32: four numbers a caller reads that were asserted rather than measured.
    # ---------------------------------------------------------------------------------------
    Say ""
    Say "Counted, not assumed: 'ran' means steps were sent (R45 finding 32):"
    # Invoke-QueueBatchGuarded above was driven with an unwritable batch directory, so it threw
    # before Invoke-QueueBatch could reach its step loop. That is the exact case the old
    # unconditional `$ran++` counted as a batch that ran.
    OCheck "throw -> flag stays false" ($script:BatchStepsAttempted -eq $false) "the guarded batch that failed at `$guarded=$guarded never reached its step loop"

    # ...and the counting rule the drain actually applies, which is why this lives outside the
    # drain loop at all: a refusal is refused ONLY, and never also 'ran'.
    $t8 = New-DrainTally; Add-BatchOutcome $t8 8 $false
    OCheck "refused -> 0 ran" (($t8.Ran -eq 0) -and ($t8.Refused -eq 1) -and ($t8.Failed -eq 0)) "exit 8 with no step sent: ran $($t8.Ran), failed $($t8.Failed), refused $($t8.Refused)"
    $t0 = New-DrainTally; Add-BatchOutcome $t0 0 $true
    OCheck "ok -> 1 ran" (($t0.Ran -eq 1) -and ($t0.Refused -eq 0) -and ($t0.Failed -eq 0)) "ran $($t0.Ran), failed $($t0.Failed), refused $($t0.Refused)"
    $t10 = New-DrainTally; Add-BatchOutcome $t10 10 $true
    OCheck "failed step -> ran AND failed" (($t10.Ran -eq 1) -and ($t10.Failed -eq 1)) "a batch that sent steps and had one fail did run: ran $($t10.Ran), failed $($t10.Failed)"
    $tThrow = New-DrainTally; Add-BatchOutcome $tThrow 10 $false
    OCheck "throw -> failed, not ran" (($tThrow.Ran -eq 0) -and ($tThrow.Failed -eq 1)) "an error before the first step is not a batch that ran: ran $($tThrow.Ran), failed $($tThrow.Failed)"
    # The drain must READ the flag rather than counting every return. Source pin, because the loop
    # is not callable here; commented lines are skipped, and a bare `$ran++` fails it.
    $dsrc = @([System.IO.File]::ReadAllLines((Join-Path $PSScriptRoot "observe\Queue.ps1")))
    $folds = 0; $bare = 0
    foreach ($l in $dsrc) {
      $t = $l.Trim()
      if ($t.StartsWith("#")) { continue }
      if ($t -eq 'Add-BatchOutcome $tally $code $script:BatchStepsAttempted') { $folds++ }
      if ($t -match '^\$(ran|tally\.Ran)\+\+$') { $bare++ }
    }
    OCheck "every finish folds one way" (($folds -eq 3) -and ($bare -eq 0)) "$folds call site(s) fold a finished batch through Add-BatchOutcome, $bare increment 'ran' directly"

    # And the flag has to be SET, unconditionally, on the last line before the loop. Nothing
    # offline can watch it turn true -- reaching the step loop needs a window and a child process
    # -- so this pin covers the direction the checks above cannot. It is written as an exact-text
    # match followed by an adjacency test on purpose: a mutation that wraps the assignment in a
    # ternary ($script:BatchStepsAttempted = $(if (...) { $false } else { $true })) survives every
    # other check in this file, which is the defect three jobs hit today and the reason
    # AssertStatement exists on the C# side.
    $iSet = -1; $iSteps = -1
    for ($i = 0; $i -lt $dsrc.Count; $i++) {
      $t = $dsrc[$i].Trim()
      if ($t.StartsWith("#") -or $t -eq "") { continue }
      if ($iSet -lt 0) { if ($t -eq '$script:BatchStepsAttempted = $true') { $iSet = $i }; continue }
      $iSteps = $i
      break
    }
    $setOk = ($iSet -ge 0) -and ($dsrc[$iSteps].Trim() -eq 'foreach ($step in $Batch.steps) {')
    OCheck "flag set true beside the loop" $setOk "line $($iSet + 1) is the bare assignment; the next statement (line $($iSteps + 1)) is '$(if ($iSteps -ge 0) { $dsrc[$iSteps].Trim() } else { '<none>' })'"

    Say ""
    Say "A rolled log is a rolled log even when it grew past where it was (R45 finding 32):"
    $lg = Join-Path $qbase "roll"
    New-Item -ItemType Directory -Force -Path $lg | Out-Null
    $lf = Join-Path $lg "LogOutput.log"
    Set-Content -LiteralPath $lf -Value @("one", "two", "three") -Encoding utf8
    $before = Get-LogStamp $lf
    OCheck "stamp is readable" (($before.Readable -eq $true) -and ($before.Lines -eq 3) -and ($before.Length -gt 0) -and ($before.FirstLine -eq "one")) "lines $($before.Lines), $($before.Length) bytes, first line '$($before.FirstLine)'"

    Add-Content -LiteralPath $lf -Value "four" -Encoding utf8
    OCheck "grew -> not rolled" ((Test-LogRolled $before (Get-LogStamp $lf)) -eq $false) "the same file with more lines in it did not roll"

    # The roll: replace the file with a NEW one that is LONGER than the old one, which is what a
    # V Rising client does within seconds of starting. The old `lineAfter -lt $lineBefore` test
    # answered "not rolled" here, and the batch's line range was then computed across two files.
    # Note the delete-and-recreate: NTFS tunneling gives the new file the OLD file's creation
    # time, which is why the fingerprint is the first line and not the creation time.
    Remove-Item -LiteralPath $lf -Force
    Set-Content -LiteralPath $lf -Value (1..40 | ForEach-Object { "fresh log line $_" }) -Encoding utf8
    $grown = Get-LogStamp $lf
    $rolledGrown = Test-LogRolled $before $grown
    OCheck "rolled AND grew -> rolled" ($rolledGrown -eq $true) "$($before.Lines) lines -> $($grown.Lines) lines in a different file: rolled=$rolledGrown"

    Set-Content -LiteralPath $lf -Value @("a") -Encoding utf8
    OCheck "rolled shorter -> rolled" ((Test-LogRolled $before (Get-LogStamp $lf)) -eq $true) "the shrink case the old test did catch still reads as a roll"

    $missing = Get-LogStamp (Join-Path $lg "no-such.log")
    OCheck "unreadable -> unknown" (($missing.Readable -eq $false) -and ($null -eq (Test-LogRolled $before $missing))) "an unreadable end is null, never `$false: 'it did not roll' is a claim"

    Say ""
    Say "latest.json follows every terminal state, not three of seven (R45 finding 32):"
    $lRoot = Join-Path $qbase "latest"
    $lPaths = [pscustomobject]@{ Root = $lRoot; Batches = (Join-Path $lRoot "batches"); Latest = (Join-Path $lRoot "latest.json"); DrainLog = (Join-Path $lRoot "drain.log") }
    New-Item -ItemType Directory -Force -Path $lPaths.Batches | Out-Null
    $seen = New-Object System.Collections.ArrayList
    foreach ($state in @("done", "failed", "refused", "expired", "aborted")) {
      $sb = [pscustomobject]@{ batchId = "b-$state"; state = "running"; reason = $null }
      Set-BatchState $sb $lPaths $state "offline self-test"
      $latest = Read-JsonFile $lPaths.Latest
      if (([string]$latest.batchId -eq "b-$state") -and ([string]$latest.state -eq $state)) { [void]$seen.Add($state) }
    }
    OCheck "every terminal state copies" ($seen.Count -eq 5) "latest.json named the batch just finished for: $($seen -join ', ')"

    Say ""
    Say "sessionLocked is a fact about the batch's START (R45 finding 32):"
    # A source check, because Invoke-QueueBatch needs a real window and a real child process and
    # so is not callable here. It is written to fail if the read moves back after the steps: the
    # ORDER is the fix, and a check that only looked for the two lines would pass with them in
    # either order. Commented-out lines are skipped, so the pin cannot be satisfied by a comment.
    $qsrc = @([System.IO.File]::ReadAllLines((Join-Path $PSScriptRoot "observe\Queue.ps1")))
    $iCapture = -1; $iLoop = -1; $iUse = -1; $iLive = -1
    for ($i = 0; $i -lt $qsrc.Count; $i++) {
      $t = $qsrc[$i].Trim()
      if ($t.StartsWith("#")) { continue }
      if ($iCapture -lt 0 -and $t -eq '$lockedAtStart = Test-SessionLocked') { $iCapture = $i }
      if ($iLoop -lt 0 -and $t -eq 'foreach ($step in $Batch.steps) {') { $iLoop = $i }
      if ($iUse -lt 0 -and $t -match '^sessionLocked\s*=\s*\$lockedAtStart$') { $iUse = $i }
      if ($iLive -lt 0 -and $t -match '^sessionLocked\s*=\s*\(Test-SessionLocked\)$') { $iLive = $i }
    }
    $lockOrderOk = ($iCapture -ge 0) -and ($iLoop -gt $iCapture) -and ($iUse -gt $iLoop) -and ($iLive -lt 0)
    OCheck "locked read before steps" $lockOrderOk "capture line $($iCapture + 1), step loop line $($iLoop + 1), recorded at line $($iUse + 1); a live read in the result record would be line $($iLive + 1)"

    # -----------------------------------------------------------------------------------------
    # R46 finding 7: this suite touches no window BY CONSTRUCTION, and here is the construction.
    # -----------------------------------------------------------------------------------------
    Say ""
    Say "The window fence: no spec resolves while this suite runs (R46 finding 7):"
    OCheck "fence armed" ([bool]$script:WindowFence) "'$script:WindowFence'"
    $hits0 = [int]$script:WindowFenceHits
    # `client` deliberately: the spec that resolves the V Rising process the user is playing in.
    # The old suite avoided the dangerous spec; this one runs it and proves it cannot land.
    $fencedClient = Resolve-BatchTarget "client"
    $fencedOk = ($null -eq $fencedClient.Target) -and ($fencedClient.Reason -match "fenced off") -and ([int]$script:WindowFenceHits -eq $hits0 + 1)
    OCheck "client spec is fenced" $fencedOk "asking for the live client returned no target and counted a fence hit ($hits0 -> $($script:WindowFenceHits)): '$($fencedClient.Reason)'"
    # ...and it is the FIRST thing Resolve-Target does, so nothing can enumerate ahead of it. The
    # pin skips comments and blank lines and requires adjacency: a fence moved below the first
    # Get-Process would satisfy a "the fence exists" check and none of the property.
    $osrc = @([System.IO.File]::ReadAllLines($script:ObserveSelf))
    $iFn = -1; $order = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $osrc.Count; $i++) {
      $t = $osrc[$i].Trim()
      if ($iFn -lt 0) { if ($t -eq 'function Resolve-Target {') { $iFn = $i }; continue }
      if ($t.StartsWith("#") -or $t -eq "") { continue }
      [void]$order.Add($t)
      if ($order.Count -ge 3) { break }
    }
    $fenceFirstOk = ($iFn -ge 0) -and ($order.Count -ge 3) -and
                    ($order[0] -eq 'param([string]$Spec, [switch]$Optional)') -and
                    ($order[1] -eq '$script:ResolveError = $null') -and
                    ($order[2] -eq 'if ($script:WindowFence) {')
    OCheck "fence is first" $fenceFirstOk "Resolve-Target at line $($iFn + 1) opens with: $($order -join ' | ')"

    # -----------------------------------------------------------------------------------------
    # R46 findings 3 and 6: a consequence sentence is composed from the observation, by one
    # function, and no call site writes one. These two are pure and callable, which is the point.
    # -----------------------------------------------------------------------------------------
    Say ""
    Say "The aborted record says what was observed, and no more (R46 finding 3):"
    $abNone = Get-BatchAbortRecord -StepsAttempted $false -Refusal $null
    $abNoneOk = ($abNone.Refused -eq $false) -and ($abNone.Reason -match "NOT established") -and
                ($abNone.Reason -match "nothing was sent") -and ($abNone.Reason -notmatch "gate") -and
                ($abNone.Reason -match "Ctrl-C")
    OCheck "no evidence -> no cause" $abNoneOk "'$($abNone.Reason)'"
    $abSteps = Get-BatchAbortRecord -StepsAttempted $true -Refusal $null
    OCheck "steps started -> unknown" (($abSteps.Reason -match "not known") -and ($abSteps.Reason -notmatch "gate")) "'$($abSteps.Reason)'"
    $abGate = Get-BatchAbortRecord -StepsAttempted $true -Refusal ([pscustomobject]@{ Message = "simulated gate refusal"; Code = 3 })
    $abGateOk = ($abGate.Refused -eq $true) -and ($abGate.Reason -match "a gate inside this batch refused") -and
                ($abGate.Reason -match "simulated gate refusal") -and ($abGate.Reason -match "exit 3")
    OCheck "a measured gate is named" $abGateOk "'$($abGate.Reason)'"

    Say ""
    Say "Who will run a batch this command would not run (R46 finding 6):"
    $lcRun = Get-ExecutionLockConsequence ([pscustomobject]@{ State = "running"; Holder = "drain pid 4242"; Detail = "held" }) "C:\q\drain.lock"
    $lcRunOk = ($lcRun.RunsUnattended -eq $true) -and ($lcRun.Cause -match "drain pid 4242") -and ($lcRun.Advice -match "without anyone doing anything")
    OCheck "measured holder -> unattended" $lcRunOk "'$($lcRun.Cause)' / '$($lcRun.Advice)'"
    foreach ($bad in @("unreadable", "idle", "absent", "something-invented-later")) {
      $lc = Get-ExecutionLockConsequence ([pscustomobject]@{ State = $bad; Holder = ""; Detail = "simulated $bad" }) "C:\q\drain.lock"
      $lcOk = ($lc.RunsUnattended -eq $false) -and ($lc.Cause -match "NOT established") -and
              ($lc.Advice -notmatch "without anyone doing anything") -and ($lc.Advice -match "drain")
      OCheck "$bad -> nobody promised" $lcOk "'$($lc.Advice)'"
    }
    $lcNull = Get-ExecutionLockConsequence $null "C:\q\drain.lock"
    OCheck "no state at all -> honest" (($lcNull.RunsUnattended -eq $false) -and ($lcNull.Cause -match "NOT established")) "a null lock state takes the default branch, not the first one"

    # The enforcement, and the reason this is a structural fix rather than a third correct patch:
    # each clause that makes a claim lives in exactly ONE place in Queue.ps1, and that place is
    # the composer. A call site that writes its own copy fails this, which is how the shape three
    # reviews have found gets caught on the next run instead of in the next review.
    $qlines = @([System.IO.File]::ReadAllLines((Join-Path $PSScriptRoot "observe\Queue.ps1")))
    function OwnerOf {
      param($Lines, [string]$Needle)
      $hits = 0; $owner = "<nowhere>"
      for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim().StartsWith("#")) { continue }
        if ($Lines[$i] -notmatch [regex]::Escape($Needle)) { continue }
        $hits++
        for ($j = $i; $j -ge 0; $j--) {
          if ($Lines[$j] -match '^function\s+([A-Za-z\-]+)\s*\{') { $owner = $Matches[1]; break }
        }
      }
      return [pscustomobject]@{ Hits = $hits; Owner = $owner }
    }
    $o1 = OwnerOf $qlines "without anyone doing anything"
    OCheck "unattended claim: 1 place" (($o1.Hits -eq 1) -and ($o1.Owner -eq "Get-ExecutionLockConsequence")) "$($o1.Hits) occurrence(s), in $($o1.Owner)"
    $o2 = OwnerOf $qlines "a gate inside this batch refused"
    OCheck "gate claim: 1 place" (($o2.Hits -eq 1) -and ($o2.Owner -eq "Get-BatchAbortRecord")) "$($o2.Hits) occurrence(s), in $($o2.Owner)"
    $o3 = OwnerOf $qlines "Get-BatchAbortRecord -StepsAttempted"
    OCheck "the finally composes" (($o3.Hits -eq 1) -and ($o3.Owner -eq "Invoke-QueueBatchGuarded")) "$($o3.Hits) call site(s), in $($o3.Owner)"
    $o4 = OwnerOf $qlines 'Set-BatchState $Batch $Paths "aborted"'
    OCheck "aborted written once" (($o4.Hits -eq 1) -and ($o4.Owner -eq "Invoke-QueueBatchGuarded")) "$($o4.Hits) write(s) of the aborted state, in $($o4.Owner)"

    # -----------------------------------------------------------------------------------------
    # The finally itself, driven by a REAL process exit -- the only thing that has ever exercised
    # it (R46 finding 3: "nothing drives the finally").
    #
    # A child powershell.exe, because `exit` is the mechanism under test and it cannot be run in
    # this process without ending the suite. It is a console process started with its output
    # redirected into this pipeline, so it inherits this console and creates no window, and it
    # cannot touch one either way: it dot-sources Queue.ps1 ONLY, so Resolve-Target does not exist
    # in it, and the batch runner is replaced with one that exits before any step. Nothing it does
    # reaches SendInput, a window handle or the game.
    # -----------------------------------------------------------------------------------------
    Say ""
    Say "The finally, driven by a real process exit (R46 finding 3):"
    $abRoot = Join-Path $qbase "abort"
    New-Item -ItemType Directory -Force -Path (Join-Path $abRoot "batches") | Out-Null
    $childPath = Join-Path $abRoot "abort-child.ps1"
    $childSrc = @'
param([string]$QueueLib, [string]$Root, [int]$Mode)
$ErrorActionPreference = "Stop"
# Observe.Native.cs uses the .NET Framework System.Drawing compiler references. PowerShell 7
# forwards Bitmap to System.Drawing.Common, which its Add-Type reference set cannot resolve.
# Re-enter the supported runtime before loading interop. Serialize parameters as data so quoted
# paths, switches, Unicode and literal command-like text retain their meaning without evaluation.
if ($PSVersionTable.PSEdition -eq "Core") {
  $windowsPowerShell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
  if (-not (Test-Path -LiteralPath $windowsPowerShell)) {
    [Console]::Error.WriteLine("observe: Windows PowerShell 5.1 is required for screenshot interop; runtime not found: $windowsPowerShell")
    exit 1
  }
  $forwardFile = [IO.Path]::GetTempFileName()
  try {
    $forwardParameters = @{}
    foreach ($key in $PSBoundParameters.Keys) {
      $valueToForward = $PSBoundParameters[$key]
      if ($valueToForward -is [System.Management.Automation.SwitchParameter]) { $valueToForward = [bool]$valueToForward }
      $forwardParameters[$key] = $valueToForward
    }
    @{ Script = $PSCommandPath; Parameters = $forwardParameters; WorkingDirectory = (Get-Location).Path } |
      Export-Clixml -LiteralPath $forwardFile -Depth 10
    $forwardLiteral = $forwardFile.Replace("'", "''")
    $forwardCommand = "`$ProgressPreference = 'SilentlyContinue'; `$call = Import-Clixml -LiteralPath '$forwardLiteral'; Set-Location -LiteralPath `$call.WorkingDirectory; `$invoke = `$call.Parameters; & `$call.Script @invoke; exit `$LASTEXITCODE"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($forwardCommand))
    [Console]::Error.WriteLine("observe: using Windows PowerShell 5.1 for System.Drawing capture compatibility")
    & $windowsPowerShell -NoLogo -NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand $encodedCommand
    $forwardExit = $LASTEXITCODE
  }
  catch { [Console]::Error.WriteLine("observe: runtime handoff failed: " + $_.Exception.Message); $forwardExit = 1 }
  finally { Remove-Item -LiteralPath $forwardFile -Force -ErrorAction SilentlyContinue }
  exit $forwardExit
}
function Say { param([string]$Message) }
function Test-SessionLocked { return $false }
$script:LastRefusal = $null
function Fail {
  param([string]$Message, [int]$Code = 1)
  $script:LastRefusal = [pscustomobject]@{ Message = $Message; Code = $Code; At = (Get-Date) }
  exit $Code
}
. $QueueLib
# The batch runner, replaced: this exercises the unwind, not the steps. Mode 1 leaves from inside
# the batch with no evidence at all (an exit, and what a Ctrl-C looks like from in here); mode 2
# leaves through a gate that named itself.
function Invoke-QueueBatch {
  param($Batch, $Paths, [int]$WaitedSeconds = 0)
  if ($Mode -eq 2) { Fail "simulated gate refusal from inside a batch" 3 }
  exit 42
}
$paths = [pscustomobject]@{
  Root = $Root; Batches = (Join-Path $Root "batches"); Shots = $Root; Logs = $Root
  Latest = (Join-Path $Root "latest.json"); DrainLog = (Join-Path $Root "drain.log")
  Lock = (Join-Path $Root "drain.lock"); Stop = (Join-Path $Root "stop-drain")
}
$batch = [pscustomobject]@{ batchId = "b-abort-$Mode"; label = "abort"; state = "running"; reason = $null
                            result = $null; drainPid = $PID; steps = @() }
Invoke-QueueBatchGuarded -Batch $batch -Paths $paths -WaitedSeconds 0 | Out-Null
exit 99
'@
    Set-Content -LiteralPath $childPath -Value $childSrc -Encoding utf8
    $queueLib = Join-Path $PSScriptRoot "observe\Queue.ps1"
    foreach ($mode in @(1, 2)) {
      $expectExit = $(if ($mode -eq 1) { 42 } else { 3 })
      & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $childPath -QueueLib $queueLib -Root $abRoot -Mode $mode 2>&1 | Out-Null
      $childExit = $LASTEXITCODE
      $rec = Read-JsonFile (Join-Path $abRoot "batches\b-abort-$mode.json")
      $stateOk = ($null -ne $rec) -and ([string]$rec.state -eq "aborted") -and ($childExit -eq $expectExit)
      if ($mode -eq 1) {
        $wordOk = $stateOk -and ([string]$rec.reason -match "NOT established") -and ([string]$rec.reason -notmatch "gate") -and
                  ([string]$rec.reason -match "nothing was sent") -and ($rec.result.refused -eq $false)
        OCheck "exit -> aborted, no cause" $wordOk "child left with $childExit, state '$($rec.state)': '$($rec.reason)'"
      }
      else {
        $wordOk = $stateOk -and ([string]$rec.reason -match "a gate inside this batch refused") -and
                  ([string]$rec.reason -match "simulated gate refusal") -and ($rec.result.refused -eq $true)
        OCheck "gate exit -> gate named" $wordOk "child left with $childExit, state '$($rec.state)': '$($rec.reason)'"
      }
    }
  }
  finally {
    try { Remove-Item -LiteralPath $qbase -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    $script:WindowFence = $null
  }

  Say ""
  $script:Result.failures = @($failures)
  $script:Result.checks = @($ochecks)
  $script:Result.checkCount = $ochecks.Count
  if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("observe: offline self-test FAILED: $($failures.Count) of $($ochecks.Count) checks: $($failures -join ', ')")
    Finish 6
  }
  Say "offline self-test PASSED ($($ochecks.Count) checks). Delivery to a real window is NOT covered"
  Say "here -- run 'observe.ps1 selftest' on an unlocked machine for that."
  Finish 0
}

function Invoke-SelfTest {
  if ($Offline) { Invoke-SelfTestOffline; return }

  # -DryRun used to be silently ignored here (R44 finding 16): every other input subcommand funnels
  # through Enter-InputMode and honors it, `selftest` did not, so `selftest -DryRun` pressed
  # Ctrl+F9, typed 21 characters, clicked and scrolled for real. Refusing is better than obeying:
  # a self-test that sends nothing proves nothing, so there is no useful thing for the flag to mean
  # here. -Offline is the mode that needs no desktop, and it is what a caller actually wants.
  if ($DryRun) {
    Fail ("'selftest' is by definition a live-input check -- it drives its own scratch window and " +
          "reads back what that window actually received, so a dry run would assert nothing. " +
          "-DryRun is not accepted here (it is honoured by key/type/move/click/scroll). Run " +
          "'observe.ps1 selftest -Offline' for the structural half, which sends no input and needs " +
          "no desktop, or 'observe.ps1 selftest' to run the real thing against the scratch window.") 1
  }

  if (Test-SessionLocked) {
    Fail ("$(Get-LockReason), so the self-test cannot run: its target window would not be " +
          "visible and its key presses would go to the Windows credential prompt. Unlock the machine, " +
          "or run 'observe.ps1 selftest -Offline' for the structural checks that need no desktop.") 3
  }

  # Proves the whole input path -- chords, unicode typing, absolute click coordinates, the wheel,
  # focus stealing -- and the screenshot path, against a window this script creates and owns.
  # Never touches the game. This is the check to run after changing anything in here.
  $target = Join-Path $PSScriptRoot "observe\selftest-target.ps1"
  if (-not (Test-Path $target)) { Fail "self-test target missing: $target" 1 }
  $log = Join-Path $env:TEMP ("progenitor-observe-selftest-" + [Guid]::NewGuid().ToString("N") + ".log")
  $shot = Join-Path $env:TEMP ("progenitor-observe-selftest-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".png")
  $title = "Progenitor observe self-test"

  Say "self-test: starting the target window ($title)"
  Say "self-test: it WILL take focus for a few seconds. Hands off the keyboard until this finishes."
  $proc = Start-Process -FilePath "powershell.exe" `
    -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-WindowStyle", "Hidden", "-File", $target, "-LogPath", $log) `
    -PassThru

  $failures = New-Object System.Collections.ArrayList
  # The count in the PASSED line is DERIVED from this, not typed into the string. It said
  # "8 checks" while running seven (R44 finding 17), and it is the line an operator reads to
  # conclude the tool is healthy -- a number the code does not measure has no business being in it.
  $checks = New-Object System.Collections.ArrayList
  try {
    $deadline = (Get-Date).AddSeconds(30)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
      if (Test-Path $log) {
        $first = @(Get-Content $log -ErrorAction SilentlyContinue)
        if ($first -and ($first -join "`n") -match "READY") { $ready = $true; break }
      }
      Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { Fail "the self-test target never reported READY (log $log). Its own error is in that file." 6 }

    $t = Resolve-Target "title:^$([regex]::Escape($title))$"
    Say ("self-test: target hwnd 0x{0:X} pid {1} window {2} client {3}" -f [int64]$t.Hwnd, $t.Pid, (Format-Rect $t.WindowRect), (Format-Rect $t.ClientRect))
    if (-not [Progenitor.Observe.Windows]::Focus($t.Hwnd, 4000)) { Fail "could not focus the self-test window" 4 }
    Start-Sleep -Milliseconds 400

    # 1. chord
    $inj = New-Object Progenitor.Observe.Injector
    $inj.KeyDown([uint16]0xA2); $inj.KeyDown([uint16]0x78); Send-Batch $inj | Out-Null
    Start-Sleep -Milliseconds 60
    $inj.KeyUp([uint16]0x78); $inj.KeyUp([uint16]0xA2); Send-Batch $inj | Out-Null
    Start-Sleep -Milliseconds 200

    # 2. unicode text
    $probe = "progenitor-observe-42"
    foreach ($ch in [char[]]$probe) { $inj.Char($ch) }
    Send-Batch $inj | Out-Null
    Start-Sleep -Milliseconds 300

    # 3. absolute click at a known client point
    $clickX = 120; $clickY = 300
    $inj.MoveAbsolute($t.ClientRect.Left + $clickX, $t.ClientRect.Top + $clickY)
    $inj.MouseButton("left", $false); $inj.MouseButton("left", $true)
    Send-Batch $inj | Out-Null
    Start-Sleep -Milliseconds 300

    # 4. wheel
    $inj.Wheel(-3, $false)
    Send-Batch $inj | Out-Null
    Start-Sleep -Milliseconds 400

    # 5. screenshot of that same window
    $bmp = [Progenitor.Observe.Capture]::FromScreen($t.WindowRect)
    $stats = [Progenitor.Observe.Capture]::Analyse($bmp, 2)
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $shotW = $bmp.Width; $shotH = $bmp.Height
    $bmp.Dispose()

    Start-Sleep -Milliseconds 300
    $lines = @(Get-Content $log -ErrorAction SilentlyContinue)
    $text = ($lines -join "`n")

    function Check {
      param([string]$Name, [bool]$Ok, [string]$Detail)
      [void]$checks.Add($Name)
      if ($Ok) { Say ("  PASS  {0,-22} {1}" -f $Name, $Detail) }
      else { Say ("  FAIL  {0,-22} {1}" -f $Name, $Detail); [void]$failures.Add($Name) }
    }

    Say ""
    Say "self-test results (what the target window actually received):"
    Check "focus stolen" ($text -match "ACTIVATED") "the target reported it became the active window"
    Check "chord Ctrl+F9" ($text -match "KEYDOWN vk=120 ctrl=True alt=False shift=False") "KeyDown with vk 120 (F9) and Ctrl held"
    $typed = ""
    foreach ($l in $lines) { if ($l -match "^CHAR (.)$") { $typed += $Matches[1] } }
    Check "unicode typing" ($typed -eq $probe) "target received '$typed' (expected '$probe')"
    $clickOk = $false; $clickDetail = "no MOUSEDOWN logged"
    foreach ($l in $lines) {
      if ($l -match "^MOUSEDOWN Left (-?\d+),(-?\d+)$") {
        $gx = [int]$Matches[1]; $gy = [int]$Matches[2]
        $clickDetail = "target saw the click at client $gx,$gy (sent $clickX,$clickY)"
        if ([math]::Abs($gx - $clickX) -le 2 -and [math]::Abs($gy - $clickY) -le 2) { $clickOk = $true }
      }
    }
    Check "click coordinates" $clickOk $clickDetail
    Check "wheel -3" ($text -match "WHEEL -360") "target saw a -360 wheel delta (3 notches toward the user)"
    Check "screenshot size" ($shotW -eq ($t.WindowRect.Right - $t.WindowRect.Left) -and $shotH -eq ($t.WindowRect.Bottom - $t.WindowRect.Top)) "${shotW}x${shotH} matches the window rect"
    # BOTH halves of the tool's own blank check, in the same order the shot path applies them.
    # This used to test the stddev half alone (R44 finding 17) -- the half evidence 169 proved
    # insufficient: the Windows lock screen scores stddev 6.07, comfortably over the 2.0 floor,
    # while black holds 99.9% of the frame. A self-test that applies half the definition cannot
    # detect a regression of the exact failure the check was rewritten for, and would have passed
    # the capture that started the whole investigation.
    $shotBlank = ($stats.StdDev -lt $MinStdDev) -or ($stats.TopColourShare -ge $MaxDominantShare)
    Check "screenshot not blank" (-not $shotBlank) ("stddev {0:F2} (floor {1}), {2} colours, dominant {3} at {4:P1} (ceiling {5:P0})" -f $stats.StdDev, $MinStdDev, $stats.UniqueColours, $stats.TopColour, $stats.TopColourShare, $MaxDominantShare)

    Say ""
    Say "self-test screenshot: $shot"
    Say "self-test log:        $log"
    $script:Result.selftestShot = $shot
    $script:Result.selftestLog = $log
    $script:Result.failures = @($failures)
    $script:Result.checks = @($checks)
    $script:Result.checkCount = $checks.Count
  }
  finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
  }

  if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("observe: self-test FAILED: $($failures.Count) of $($checks.Count) checks: $($failures -join ', ')")
    Finish 6
  }
  Say "self-test PASSED ($($checks.Count) checks: $($checks -join ', '))."
  Finish 0
}

# ---------------------------------------------------------------------------------------------
# The batch queue. Same reason the interop is its own file: a 700-line block bolted onto the end
# of this one would be undiffable, and none of it is Win32. Dot-sourced into the SCRIPT scope, so
# it sees the parameter block, $script:Result, Fail/Finish/Say, Test-SessionLocked and
# Resolve-Target -- the queue never reimplements a gate, it spawns this script and inherits them.
# ---------------------------------------------------------------------------------------------
$script:ObserveSelf = $PSCommandPath
. (Join-Path $PSScriptRoot "observe\Queue.ps1")
. (Join-Path $PSScriptRoot "observe\QueueSelfTest.ps1")

switch ($Action) {
  "windows"        { Invoke-Windows }
  "shot"           { Invoke-Shot }
  "key"            { Invoke-Key }
  "type"           { Invoke-Type }
  "move"           { Invoke-Move }
  "click"          { Invoke-Click }
  "scroll"         { Invoke-Scroll }
  "selftest"       { Invoke-SelfTest }
  "queue"          { Invoke-Queue }
  "drain"          { Invoke-Drain }
  "queue-status"   { Invoke-QueueStatus }
  "queue-cancel"   { Invoke-QueueCancel }
  "queue-selftest" { Invoke-QueueSelfTest }
  default          { Fail "unknown action '$Action'" 1 }
}

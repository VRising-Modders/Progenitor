<#
.SYNOPSIS
  Launch or stop an extra V Rising client process (not through Steam) for multi-instance tests.

.DESCRIPTION
  Index 1..99 starts VRising.exe directly (never through Steam) with an isolated -logFile,
  -persistentDataPath, Dev-bridge port, and token file. Never uses steam://run (Steam is
  single-instance and would focus the primary client). Index 0 is refused; that is
  tools/launch-client.ps1.

  The child process gets PROGENITOR_DEV_PORT=27341+10*N, PROGENITOR_DEV_TOKEN_FILE under
  %TEMP%\progenitor-client-N, and PROGENITOR_INSTANCE=client-N. This script then polls
  that instance's bridge via tools/mcp/call.mjs (PROGENITOR_CLIENT_PORT / TOKEN_FILE)
  until ping answers or the timeout.

  -Stop kills only the PID recorded for that index, and only if that process path is
  VRising.exe under the Steam install or the headless root. Never matches by process name. Never touches
  C:\VRisingServer or the staged test server.

  Optional -Connect host:port sends the client "connect" op through that instance's
  bridge. JSON goes through @file because PowerShell 5.1 strips quotes. On an extra
  instance Progenitor.Dev rewrites that connect from SteamIPv4 to Lidgren
  (Steam networking is not initialized in the extra process) and supplies a fake
  platform id 90000000000020000+Index (outside the ghost-user id window). Details are in the internal research notes.

  Server prerequisite (this script changes no server files; never point it at C:\VRisingServer):
    a dev-build Progenitor.Dev on a _testserver or progenitor-hosted server with
    [ExtraInstances] AllowExtraInstances = true. The server then keeps its Steam transport for
    players and adds a loopback-only Lidgren listener on game port + 2; connect extra clients
    there (-Connect 127.0.0.1:9988 for a 9986 server). The Steam skip only admits the id band
    from a loopback Lidgren connection. See docs/test-rig.md "Extra clients and headless clients".

  -Headless runs the instance with -batchmode and no window, from the separate install root
  <workspace>\_headless\client when it exists (tools/headless-root.ps1; deploy to it with
  tools/deploy.ps1 -Side client -GameDir <workspace>\_headless\client). The launcher sets
  PROGENITOR_HEADLESS=1 and Bootstrap's HeadlessClient hides -batchmode from game code, which
  otherwise boots a batch-mode process as a server and skips the main menu. -batchmode keeps a
  GPU device; -NoGraphics adds -nographics, which does not reach the menu (evidence 51) and is
  kept only for experiments. -GameRoot picks the install root explicitly (the Steam install or
  a folder under <workspace>\_headless). <workspace> is the folder holding the main checkout
  (tools/common/Workspace.ps1; override with PROGENITOR_WORKSPACE). When it cannot be resolved,
  only the Steam install is allowed.

  A new platform id has no character. Once the join settles, run the create_character op on
  this instance's bridge ({"name":"..."}; idempotent, retry until it answers already=true), then
  run the wake op on the server ({"user":"<name>"}) so the character leaves the crypt sleep and
  becomes visible to other players. docs/test-rig.md has the details.

  Without the Dev extra-auth shim, Steam fails to init in the extra process and
  GetAuthSessionTicket throws before any hail (evidence 2026-09-02 evening §19).

.EXAMPLE
  powershell -File tools/extra-client.ps1 -Index 1
  powershell -File tools/extra-client.ps1 -Index 1 -Headless
  powershell -File tools/extra-client.ps1 -Index 2 -Headless -Connect 127.0.0.1:9988
  powershell -File tools/extra-client.ps1 -Index 1 -Connect 127.0.0.1:9976
  powershell -File tools/extra-client.ps1 -Index 1 -Stop
#>
param(
  [Parameter(Mandatory = $true)]
  [ValidateRange(1, 99)]
  [int]$Index,
  [switch]$Stop,
  [switch]$Headless,
  [switch]$NoGraphics,
  [string]$GameRoot,
  [string]$Connect,
  [string]$Password,
  [int]$TimeoutSeconds = 150
)

$ErrorActionPreference = "Stop"
# call.mjs output is parsed or discarded here: no bridge-queue mail on its stderr (docs/bridge.md#bridge-queue).
$env:PROGENITOR_CALL_NOTES = "0"
$repo = Split-Path -Parent $PSScriptRoot
$call = Join-Path $repo "tools\mcp\call.mjs"
$steamInstall = "C:\Program Files (x86)\Steam\steamapps\common\VRising"
$liveServer = "C:\VRisingServer"
. (Join-Path $PSScriptRoot "common\Workspace.ps1")
# Both are $null when the workspace cannot be resolved: then no headless root is allowed (only the
# Steam install), and the test-server refusal below has nothing to compare -- the Steam install
# is never under it.
$testServer = Get-ProgenitorWorkspacePath -Child "_testserver"
$headlessBase = Get-ProgenitorWorkspacePath -Child "_headless"

if ($Index -eq 0) {
  throw "-Index 0 is the primary Steam-launched client. Use tools/launch-client.ps1."
}
if ($Index -lt 1 -or $Index -gt 99) {
  throw "-Index must be 1..99 (0 is the primary Steam client; use tools/launch-client.ps1)"
}

$headlessRoot = if ($headlessBase) { Join-Path $headlessBase "client" } else { $null }
# Headless instances default to their own install root (tools/headless-root.ps1): a private
# BepInEx tree with only Progenitor, so the player's client and its plugins are never touched.
if (-not $GameRoot) {
  if ($Headless -and $headlessRoot -and (Test-Path -LiteralPath (Join-Path $headlessRoot "VRising.exe"))) { $GameRoot = $headlessRoot }
  else { $GameRoot = $steamInstall }
}
$gameDir = $GameRoot
$gameDirFull = [System.IO.Path]::GetFullPath($gameDir).TrimEnd([char]92)
if ($gameDirFull -like "$liveServer*") {
  throw "refusing to launch extra client from the live dedicated server ($liveServer)"
}
if ($testServer -and $gameDirFull -like "$testServer*") {
  throw "refusing to launch extra client from the staged test server ($testServer)"
}
$isSteamRoot = [string]::Equals($gameDirFull, $steamInstall, [StringComparison]::OrdinalIgnoreCase)
$isHeadlessRoot = [bool]$headlessBase -and $gameDirFull.StartsWith($headlessBase.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
if (-not ($isSteamRoot -or $isHeadlessRoot)) {
  throw "refusing: extra clients launch the Steam install ($steamInstall) or a root under <workspace>\_headless ($headlessBase) (got $gameDirFull)"
}

$exe = Join-Path $gameDir "VRising.exe"
if (-not (Test-Path -LiteralPath $exe)) {
  throw "Steam V Rising install not found (missing $exe)"
}
$exeFull = [System.IO.Path]::GetFullPath($exe)
if ($exeFull -like "$liveServer*" -or ($testServer -and $exeFull -like "$testServer*")) {
  throw "refusing to launch extra client from live server or test server ($exeFull)"
}

$workDir = Join-Path $env:TEMP "progenitor-client-$Index"
$logFile = Join-Path $workDir "Player.log"
$tokenFile = Join-Path $workDir "progenitor-dev.token"
$pidFile = Join-Path $workDir "pid.txt"
$bridgePort = 27341 + (10 * $Index)
$instance = "client-$Index"

function Get-RecordedPid {
  if (-not (Test-Path -LiteralPath $pidFile)) { return $null }
  $raw = (Get-Content -LiteralPath $pidFile -Raw -ErrorAction SilentlyContinue)
  if ($null -eq $raw) { return $null }
  $text = $raw.Trim()
  $parsed = 0
  if (-not [int]::TryParse($text, [ref]$parsed) -or $parsed -le 0) { return $null }
  return $parsed
}

function Get-SteamVRisingProcess {
  param([int]$ProcessId)
  $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $proc) { return $null }
  $path = $null
  try { $path = $proc.Path } catch { $path = $null }
  if (-not $path) { return $null }
  $pathFull = $null
  try { $pathFull = [System.IO.Path]::GetFullPath($path) } catch { return $null }
  if (-not [string]::Equals([System.IO.Path]::GetFileName($pathFull), "VRising.exe", [StringComparison]::OrdinalIgnoreCase)) {
    return $null
  }
  # The instance may have been started from either allowed root (-Stop does not repeat -Headless).
  foreach ($root in @($gameDirFull, $steamInstall, $headlessRoot | Where-Object { $_ })) {
    $under = $root.TrimEnd('\') + '\'
    if ($pathFull.StartsWith($under, [StringComparison]::OrdinalIgnoreCase)) { return $proc }
  }
  return $null
}

function Stop-ExtraClient {
  $recorded = Get-RecordedPid
  if ($null -eq $recorded) {
    Write-Host "extra client-${Index}: no recorded pid at $pidFile"
    return
  }
  $proc = Get-Process -Id $recorded -ErrorAction SilentlyContinue
  if (-not $proc) {
    Write-Host "extra client-${Index}: pid $recorded not running"
    return
  }
  $matched = Get-SteamVRisingProcess -ProcessId $recorded
  if (-not $matched) {
    $path = $null
    try { $path = $proc.Path } catch {}
    throw "recorded pid $recorded is not a VRising.exe under the Steam install or the headless root (path=$path); refusing to kill by name"
  }
  $matched | Stop-Process -Force
  Write-Host "stopped extra client-$Index pid $recorded"
}

function Send-ConnectOp {
  param(
    [string]$Target,
    [string]$JoinPassword
  )
  $addr = $Target.Trim()
  $port = 9976
  $colon = $addr.LastIndexOf(":")
  if ($colon -ge 0) {
    $portText = $addr.Substring($colon + 1)
    $parsedPort = 0
    if ([int]::TryParse($portText, [ref]$parsedPort)) {
      $addr = $addr.Substring(0, $colon)
      $port = $parsedPort
    }
  }
  $payload = @{ address = $addr; port = $port }
  if ($null -ne $JoinPassword -and $JoinPassword.Length -gt 0) {
    $payload.password = $JoinPassword
  }
  $json = ConvertTo-Json -InputObject $payload -Compress
  $argsFile = Join-Path $workDir "connect-args.json"
  [System.IO.File]::WriteAllText($argsFile, $json, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "sending connect $addr`:$port on instance $instance (port $bridgePort)"
  $output = & node $call client connect "@$argsFile"
  $code = $LASTEXITCODE
  if ($output -is [System.Array]) {
    $output = $output -join "`n"
  }
  if ($output) {
    Write-Host $output
  }
  return $code
}

function Test-ExtraBridge {
  try {
    if (-not (Test-Path -LiteralPath $tokenFile)) { return $false }
    $out = & node $call client ping 2>$null
    return ($LASTEXITCODE -eq 0 -and $out)
  } catch { return $false }
}

if ($Stop) {
  Stop-ExtraClient
  exit 0
}

New-Item -ItemType Directory -Force -Path $workDir | Out-Null
# Seed client settings from the main install so the extra instance skips the first-launch
# prompts (brightness calibration etc.). Verified live 2026-09-03: a fresh persistentDataPath
# stops at the calibration screen and the connect op cannot fire from there.
$mainSettings = Join-Path $env:USERPROFILE "AppData\LocalLow\Stunlock Studios\VRising\Settings"
$extraSettings = Join-Path $workDir "Settings"
if ((Test-Path $mainSettings) -and -not (Test-Path (Join-Path $extraSettings "v4"))) {
  Copy-Item -Path $mainSettings -Destination $extraSettings -Recurse -Force
  Write-Host "extra client-${Index}: seeded Settings from the main install"
}

$recorded = Get-RecordedPid
$running = $null
if ($null -ne $recorded) {
  $running = Get-SteamVRisingProcess -ProcessId $recorded
}

if ($running) {
  Write-Host "extra client-$Index already running (pid $($running.Id), port $bridgePort)"
} else {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.UseShellExecute = $false
  $psi.FileName = $exeFull
  $psi.WorkingDirectory = $gameDirFull
  $psi.Arguments = "-logFile `"$logFile`" -persistentDataPath `"$workDir`" -noVideo"
  if ($Headless) {
    # -batchmode alone: no window, but Unity still creates a GPU device, so HDRP and the UI
    # shaders load and the join proceeds. Adding -nographics (null device) stalls before the
    # client world is created (evidence 51: "Stunlock/UI/UI shader is not supported").
    # A tiny render size keeps HDRP's render targets small; nothing looks at this screen.
    $psi.Arguments = $psi.Arguments + " -batchmode -screen-width 256 -screen-height 144 -screen-fullscreen 0"
    if ($NoGraphics) { $psi.Arguments = $psi.Arguments + " -nographics" }
    $psi.CreateNoWindow = $true
  }
  $psi.EnvironmentVariables["PROGENITOR_DEV_PORT"] = [string]$bridgePort
  $psi.EnvironmentVariables["PROGENITOR_DEV_TOKEN_FILE"] = $tokenFile
  $psi.EnvironmentVariables["PROGENITOR_INSTANCE"] = $instance
  if ($Headless) {
    # Bootstrap's HeadlessClient hides -batchmode from game code (UIEntryPoint would otherwise
    # boot this process as a server and skip the main menu).
    $psi.EnvironmentVariables["PROGENITOR_HEADLESS"] = "1"
  }

  $proc = [System.Diagnostics.Process]::Start($psi)
  if (-not $proc) {
    throw "failed to start $exeFull"
  }
  [System.IO.File]::WriteAllText($pidFile, [string]$proc.Id, (New-Object System.Text.UTF8Encoding($false)))
  $headlessNote = ""
  if ($Headless) { $headlessNote = " headless" }
  Write-Host "started extra client-$Index pid $($proc.Id) port $bridgePort$headlessNote"
  Write-Host "  exe: $exeFull"
  Write-Host "  data/log: $workDir"
  Write-Host "  token: $tokenFile"
}

$prevPort = $env:PROGENITOR_CLIENT_PORT
$prevToken = $env:PROGENITOR_CLIENT_TOKEN_FILE
$env:PROGENITOR_CLIENT_PORT = [string]$bridgePort
$env:PROGENITOR_CLIENT_TOKEN_FILE = $tokenFile
$exitCode = 1
try {
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $done = $false
  while ((Get-Date) -lt $deadline) {
    $aliveId = Get-RecordedPid
    if ($null -ne $aliveId -and -not (Get-Process -Id $aliveId -ErrorAction SilentlyContinue)) {
      Write-Warning "extra client-$Index pid $aliveId exited before the bridge answered"
      $exitCode = 1
      $done = $true
      break
    }
    if (Test-ExtraBridge) {
      Write-Host "extra client-$Index bridge is up on 127.0.0.1:$bridgePort instance=$instance"
      & node $call client ping 2>$null | Select-Object -First 8
      if ($null -ne $Connect -and $Connect.Trim().Length -gt 0) {
        $exitCode = Send-ConnectOp -Target $Connect -JoinPassword $Password
      } else {
        $exitCode = 0
      }
      $done = $true
      break
    }
    Start-Sleep -Seconds 3
  }
  if (-not $done) {
    Write-Warning "extra client-$Index bridge did not answer on 127.0.0.1:$bridgePort within $TimeoutSeconds s (token $tokenFile)"
    $exitCode = 1
  }
} finally {
  if ($null -eq $prevPort -or $prevPort -eq "") { Remove-Item Env:\PROGENITOR_CLIENT_PORT -ErrorAction SilentlyContinue }
  else { $env:PROGENITOR_CLIENT_PORT = $prevPort }
  if ($null -eq $prevToken -or $prevToken -eq "") { Remove-Item Env:\PROGENITOR_CLIENT_TOKEN_FILE -ErrorAction SilentlyContinue }
  else { $env:PROGENITOR_CLIENT_TOKEN_FILE = $prevToken }
}
exit $exitCode

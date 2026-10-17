<#
.SYNOPSIS
  Start the V Rising client through Steam and wait for the Progenitor client bridge.

.DESCRIPTION
  Uses steam://run/1604030 so the user's Steam launch options (EAC off) apply. Polls the
  client debug bridge (127.0.0.1:27341 via tools/mcp/call.mjs) until it answers "ping" or the
  timeout passes. Prints the bridge status when ready. Does nothing if the client is already
  running unless -Restart.

  -Restart first sends the bridge "quit" op (UnityEngine.Application.Quit, which runs
  Application.quitting and promotes a staged Progenitor.core.next under
  BepInEx/plugins/Progenitor/progenitor-core and Progenitor.Bootstrap.dll.next beside Bootstrap), waits
  up to 30 s for the process to exit, then falls back to Stop-Process on the path-matched
  VRising.exe.
  Without -Force, -Restart refuses when a client world exists in the bridge ping worlds list
  (the user is in a game) unless -Quit is also passed. A client sitting at the main menu
  (ping answers, no Client_* world) can be restarted without -Force. If the process is
  running but the bridge does not answer, -Restart also refuses without -Force or -Quit
  (cannot tell whether the user is playing).

  Application.quitting and promotes a staged Progenitor.core.next (under BepInEx/plugins/Progenitor/progenitor-core)
  and Progenitor.Bootstrap.dll.next (beside Bootstrap)), waits up to 30 s for the process to exit, then falls back
  so the menu joins a dedicated server. Default target is the staged test server at 127.0.0.1:9976.
  Pass -Connect "" to skip the join. -Password is the optional server join password.
  -Protocol SteamIPv4 (default) or Lidgren is passed through on the connect payload. The LanMode
  test server only opens a Lidgren socket, so the Steam client cannot join it with SteamIPv4.

  -Host <saveName> is mutually exclusive with -Connect: it sends the client "host" op (Private Game /
  Host New) instead, so the menu starts a hosted child server. Default save name when -Host is given
  empty is progenitor-hosted. -Password then becomes the host password. JSON args go through a temp
  file with '@file' because PowerShell 5.1 strips quotes.

.EXAMPLE
  powershell -File tools/launch-client.ps1
  powershell -File tools/launch-client.ps1 -TimeoutSeconds 180
  powershell -File tools/launch-client.ps1 -Connect 127.0.0.1:9976
  powershell -File tools/launch-client.ps1 -Connect 127.0.0.1:9976 -Protocol Lidgren
  powershell -File tools/launch-client.ps1 -Connect 127.0.0.1:9976 -Password secret
  powershell -File tools/launch-client.ps1 -Host progenitor-hosted
  powershell -File tools/launch-client.ps1 -Host progenitor-hosted -Password secret
  powershell -File tools/launch-client.ps1 -Restart
  powershell -File tools/launch-client.ps1 -Restart -Quit
  powershell -File tools/launch-client.ps1 -Restart -Force
#>
param(
  [int]$TimeoutSeconds = 150,
  [switch]$Restart,
  [switch]$Force,
  # With -Restart: allow quitting even when ping.worlds contains a Client_* world.
  [switch]$Quit,
  [string]$Connect = "127.0.0.1:9976",
  [string]$Password,
  # Direct-Connect transport forwarded to the connect op. Lidgren is the default because
  # the staged test server in LanMode only opens a Lidgren socket (SteamIPv4 cannot hail it).
  [ValidateSet("SteamIPv4", "Lidgren")]
  [string]$Protocol = "SteamIPv4",
  # -Host is the switch the caller uses; $Host is a PowerShell automatic variable, so bind to HostSaveName.
  [Alias("Host")]
  [string]$HostSaveName
)
$ErrorActionPreference = "Stop"
# call.mjs output is parsed or discarded here: no bridge-queue mail on its stderr (docs/bridge.md#bridge-queue).
$env:PROGENITOR_CALL_NOTES = "0"
$repo = Split-Path -Parent $PSScriptRoot
$call = Join-Path $repo "tools\mcp\call.mjs"
$gameDir = "C:\Program Files (x86)\Steam\steamapps\common\VRising"
$wantHost = $PSBoundParameters.ContainsKey("HostSaveName") -or $PSBoundParameters.ContainsKey("Host")
$wantConnect = $PSBoundParameters.ContainsKey("Connect")
if ($wantHost -and $wantConnect) {
  throw "-Host and -Connect are mutually exclusive"
}

function Get-ClientProcess {
  Get-Process -Name VRising -ErrorAction SilentlyContinue | Where-Object {
    $p = $null; try { $p = $_.Path } catch {}
    $p -and $p -like "$gameDir*"
  }
}
function Test-Bridge {
  try {
    $out = & node $call client ping 2>$null
    return ($LASTEXITCODE -eq 0 -and $out)
  } catch { return $false }
}

function Get-BridgePing {
  try {
    $out = & node $call client ping 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $out) { return $null }
    if ($out -is [System.Array]) { $out = $out -join "`n" }
    return $out | ConvertFrom-Json
  } catch { return $null }
}

function Test-HasClientWorld {
  param($ping)
  if (-not $ping) { return $false }
  foreach ($w in @($ping.worlds)) {
    if ($null -eq $w) { continue }
    $name = [string]$w
    if ($name.StartsWith("Client")) { return $true }
  }
  return $false
}

function Stop-ClientProcess {
  param($procs)
  if (-not $procs) { return }
  $procs | Stop-Process -Force
  Write-Host "closed client pid $($procs.Id -join ', ')"
  Start-Sleep -Seconds 3
}

function Send-ConnectOp {
  param(
    [string]$Target,
    [string]$JoinPassword,
    [string]$JoinProtocol
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
  if ($JoinPassword -ne $null -and $JoinPassword.Length -gt 0) {
    $payload.password = $JoinPassword
  }
  if ($JoinProtocol -ne $null -and $JoinProtocol.Trim().Length -gt 0) {
    $payload.protocol = $JoinProtocol.Trim()
  }
  # -InputObject: piping a hashtable enumerates DictionaryEntry objects in Windows PowerShell 5.1.
  $json = ConvertTo-Json -InputObject $payload -Compress
  # PowerShell 5.1 strips the double quotes when passing JSON to node; hand it over as a file.
  $argsFile = Join-Path $env:TEMP "progenitor-connect-args.json"
  [System.IO.File]::WriteAllText($argsFile, $json, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "sending connect $addr`:$port protocol=$JoinProtocol"
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

function Send-HostOp {
  param(
    [string]$SaveName,
    [string]$HostPassword
  )
  if ($SaveName -eq $null -or $SaveName.Trim().Length -eq 0) {
    $SaveName = "progenitor-hosted"
  } else {
    $SaveName = $SaveName.Trim()
  }
  $payload = @{ saveName = $SaveName; lan = $true }
  if ($HostPassword -ne $null -and $HostPassword.Length -gt 0) {
    $payload.password = $HostPassword
  }
  $json = ConvertTo-Json -InputObject $payload -Compress
  $argsFile = Join-Path $env:TEMP "progenitor-host-args.json"
  [System.IO.File]::WriteAllText($argsFile, $json, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "sending host saveName=$SaveName"
  $output = & node $call client host "@$argsFile"
  $code = $LASTEXITCODE
  if ($output -is [System.Array]) {
    $output = $output -join "`n"
  }
  if ($output) {
    Write-Host $output
  }
  return $code
}

$running = Get-ClientProcess
if ($running -and $Restart) {
  $ping = Get-BridgePing
  $inWorld = Test-HasClientWorld $ping
  $pids = $running.Id -join ', '
  if ($inWorld -and -not $Force -and -not $Quit) {
    throw "client is in a game (pid $pids, worlds: $($ping.worlds -join ', ')); pass -Force to kill it, or -Quit to request Application.Quit even while in-game"
  }
  if (-not $ping -and -not $Force -and -not $Quit) {
    throw "client is running (pid $pids) but the bridge did not answer; pass -Force to kill it, or -Quit to try Application.Quit then kill"
  }
  if ($ping) {
    Write-Host "sending quit (Application.Quit via bridge)"
    & node $call client quit 2>$null | Out-Host
    $quitDeadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $quitDeadline) {
      $running = Get-ClientProcess
      if (-not $running) { break }
      Start-Sleep -Seconds 1
    }
  }
  $running = Get-ClientProcess
  if ($running) {
    Write-Host "process still running; Stop-Process on path-matched VRising.exe"
    Stop-ClientProcess $running
  } else {
    Write-Host "client exited after quit"
  }
  # Steam keeps the app marked "running" for a few seconds after the process exits; a
  # steam://run issued inside that window is refused with "already running" (seen live
  # 2026-09-12). Let Steam settle before asking it to launch again.
  Start-Sleep -Seconds 8
  $running = $null
}
if ($running) {
  Write-Host "client already running (pid $($running.Id -join ', '))"
} else {
  # Promote staged restart-only DLLs before launch: Bootstrap's own startup promotion of
  # itself is one launch too late. Same Move-Item + log line as tools/test-server.ps1 start.
  $pluginsDir = Join-Path $gameDir "BepInEx\plugins"
  # The single-folder install (BepInEx/plugins/Progenitor), then the three-location layout until a
  # single-folder Bootstrap's first start retires it.
  $homeDir = Join-Path $pluginsDir "Progenitor"
  foreach ($core in @((Join-Path $homeDir "progenitor-core\Progenitor.core"), (Join-Path $gameDir "BepInEx\progenitor-core\Progenitor.dll"))) {
    if (Test-Path "$core.next") {
      Move-Item "$core.next" $core -Force
      Write-Host "promoted $(Split-Path $core -Leaf).next"
    }
  }
  foreach ($bootstrap in @((Join-Path $homeDir "Progenitor.Bootstrap.dll"), (Join-Path $pluginsDir "Progenitor.Bootstrap.dll"))) {
    if (Test-Path "$bootstrap.next") { Move-Item "$bootstrap.next" $bootstrap -Force; Write-Host "promoted Progenitor.Bootstrap.dll.next" }
  }
  $staleCore = Join-Path $pluginsDir "Progenitor.dll"
  foreach ($p in @($staleCore, "$staleCore.next", "$staleCore.prev")) {
    if (Test-Path $p) {
      Remove-Item $p -Force
      Write-Host "removed stale $(Split-Path $p -Leaf) from plugins (Core is plugins/Progenitor/progenitor-core/Progenitor.core)"
    }
  }
  # Job FENC2 (roadmap M4 item 5), documented and deliberately NOT implemented here.
  #
  # Method-body hot reload needs DOTNET_MODIFIABLE_ASSEMBLIES=debug in the game process's
  # environment before CoreCLR starts. This line hands a URI to the already-running Steam client,
  # so the game inherits STEAM's environment block, not this script's: setting the variable above
  # would have no effect, and Windows Steam launch options are appended as arguments (the
  # `VAR=value %command%` form is Linux-only). Doorstop cannot inject it either — this
  # doorstop_config.ini has no env section. Measured (internal research notes).
  #
  # Two options, both dev-loop only, neither a default and neither wired up here:
  #
  #   1. A user-level environment variable, then restart Steam so it re-reads the environment:
  #        [Environment]::SetEnvironmentVariable('DOTNET_MODIFIABLE_ASSEMBLIES','debug','User')
  #      Coarse — every .NET app Steam launches gets Edit-and-Continue — but reversible (set it
  #      back to $null and restart Steam) and needs no code.
  #   2. Launch VRising.exe directly with the variable set, replicating the EAC-off arguments the
  #      user's Steam launch options carry. That loses the "the user's launch options apply"
  #      property this script's synopsis promises, so it would have to be an opt-in switch.
  #
  # The server side needs neither: `tools/test-server.ps1 start -EditAndContinue` sets it on the
  # child it creates. Start there. When the variable is absent the reloader simply logs
  # "Reload: EnC unavailable — …" once and every .delta is refused; nothing else changes.
  Start-Process "steam://run/1604030"
  Write-Host "launch requested through Steam (app 1604030)"
  # If Steam refused the launch (no client process within 20 s), ask once more.
  $appearDeadline = (Get-Date).AddSeconds(20)
  while ((Get-Date) -lt $appearDeadline -and -not (Get-ClientProcess)) { Start-Sleep -Seconds 2 }
  if (-not (Get-ClientProcess)) {
    Write-Host "no client process yet; asking Steam again"
    Start-Process "steam://run/1604030"
  }
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
  if (Test-Bridge) {
    Write-Host "client bridge is up"
    & node $call client status 2>$null | Select-Object -First 6
    if ($wantHost) {
      $hostCode = Send-HostOp -SaveName $HostSaveName -HostPassword $Password
      if ($hostCode -ne 0) {
        exit $hostCode
      }
    } elseif ($Connect -ne $null -and $Connect.Trim().Length -gt 0) {
      $connectCode = Send-ConnectOp -Target $Connect -JoinPassword $Password -JoinProtocol $Protocol
      if ($connectCode -ne 0) {
        exit $connectCode
      }
    }
    exit 0
  }
  Start-Sleep -Seconds 3
}
Write-Warning "client bridge did not answer within $TimeoutSeconds s"
exit 1

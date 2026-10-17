<#
.SYNOPSIS
  Stage a throwaway V Rising dedicated test server from the bundled server that ships with the Steam client.

.DESCRIPTION
  Copies <game>\VRising_Server to a separate folder, drops the client's BepInEx loader on top
  (core, patchers, doorstop, the CoreCLR runtime), and writes a start script that keeps
  save data, logs, and ports away from any live server on this machine.

  Never points at C:\VRisingServer. That one has players.

.PARAMETER GameDir
  The Steam V Rising install. Defaults to the standard Steam path.

.PARAMETER TestRoot
  Where the test server lives. Defaults to <workspace>\_testserver, where <workspace> is the folder
  holding the main checkout (tools/common/Workspace.ps1; override with PROGENITOR_WORKSPACE).

.PARAMETER Port
  Game port. Query port is Port + 1. Defaults to 9976 so it never collides with a default 9876 server.

.EXAMPLE
  .\tools\stage-test-server.ps1
  .\tools\stage-test-server.ps1 -TestRoot D:\vr-test -Port 9990
#>
[CmdletBinding()]
param(
  [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\VRising",
  [string]$TestRoot = "",
  [int]$Port = 9976
)

$ErrorActionPreference = "Stop"

# The one fence (tools/common/Fence.ps1, R40 finding 4). Dot-sourced and asserted FIRST, on
# every root this script reads or writes -- not just TestRoot, and before the first Test-Path.
# A -GameDir pointing at the live server would mirror its files out of it, and -TestRoot is
# only half the surface (docs/dev-loop.md, "Deploy audit 2026-09-06").
#
# It matters here more than anywhere: robocopy /MIR below DELETES destination files absent from
# the source. The old `-like "C:\VRisingServer*"` compared the raw parameter string, so
# `-GameDir C:/VRisingServer -TestRoot C:/VRisingServer` passed the fence and reached /MIR.
# Assert-NotLiveServerPath canonicalizes first (slashes, \\?\, admin share, 8.3, junctions).
. (Join-Path $PSScriptRoot "common\Fence.ps1")
# Fence.ps1 dot-sources common\Workspace.ps1; an unresolvable workspace throws here.
if (-not $TestRoot) { $TestRoot = Resolve-ProgenitorWorkspacePath -Child "_testserver" -Parameter "-TestRoot" }

$bundled = Join-Path $GameDir "VRising_Server"
$serverDir = Join-Path $TestRoot "server"
$saveDir = Join-Path $TestRoot "save-data"
$logDir = Join-Path $TestRoot "logs"

foreach ($root in @($TestRoot, $GameDir, $bundled, $serverDir, $saveDir, $logDir)) {
  Assert-NotLiveServerPath $root
}

if (-not (Test-Path (Join-Path $bundled "VRisingServer.exe"))) {
  throw "Bundled server not found at $bundled. Check -GameDir."
}

Write-Host "Staging test server into $serverDir"
New-Item -ItemType Directory -Force -Path $serverDir, $saveDir, $logDir, (Join-Path $serverDir "BepInEx\plugins") | Out-Null

# 1. Game files. robocopy /MIR keeps the copy in sync with the Steam install after a game update.
$rc = & robocopy $bundled $serverDir /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 /XD BepInEx
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

# 2. Loader. Everything BepInEx needs except interop/cache (the server generates its own on first launch)
#    and plugins (deployed separately by deploy.ps1).
foreach ($name in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version")) {
  $src = Join-Path $GameDir $name
  if (Test-Path $src) { Copy-Item $src (Join-Path $serverDir $name) -Force }
}
foreach ($sub in @("core", "patchers", "unity-libs")) {
  $src = Join-Path $GameDir "BepInEx\$sub"
  if (Test-Path $src) {
    & robocopy $src (Join-Path $serverDir "BepInEx\$sub") /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
  }
}
# CoreCLR runtime that doorstop points at (coreclr_path = dotnet\coreclr.dll).
& robocopy (Join-Path $GameDir "dotnet") (Join-Path $serverDir "dotnet") /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null

# 3. Host settings: distinct ports, no public listing, no password, LAN off (direct connect by IP:port).
$settingsDir = Join-Path $saveDir "Settings"
Assert-NotLiveServerPath $settingsDir
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
$hostDefaults = Get-Content (Join-Path $serverDir "VRisingServer_Data\StreamingAssets\Settings\ServerHostSettings.json") -Raw | ConvertFrom-Json
$hostDefaults.Name = "Progenitor Test"
$hostDefaults.Port = $Port
$hostDefaults.QueryPort = $Port + 1
$hostDefaults.ListOnSteam = $false
$hostDefaults.ListOnEOS = $false
$hostDefaults.MaxConnectedUsers = 4
$hostDefaults | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $settingsDir "ServerHostSettings.json") -Encoding utf8
Copy-Item (Join-Path $serverDir "VRisingServer_Data\StreamingAssets\Settings\ServerGameSettings.json") (Join-Path $settingsDir "ServerGameSettings.json") -Force

# 3b. Progenitor developer-tree config. Reload.WatchCore ships FALSE (job FRLD): watching
#     plugins/Progenitor/progenitor-core/Progenitor.core.next is an automatic code-loading path and must be
#     opt-in for anyone who installs a release. This is a throwaway developer tree, so the
#     entry is written explicitly here (tools/deploy.ps1 re-asserts it on every deploy) and
#     the in-process Core hot swap keeps working. Never run against C:\VRisingServer: the
#     fence above already refuses that root, in every spelling of it.
$cfgDir = Join-Path $serverDir "BepInEx\config"
Assert-NotLiveServerPath $cfgDir
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfgPath = Join-Path $cfgDir "progenitor.cfg"
if (-not (Test-Path -LiteralPath $cfgPath)) {
  $utf8NoBom = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllLines(
    $cfgPath,
    @("## Written by tools/stage-test-server.ps1: this is a developer tree.", "[Reload]", "WatchCore = true"),
    $utf8NoBom)
  Write-Host "Seeded Reload.WatchCore = true in $cfgPath"
} else {
  Write-Host "Kept existing $cfgPath (tools/deploy.ps1 re-asserts Reload.WatchCore = true)"
}

# 4. Start script. Run it from its own console; Ctrl-C stops the server.
$start = @"
@echo off
set SteamAppId=1604030
cd /d "$serverDir"
VRisingServer.exe -persistentDataPath "$saveDir" -serverName "Progenitor Test" -saveName "test1" -logFile "$logDir\VRisingServer.log"
"@
Set-Content (Join-Path $TestRoot "start-test-server.bat") $start -Encoding ascii

Write-Host ""
Write-Host "Done."
Write-Host "  Server:   $serverDir"
Write-Host "  Plugins:  $serverDir\BepInEx\plugins   (use tools\deploy.ps1)"
Write-Host "  Start:    $TestRoot\start-test-server.bat"
Write-Host "  Connect:  Direct connect 127.0.0.1:$Port from the client"
Write-Host "  First launch builds the server's interop cache; expect a couple of minutes before it listens."
# robocopy exit codes below 8 mean success; do not let the last one leak out as the script's exit code.
exit 0

<#
.SYNOPSIS
  Start, stop, or inspect the staged test server.

.EXAMPLE
  .\tools\test-server.ps1 start
  .\tools\test-server.ps1 stop
  .\tools\test-server.ps1 status
  .\tools\test-server.ps1 log            # tail BepInEx LogOutput.log
  .\tools\test-server.ps1 log -Lines 200 -Filter Spike
  .\tools\test-server.ps1 start -EditAndContinue   # method-body hot reload (docs/dev-loop.md)
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)] [ValidateSet("start", "stop", "restart", "status", "log")] [string]$Action = "status",
  [string]$TestRoot = "",   # default <workspace>\_testserver (tools/common/Workspace.ps1)
  [int]$Lines = 60,
  [string]$Filter = "",
  # Job FENC2 (roadmap M4 item 5). Start the server with DOTNET_MODIFIABLE_ASSEMBLIES=debug in its
  # process environment so MetadataUpdater.ApplyUpdate works and the reloader can take a .delta
  # from tools/enc --watch. OFF by default and never set by anything Progenitor ships: it enables
  # Edit-and-Continue for the whole process, and a new automatic code path with it.
  #
  # It has to be in the environment block the child is CREATED with. The native runtime reads it
  # at startup; setting it in this shell, or from managed code inside the server, does nothing
  # (measured; internal research notes).
  [switch]$EditAndContinue
)

$ErrorActionPreference = "Stop"

# The one fence (tools/common/Fence.ps1, R40 finding 4). This script promotes staged DLLs,
# deletes a stale Core out of plugins/ and starts a process with -TestRoot's tree as its
# working directory, so -TestRoot is a root parameter like any other and goes through the
# fence before the first Move-Item / Remove-Item / Win32_Process.Create.
. (Join-Path $PSScriptRoot "common\Fence.ps1")
# Fence.ps1 dot-sources common\Workspace.ps1; an unresolvable workspace throws here.
if (-not $TestRoot) { $TestRoot = Resolve-ProgenitorWorkspacePath -Child "_testserver" -Parameter "-TestRoot" }
Assert-NotLiveServerPath $TestRoot

$serverDir = Join-Path $TestRoot "server"
$bat = Join-Path $TestRoot "start-test-server.bat"
$log = Join-Path $serverDir "BepInEx\LogOutput.log"
foreach ($root in @($serverDir, $bat, $log)) { Assert-NotLiveServerPath $root }

function Get-TestServerProcess {
  Get-Process -Name VRisingServer -ErrorAction SilentlyContinue | Where-Object {
    $path = $null
    try { $path = $_.Path } catch {}
    $path -and $path -like "$serverDir*"
  }
}

function Promote-Staged([string]$current, [string]$label) {
  if (Test-Path "$current.next") {
    $dir = Split-Path $current -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Move-Item "$current.next" $current -Force
    Write-Host "promoted $label"
  }
}

function Remove-StaleChainloaderCore([string]$pluginsDir) {
  # Core lives in progenitor-core/; a leftover plugins copy would steal the Progenitor identity.
  $stale = Join-Path $pluginsDir "Progenitor.dll"
  foreach ($p in @($stale, "$stale.next", "$stale.prev")) {
    if (Test-Path $p) {
      Remove-Item $p -Force
      Write-Host "removed stale $(Split-Path $p -Leaf) from plugins (Core lives in progenitor-core)"
    }
  }
}

function Start-TestServer([string]$Launch = "") {
  if (Get-TestServerProcess) { Write-Host "already running"; return }
  # A Core or Bootstrap staged by deploy.ps1 while the server was running is promoted here: a
  # forced stop never runs the in-game promote-at-exit, and promoting Bootstrap from inside the
  # old copy is one launch too late. Same Move-Item + log line for each restart-only DLL.
  $pluginsDir = Join-Path $serverDir "BepInEx\plugins"
  $coreDir = Join-Path $serverDir "BepInEx\progenitor-core"
  Promote-Staged (Join-Path $coreDir "Progenitor.dll") "Progenitor.dll.next"
  Promote-Staged (Join-Path $pluginsDir "Progenitor.Bootstrap.dll") "Progenitor.Bootstrap.dll.next"
  Remove-StaleChainloaderCore $pluginsDir
  # WMI Create keeps the server alive after this shell exits; Start-Process would put it in our job object.
  # A restart passes the running server's own command line, so it comes back on the same world
  # (-persistentDataPath / -saveName); a plain start uses start-test-server.bat.
  $target = if ($Launch) { $Launch } else { "`"$bat`"" }
  # A relaunch of the servers own command line goes straight to Win32_Process.Create: cmd /c would
  # mangle its leading quoted exe path.
  $commandLine = if ($Launch) { $Launch } else { "cmd.exe /c $target" }
  if ($EditAndContinue) {
    # `set VAR=debug&&` with no space before the && (a space would land in the value) puts the
    # variable in cmd's environment before the .bat runs, so VRisingServer.exe and the CoreCLR
    # Doorstop starts inside it inherit it. This is the one line the research note's S3 needs.
    $commandLine = "cmd.exe /c set DOTNET_MODIFIABLE_ASSEMBLIES=debug&& $target"
    Write-Host "Edit-and-Continue: DOTNET_MODIFIABLE_ASSEMBLIES=debug in the server's environment"
  }
  # The server's console (and the BepInEx console inside it) starts minimized without taking focus:
  # ShowWindow 7 = SW_SHOWMINNOACTIVE. The user works on this machine; a console popping to the front
  # steals their focus.
  $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]7 }
  $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $commandLine; CurrentDirectory = $serverDir; ProcessStartupInformation = $startup }
  if ($r.ReturnValue -ne 0) { throw "Win32_Process.Create failed: $($r.ReturnValue)" }
  Write-Host "started (launcher pid $($r.ProcessId))"
}

function Stop-TestServer {
  $p = Get-TestServerProcess
  if (-not $p) { Write-Host "not running"; return }
  $p | Stop-Process -Force
  Write-Host "stopped pid $($p.Id -join ', ')"
}

switch ($Action) {
  "start"   { Start-TestServer }
  "stop"    { Stop-TestServer }
  "restart" {
    # Keep the running world: read the live server's command line (exe path plus -persistentDataPath,
    # -saveName and the rest) before stopping it, and relaunch exactly that. start-test-server.bat
    # starts the default world, which is wrong when another world (a copy started by its own .bat) is up.
    $launch = ""
    $p = Get-TestServerProcess | Select-Object -First 1
    if ($p) {
      $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine
      if ($cmd -and $cmd -like "*VRisingServer.exe*") { $launch = $cmd; Write-Host "restart keeps the running command line: $cmd" }
      else { Write-Warning "could not read the running server's command line; restarting with start-test-server.bat (the default world)" }
    }
    Stop-TestServer; Start-Sleep -Seconds 2; Start-TestServer -Launch $launch
  }
  "status"  {
    $p = Get-TestServerProcess
    if ($p) { $p | Select-Object Id, StartTime, @{n = 'CPU(s)'; e = { [int]$_.CPU } }, @{n = 'RAM(MB)'; e = { [int]($_.WorkingSet64 / 1MB) } } | Format-Table -AutoSize }
    else { Write-Host "test server not running" }
    if (Test-Path $log) { Write-Host "log: $log ($([int]((Get-Item $log).Length / 1KB)) KB, updated $((Get-Item $log).LastWriteTime))" }
  }
  "log" {
    if (-not (Test-Path $log)) { Write-Host "no log yet at $log"; return }
    $content = Get-Content $log -Tail $Lines
    if ($Filter) { $content = $content | Where-Object { $_ -match $Filter } }
    $content
  }
}

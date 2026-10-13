<#
.SYNOPSIS
  Create (or refresh) a separate client install root for headless extra clients.

.DESCRIPTION
  Headless clients (tools/extra-client.ps1 -Headless) run from their own root instead of the
  Steam install, so they get their own BepInEx tree: their own plugins (Progenitor only, no
  personal client mods), their own config, their own LogOutput.log, and a Bootstrap build
  that can differ from the one the player's client has loaded. Nothing in the Steam install
  is written.

  Layout under -Root (default <workspace>\_headless\client, where <workspace> is the folder holding
  the main checkout; tools/common/Workspace.ps1, override with PROGENITOR_WORKSPACE):
    VRising.exe, GameAssembly.dll, UnityPlayer.dll, baselib.dll, UnityCrashHandler64.exe,
    winhttp.dll            hard links to the Steam install (same volume; re-run after a game update)
    doorstop_config.ini,
    .doorstop_version      copies
    VRising_Data, dotnet   directory junctions to the Steam install (read-only use)
    BepInEx\core, interop, unity-libs   junctions (generated interop is shared, never regenerated here)
    BepInEx\cache          copy (so BepInEx does not regenerate interop against the junction)
    BepInEx\config         BepInEx.cfg copy, progenitor-dev.enable copy
    BepInEx\plugins, progenitor-core, progenitor-reload   filled by
                           tools/deploy.ps1 -Side client -GameDir <Root>

  After a game update: re-run with -Force to relink the binaries.

.EXAMPLE
  powershell -File tools/headless-root.ps1
  powershell -File tools/deploy.ps1 -Side client -GameDir <workspace>\_headless\client -Projects Progenitor.Bootstrap,Progenitor.Core,Progenitor.Dev
#>
param(
  [string]$Root = "",
  [string]$SteamInstall = "C:\Program Files (x86)\Steam\steamapps\common\VRising",
  [switch]$Force
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common\Workspace.ps1")
# The test server is a forbidden root. If the workspace cannot be resolved its location is
# unknown, so this refuses outright rather than run with a shorter forbidden list.
$testServer = Resolve-ProgenitorWorkspacePath -Child "_testserver"
if (-not $Root) { $Root = Resolve-ProgenitorWorkspacePath -Child "_headless\client" -Parameter "-Root" }
$rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
$steamFull = [System.IO.Path]::GetFullPath($SteamInstall).TrimEnd('\')

foreach ($forbidden in @("C:\VRisingServer", $testServer, $steamFull)) {
  if ($rootFull.StartsWith($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing: headless root may not live under $forbidden (got $rootFull)"
  }
}
if (-not (Test-Path -LiteralPath (Join-Path $steamFull "VRising.exe"))) {
  throw "Steam V Rising install not found at $steamFull"
}

function New-Dir([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Path $path | Out-Null }
}

function Set-HardLink([string]$name) {
  $src = Join-Path $steamFull $name
  $dst = Join-Path $rootFull $name
  if (Test-Path -LiteralPath $dst) {
    if (-not $Force) { return }
    Remove-Item -LiteralPath $dst -Force
  }
  New-Item -ItemType HardLink -Path $dst -Target $src | Out-Null
  Write-Host "link  $name"
}

function Set-Junction([string]$relative) {
  $src = Join-Path $steamFull $relative
  $dst = Join-Path $rootFull $relative
  if (Test-Path -LiteralPath $dst) { return }
  New-Item -ItemType Junction -Path $dst -Target $src | Out-Null
  Write-Host "junct $relative"
}

function Copy-IfMissing([string]$relative) {
  $src = Join-Path $steamFull $relative
  $dst = Join-Path $rootFull $relative
  if ((Test-Path -LiteralPath $dst) -and -not $Force) { return }
  if (-not (Test-Path -LiteralPath $src)) { return }
  Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
  Write-Host "copy  $relative"
}

New-Dir $rootFull
foreach ($f in @("VRising.exe", "GameAssembly.dll", "UnityPlayer.dll", "baselib.dll", "UnityCrashHandler64.exe", "winhttp.dll")) {
  Set-HardLink $f
}
Copy-IfMissing "doorstop_config.ini"
Copy-IfMissing ".doorstop_version"
Set-Junction "VRising_Data"
Set-Junction "dotnet"

$bep = Join-Path $rootFull "BepInEx"
New-Dir $bep
foreach ($d in @("core", "interop", "unity-libs")) { Set-Junction "BepInEx\$d" }
foreach ($d in @("patchers", "plugins", "config", "progenitor-core", "progenitor-reload")) { New-Dir (Join-Path $bep $d) }
Copy-IfMissing "BepInEx\cache"
Copy-IfMissing "BepInEx\config\BepInEx.cfg"
Copy-IfMissing "BepInEx\config\progenitor-dev.enable"

Write-Host "headless root ready: $rootFull"
Write-Host "next: powershell -File tools/deploy.ps1 -Side client -GameDir `"$rootFull`""

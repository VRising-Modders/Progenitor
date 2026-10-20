<#
.SYNOPSIS
  Removes Progenitor from V Rising or a V Rising dedicated server.

.DESCRIPTION
  Finds the game through Steam (or uses -GamePath), refuses while that install is running, then moves
  Progenitor's files into BepInEx\progenitor-backups\uninstall-<time>: Bootstrap, the preloader, Core's
  folder, the reload and pending folders with every mod in them (also mods copied there by hand), the
  mods Progenitor installed elsewhere, the self-install record, the install ledger, a waiting removal
  request from the Mods menu, kept versions, logs and caches, and Progenitor's settings unless
  -KeepSettings. Other mods in BepInEx\plugins and BepInEx itself stay, unless -RemoveBepInEx. Saved
  worlds are never touched.

  Read this script before you run it:
    https://vrising-modders.github.io/Progenitor/uninstall.ps1

  One line, in PowerShell:
    irm https://vrising-modders.github.io/Progenitor/uninstall.ps1 | iex

  With options:
    & ([scriptblock]::Create((irm https://vrising-modders.github.io/Progenitor/uninstall.ps1))) -GamePath "D:\Games\VRising" -KeepSettings

  Downloaded:
    powershell -ExecutionPolicy Bypass -File uninstall.ps1 -GamePath "D:\Games\VRising"

  With "irm | iex" no options can be passed; set them as environment variables first instead:
    PROGENITOR_GAME_PATH, PROGENITOR_SERVER=1, PROGENITOR_WHATIF=1, PROGENITOR_FORCE=1,
    PROGENITOR_KEEP_SETTINGS=1, PROGENITOR_REMOVE_BEPINEX=1, PROGENITOR_STEAM_PATH.

.PARAMETER GamePath
  The folder that holds VRising.exe or VRisingServer.exe. Default: V Rising in your Steam libraries.

.PARAMETER Server
  Remove it from the dedicated server: "V Rising Dedicated Server" in your Steam libraries, or -GamePath.

.PARAMETER WhatIf
  Print what would be removed and change nothing.

.PARAMETER Force
  Go on when a V Rising process is running whose folder Windows does not name. Use it only when that
  process runs from another folder.

.PARAMETER KeepSettings
  Keep Progenitor's settings files (BepInEx\config\progenitor*), for a later reinstall.

.PARAMETER RemoveBepInEx
  Also remove BepInEx itself. Every other BepInEx mod in this folder stops loading.

.PARAMETER SteamPath
  Look for the game in this Steam folder instead of the one Windows has registered.
#>
[CmdletBinding()]
param(
  [string]$GamePath = $env:PROGENITOR_GAME_PATH,
  [switch]$Server = ($env:PROGENITOR_SERVER -match '^(1|true|yes)$'),
  [switch]$WhatIf = ($env:PROGENITOR_WHATIF -match '^(1|true|yes)$'),
  [switch]$Force = ($env:PROGENITOR_FORCE -match '^(1|true|yes)$'),
  [switch]$KeepSettings = ($env:PROGENITOR_KEEP_SETTINGS -match '^(1|true|yes)$'),
  [switch]$RemoveBepInEx = ($env:PROGENITOR_REMOVE_BEPINEX -match '^(1|true|yes)$'),
  [string]$SteamPath = $env:PROGENITOR_STEAM_PATH
)

#region shared (install.ps1 and uninstall.ps1 carry the same copy of this region)

function Write-PgLine([string]$Text, [string]$Color = '') {
  if ($Color) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
}

# A full path; a relative one is taken from PowerShell's current location (not the process's working folder).
function Get-PgFullPath([string]$Path) {
  $p = $Path.Trim().Trim('"')
  $p = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($p)
  return [System.IO.Path]::GetFullPath($p).TrimEnd('\')
}

function Test-PgUnder([string]$Path, [string]$Root) {
  $full = [System.IO.Path]::GetFullPath($Path)
  $r = $Root.TrimEnd('\') + '\'
  return $full.StartsWith($r, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-PgSteamRoots([string]$Explicit) {
  $roots = New-Object System.Collections.Generic.List[string]
  if ($Explicit) {
    $roots.Add((Get-PgFullPath $Explicit))
    return , $roots
  }
  $keys = @(
    @('HKCU:\Software\Valve\Steam', 'SteamPath'),
    @('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'),
    @('HKLM:\SOFTWARE\Valve\Steam', 'InstallPath')
  )
  foreach ($k in $keys) {
    $value = $null
    try { $value = (Get-ItemProperty -LiteralPath $k[0] -Name $k[1] -ErrorAction Stop).($k[1]) } catch { $value = $null }
    if (-not $value) { continue }
    $p = Get-PgFullPath ($value -replace '/', '\')
    if ((Test-Path -LiteralPath $p -PathType Container) -and -not ($roots -contains $p)) { $roots.Add($p) }
  }
  return , $roots
}

function Get-PgSteamLibraries([string]$SteamRoot) {
  $libs = New-Object System.Collections.Generic.List[string]
  $libs.Add($SteamRoot)
  foreach ($vdf in @((Join-Path $SteamRoot 'steamapps\libraryfolders.vdf'), (Join-Path $SteamRoot 'config\libraryfolders.vdf'))) {
    if (-not (Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }
    $text = [System.IO.File]::ReadAllText($vdf)
    # Current format: "path" "D:\\SteamLibrary". Older format: "1" "D:\\SteamLibrary".
    foreach ($m in [regex]::Matches($text, '"(?:path|\d+)"\s+"((?:[^"\\]|\\.)*)"')) {
      $raw = $m.Groups[1].Value -replace '\\\\', '\'
      if (-not $raw -or $raw -notmatch '^[A-Za-z]:\\|^\\\\') { continue }
      $p = Get-PgFullPath $raw
      if (-not ($libs -contains $p)) { $libs.Add($p) }
    }
  }
  return , $libs
}

function Find-PgSteamApp([string]$SteamPath, [string]$AppId, [string]$DefaultFolder) {
  foreach ($root in (Get-PgSteamRoots $SteamPath)) {
    foreach ($lib in (Get-PgSteamLibraries $root)) {
      $apps = Join-Path $lib 'steamapps'
      $folder = $DefaultFolder
      $manifest = Join-Path $apps ('appmanifest_' + $AppId + '.acf')
      if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        $m = [regex]::Match([System.IO.File]::ReadAllText($manifest), '"installdir"\s+"([^"]+)"')
        if ($m.Success) { $folder = $m.Groups[1].Value }
      }
      $candidate = Join-Path (Join-Path $apps 'common') $folder
      if (Test-Path -LiteralPath $candidate -PathType Container) { return (Get-PgFullPath $candidate) }
    }
  }
  return $null
}

function Resolve-PgTarget([string]$GamePath, [bool]$Server, [string]$SteamPath) {
  $clientExe = 'VRising.exe'
  $serverExe = 'VRisingServer.exe'
  if ($GamePath) {
    $path = Get-PgFullPath $GamePath
    if (Test-Path -LiteralPath $path -PathType Leaf) { $path = Split-Path -Parent $path }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "There is no folder at $path." }
  }
  else {
    $roots = Get-PgSteamRoots $SteamPath
    if ($roots.Count -eq 0) { throw 'Steam was not found on this PC. Pass the game folder with -GamePath "<folder that holds VRising.exe>".' }
    if ($Server) { $path = Find-PgSteamApp $SteamPath '1829350' 'VRisingDedicatedServer' }
    else { $path = Find-PgSteamApp $SteamPath '1604030' 'VRising' }
    if (-not $path) {
      $what = 'V Rising'
      if ($Server) { $what = 'V Rising Dedicated Server' }
      throw "$what was not found in your Steam libraries. Pass its folder with -GamePath `"<folder>`"."
    }
  }
  $hasClient = Test-Path -LiteralPath (Join-Path $path $clientExe) -PathType Leaf
  $hasServer = Test-Path -LiteralPath (Join-Path $path $serverExe) -PathType Leaf
  if ($Server -and -not $hasServer) { throw "$serverExe is not in $path. Pass the dedicated server's folder with -GamePath." }
  if (-not $Server -and -not $hasClient -and -not $hasServer) { throw "Neither $clientExe nor $serverExe is in $path. Pass the folder that holds one of them with -GamePath." }
  if ($Server -or -not $hasClient) {
    return [pscustomobject]@{ Path = $path; Exe = $serverExe; Side = 'server'; Name = 'the V Rising dedicated server' }
  }
  return [pscustomobject]@{ Path = $path; Exe = $clientExe; Side = 'client'; Name = 'V Rising' }
}

# Every running process whose executable is inside the target folder (matched by full path, never by name), plus the
# V Rising processes whose path Windows does not let this script read.
function Get-PgRunning([string]$Root) {
  $inside = New-Object System.Collections.Generic.List[object]
  $unknown = New-Object System.Collections.Generic.List[object]
  $procs = $null
  try { $procs = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop | Select-Object ProcessId, Name, ExecutablePath) }
  catch {
    $procs = @(Get-Process | ForEach-Object {
        $p = $null
        try { $p = $_.Path } catch { $p = $null }
        [pscustomobject]@{ ProcessId = $_.Id; Name = ($_.ProcessName + '.exe'); ExecutablePath = $p }
      })
  }
  foreach ($p in $procs) {
    if ($p.ExecutablePath) {
      $exe = $null
      try { $exe = [System.IO.Path]::GetFullPath($p.ExecutablePath) } catch { $exe = $null }
      if ($exe -and (Test-PgUnder $exe $Root)) { $inside.Add($p) }
    }
    elseif ($p.Name -in @('VRising.exe', 'VRisingServer.exe')) { $unknown.Add($p) }
  }
  return [pscustomobject]@{ Inside = $inside; Unknown = $unknown }
}

# True when the file is missing or can be opened for writing with no one else holding it (a running game holds
# winhttp.dll and Progenitor.Bootstrap.dll).
function Test-PgFileFree([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $true }
  try {
    $s = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $s.Dispose()
    return $true
  }
  catch { return $false }
}

function Assert-PgNotRunning($Target, [string]$Action, [bool]$Force) {
  $how = 'Quit the game'
  if ($Target.Side -eq 'server') { $how = 'Stop the server' }
  $running = Get-PgRunning $Target.Path
  if ($running.Inside.Count -gt 0) {
    $list = ($running.Inside | ForEach-Object { $_.Name + ' (process ' + $_.ProcessId + ')' }) -join ', '
    throw "$($Target.Name) is running from $($Target.Path): $list. $how, then run this again to $Action."
  }
  foreach ($f in @('winhttp.dll', 'BepInEx\plugins\Progenitor.Bootstrap.dll', 'BepInEx\core\BepInEx.Core.dll')) {
    $p = Join-Path $Target.Path $f
    if (-not (Test-PgFileFree $p)) { throw "$p is in use, so $($Target.Name) is probably running from this folder. $how, then run this again to $Action." }
  }
  if ($running.Unknown.Count -gt 0) {
    $list = ($running.Unknown | ForEach-Object { $_.Name + ' (process ' + $_.ProcessId + ')' }) -join ', '
    if (-not $Force) {
      throw "$list is running and Windows does not say from which folder. $how if it runs from $($Target.Path); if it runs from another folder, run this again with -Force (or set PROGENITOR_FORCE=1)."
    }
    Write-PgLine ("Note: $list is running from a folder Windows does not name; going on because of -Force.") 'Yellow'
  }
}

function Get-PgInstalledVersion([string]$GameRoot) {
  $record = Join-Path $GameRoot 'BepInEx\progenitor-self-install.json'
  if (Test-Path -LiteralPath $record -PathType Leaf) {
    try {
      $r = [System.IO.File]::ReadAllText($record) | ConvertFrom-Json
      if ($r.version) { return [string]$r.version }
    }
    catch { }
  }
  $core = Join-Path $GameRoot 'BepInEx\progenitor-core\Progenitor.dll'
  if (Test-Path -LiteralPath $core -PathType Leaf) {
    $v = Get-PgDllVersion $core
    if ($v) { return $v }
    return 'unknown'
  }
  if (Test-Path -LiteralPath (Join-Path $GameRoot 'BepInEx\plugins\Progenitor.Bootstrap.dll') -PathType Leaf) { return 'unknown' }
  return ''
}

# A DLL's product version without the "+<revision>" suffix, or '' when it has none.
function Get-PgDllVersion([string]$Path) {
  $v = $null
  try { $v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion } catch { $v = $null }
  if (-not $v) { return '' }
  $v = (($v -split '\+')[0]).Trim()
  if ($v -notmatch '^\d+(\.\d+){1,3}$') { return '' }
  return $v
}

# "Progenitor 2026.10.162", or "Progenitor (version unknown)".
function Format-PgInstalled([string]$Version) {
  if (-not $Version -or $Version -eq 'unknown') { return 'Progenitor (version unknown)' }
  return 'Progenitor ' + $Version
}

# A new folder under progenitor-backups: <kind>-<local time>, with -2, -3 and so on when that name is taken.
function New-PgBackupPath([string]$Parent, [string]$Kind) {
  $base = Join-Path $Parent ($Kind + '-' + (Get-Date).ToString('yyyyMMdd-HHmmss'))
  $path = $base
  $n = 2
  while (Test-Path -LiteralPath $path) { $path = $base + '-' + $n; $n++ }
  return $path
}

# Moves one file or folder into the backup folder (same relative path) and records it, so Undo-PgMoves can put it back.
function Move-PgToBackup($State, [string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return }
  $rel = $Path.Substring($State.Root.Length).TrimStart('\')
  $dest = Join-Path $State.Backup $rel
  $parent = Split-Path -Parent $dest
  if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
  # Never into something already in the backup: Move-Item would nest a folder inside it.
  if (Test-Path -LiteralPath $dest) { throw "The backup already holds $dest." }
  Move-Item -LiteralPath $Path -Destination $dest -ErrorAction Stop
  $State.Moved.Add([pscustomobject]@{ From = $Path; To = $dest })
}

# Puts every moved item back. Nothing at an original path is ever deleted: an empty folder left there is removed, and
# anything else is a collision that is reported, with the backup copy kept.
function Undo-PgMoves($State) {
  $failed = 0
  for ($i = $State.Moved.Count - 1; $i -ge 0; $i--) {
    $m = $State.Moved[$i]
    try {
      if (Test-Path -LiteralPath $m.From) {
        $isEmptyDir = (Test-Path -LiteralPath $m.From -PathType Container) -and -not (Get-ChildItem -LiteralPath $m.From -Force | Select-Object -First 1)
        if (-not $isEmptyDir) { throw 'something else is there now' }
        Remove-Item -LiteralPath $m.From -Force -ErrorAction Stop
      }
      $parent = Split-Path -Parent $m.From
      if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
      Move-Item -LiteralPath $m.To -Destination $m.From -ErrorAction Stop
    }
    catch { $failed++; Write-PgLine ("Could not put back " + $m.From + ": " + $_.Exception.Message + ". Its copy is " + $m.To) 'Red' }
  }
  # Everything is back, so the backup folder holds only empty folders.
  if ($failed -eq 0 -and (Test-Path -LiteralPath $State.Backup) -and -not (Get-ChildItem -LiteralPath $State.Backup -Recurse -Force -File | Select-Object -First 1)) {
    Remove-Item -LiteralPath $State.Backup -Recurse -Force -ErrorAction SilentlyContinue
  }
  return $failed
}

#endregion shared

function Write-PgFail([string]$Text) { Write-PgLine $Text 'Red' }

# Progenitor's own files in plugins\ and patchers\ (the same names the Mods menu's removal uses).
$PgPluginFiles = @('Progenitor.Bootstrap.dll', 'Progenitor.Bootstrap.dll.next', 'Progenitor.Bootstrap.dll.prev', 'Progenitor.Dev.dll', 'Progenitor.Sample.dll', 'Progenitor.ReloadExample.dll')
$PgPatcherFiles = @('Progenitor.Preloader.dll', 'progenitor-files.zip')
# BepInEx's own files beside the game's executable (-RemoveBepInEx only).
$PgLoaderRootFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
# Folders of a removed BepInEx that are not kept in the backup: BepInEx and the game rebuild or reinstall them.
$PgRegenerated = @('BepInEx\core', 'BepInEx\interop', 'BepInEx\unity-libs', 'BepInEx\cache', 'BepInEx\progenitor-cache', 'dotnet')

function Get-PgUninstallPlan([string]$Game, [bool]$Keep, [bool]$RemoveLoader) {
  $bep = Join-Path $Game 'BepInEx'
  $items = New-Object System.Collections.Generic.List[string]
  $add = {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowed = Test-PgUnder $full $bep
    if ($RemoveLoader) {
      $allowed = $allowed -or ($full -ieq $bep) -or ($full -ieq (Join-Path $Game 'dotnet'))
      foreach ($n in $PgLoaderRootFiles) { if ($full -ieq (Join-Path $Game $n)) { $allowed = $true } }
    }
    # Only BepInEx's folder and the loader files beside the game are ever touched: never saves or the game's own files.
    if (-not $allowed) { return }
    if ($full -ieq (Join-Path $bep 'core') -or (Test-PgUnder $full (Join-Path $bep 'core'))) { if (-not $RemoveLoader) { return } }
    foreach ($i in $items) { if ($i -ieq $full -or (Test-PgUnder $full $i)) { return } }
    $items.Add($full)
  }
  if (-not (Test-Path -LiteralPath $bep -PathType Container)) { return , $items }

  if ($RemoveLoader) {
    & $add $bep
    foreach ($n in $PgLoaderRootFiles) { & $add (Join-Path $Game $n) }
    if (Test-Path -LiteralPath (Join-Path $Game 'dotnet\coreclr.dll') -PathType Leaf) { & $add (Join-Path $Game 'dotnet') }
    return , $items
  }

  # The mods Progenitor installed, from its install ledger: only files whose bytes are still the ones it wrote.
  $ledger = Join-Path $bep 'progenitor-install.json'
  if (Test-Path -LiteralPath $ledger -PathType Leaf) {
    $rows = @()
    try { $rows = @(([System.IO.File]::ReadAllText($ledger) | ConvertFrom-Json).files) } catch { $rows = @() }
    foreach ($row in $rows) {
      if (-not $row.path) { continue }
      $rel = ([string]$row.path) -replace '/', '\'
      if ($rel -match '^[A-Za-z]:|^\\' -or $rel -match '(^|\\)\.\.(\\|$)') { continue }
      $p = Join-Path $Game $rel
      if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { continue }
      if ($Keep -and (Test-PgUnder $p (Join-Path $bep 'config'))) { continue }
      if ($row.sha256 -and ((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne ([string]$row.sha256).ToUpperInvariant())) { continue }
      & $add $p
    }
  }

  foreach ($pair in @(@('plugins', $PgPluginFiles), @('patchers', $PgPatcherFiles))) {
    $dir = Join-Path $bep $pair[0]
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
    foreach ($n in $pair[1]) { & $add (Join-Path $dir $n) }
    # A mod manager puts a package in a folder of its own (plugins\<package>\): Progenitor's file names only.
    foreach ($sub in Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue) {
      $found = $false
      foreach ($n in $pair[1]) {
        $p = Join-Path $sub.FullName $n
        if (Test-Path -LiteralPath $p -PathType Leaf) { & $add $p; $found = $true }
      }
      if ($found -and $pair[0] -eq 'plugins') { & $add (Join-Path $sub.FullName 'Progenitor.dll') }
    }
  }

  # Core, the reload and pending folders, caches, kept versions, logs, the self-install record and the ledger.
  foreach ($e in Get-ChildItem -LiteralPath $bep -Force -ErrorAction SilentlyContinue) {
    if ($e.Name -like 'progenitor*' -and $e.Name -ne 'progenitor-backups') { & $add $e.FullName }
  }

  if (-not $Keep) {
    $config = Join-Path $bep 'config'
    if (Test-Path -LiteralPath $config -PathType Container) {
      foreach ($e in Get-ChildItem -LiteralPath $config -Force -ErrorAction SilentlyContinue) {
        if ($e.Name -like 'progenitor*') { & $add $e.FullName }
      }
    }
  }

  # A ledgered file inside a folder that moves as a whole (progenitor-reload, progenitor-core) moves with it.
  $top = New-Object System.Collections.Generic.List[string]
  foreach ($i in $items) {
    $nested = $false
    foreach ($j in $items) { if ($j -ne $i -and (Test-PgUnder $i $j)) { $nested = $true; break } }
    if (-not $nested) { $top.Add($i) }
  }
  return , $top
}

function Invoke-PgUninstall {
  $ErrorActionPreference = 'Stop'
  $ProgressPreference = 'SilentlyContinue'
  $dry = [bool]$WhatIf
  $state = $null
  try {
    if ($dry) { Write-PgLine 'Progenitor uninstaller: dry run (-WhatIf). Nothing is changed.' 'Cyan' }
    else { Write-PgLine 'Progenitor uninstaller' 'Cyan' }

    $target = Resolve-PgTarget $GamePath ([bool]$Server) $SteamPath
    $game = $target.Path
    $bep = Join-Path $game 'BepInEx'
    Write-PgLine ("Target: " + $game + " (" + $target.Exe + ")")
    Assert-PgNotRunning $target 'uninstall' ([bool]$Force)

    if ($RemoveBepInEx) {
      Write-PgLine 'Warning: -RemoveBepInEx removes BepInEx itself. Every other BepInEx mod in this folder stops loading too.' 'Yellow'
    }

    $installed = Get-PgInstalledVersion $game
    $items = Get-PgUninstallPlan $game ([bool]$KeepSettings) ([bool]$RemoveBepInEx)
    if ($items.Count -eq 0) {
      Write-PgLine ("Progenitor is not installed in " + $game + ". Nothing to remove.") 'Green'
      return 0
    }

    $backup = New-PgBackupPath (Join-Path $bep 'progenitor-backups') 'uninstall'
    if ($RemoveBepInEx) { $backup = New-PgBackupPath (Join-Path $game 'progenitor-backups') 'uninstall' }

    if ($dry) {
      Write-PgLine ''
      Write-PgLine ('Would move ' + $items.Count + ' items to ' + $backup + ':')
      foreach ($i in $items) { Write-PgLine ('  ' + $i.Substring($game.Length).TrimStart('\')) }
      if ($RemoveBepInEx) { Write-PgLine "BepInEx's own files (core, interop, unity-libs, cache, dotnet) would not be kept in the backup." }
      elseif ($KeepSettings) { Write-PgLine "Progenitor's settings in BepInEx\config would stay." }
      else { Write-PgLine "Progenitor's settings in BepInEx\config would move to the backup too." }
      Write-PgLine 'Saved worlds are never touched.'
      Write-PgLine ''
      Write-PgLine 'Nothing was changed. Run again without -WhatIf to uninstall.' 'Cyan'
      return 0
    }

    $failAfter = 0
    if ($env:PROGENITOR_INSTALL_FAIL_AFTER -match '^\d+$') { $failAfter = [int]$env:PROGENITOR_INSTALL_FAIL_AFTER }
    $state = [pscustomobject]@{ Root = $game; Backup = $backup; Moved = (New-Object System.Collections.Generic.List[object]) }
    foreach ($i in $items) {
      Move-PgToBackup $state $i
      if ($failAfter -gt 0 -and $state.Moved.Count -ge $failAfter) { throw "Stopped after $($state.Moved.Count) items (PROGENITOR_INSTALL_FAIL_AFTER)." }
    }

    # Done: drop what does not belong in a backup, and the package folders a mod manager left empty.
    foreach ($r in $PgRegenerated) {
      $p = Join-Path $backup $r
      if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if (-not $RemoveBepInEx) {
      foreach ($dir in @('plugins', 'patchers')) {
        $d = Join-Path $bep $dir
        if (-not (Test-Path -LiteralPath $d -PathType Container)) { continue }
        foreach ($sub in Get-ChildItem -LiteralPath $d -Directory -ErrorAction SilentlyContinue) {
          $was = $false
          foreach ($m in $state.Moved) { if (Test-PgUnder $m.From $sub.FullName) { $was = $true } }
          if ($was -and -not (Get-ChildItem -LiteralPath $sub.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $sub.FullName -Force -ErrorAction SilentlyContinue }
        }
      }
    }

    Write-PgLine ''
    Write-PgLine 'Progenitor is removed.' 'Green'
    Write-PgLine ('  Target:      ' + $game)
    $what = 'Progenitor'
    if ($installed) { $what = Format-PgInstalled $installed }
    Write-PgLine ('  Removed:     ' + $what + ' (' + $state.Moved.Count + ' items)')
    Write-PgLine ('  Backup:      ' + $backup)
    foreach ($m in $state.Moved) {
      if ((Split-Path -Leaf $m.From) -like 'progenitor-uninstall.*') { Write-PgLine ('  Cleared:     the Mods menu removal request (' + (Split-Path -Leaf $m.From) + '), moved to the backup') }
    }
    if ($RemoveBepInEx) { Write-PgLine '  BepInEx:     removed (its settings, plugins and patchers are in the backup)' }
    else {
      Write-PgLine '  BepInEx:     kept'
      if ($KeepSettings) { Write-PgLine "  Settings:    kept in BepInEx\config" }
      else { Write-PgLine '  Settings:    moved to the backup' }
    }
    Write-PgLine '  Saves:       not touched'
    if ($target.Side -eq 'server') { Write-PgLine 'Next: start the server. It runs without Progenitor.' 'Cyan' }
    else { Write-PgLine 'Next: start V Rising. It runs without Progenitor.' 'Cyan' }
    return 0
  }
  catch {
    $message = $_.Exception.Message
    if ($state -and $state.Moved.Count -gt 0) {
      $failed = Undo-PgMoves $state
      Write-PgFail ("Uninstall failed: " + $message)
      if ($failed -eq 0) { Write-PgFail 'Every change was undone; the game folder is as it was.' }
      else { Write-PgFail ("Some files could not be put back. The copies are in " + $state.Backup) }
    }
    else { Write-PgFail ("Uninstall failed: " + $message); Write-PgFail 'Nothing was changed.' }
    return 1
  }
}

$PgExitCode = [int](@(Invoke-PgUninstall)[-1])
# Run as a file, a failure ends with exit code 1. Run through "irm | iex" or a scriptblock, it never closes the window.
if ($PgExitCode -ne 0) {
  $global:LASTEXITCODE = $PgExitCode
  $PgSelf = $MyInvocation.MyCommand.Path
  if ($MyInvocation.MyCommand.CommandType -eq 'ExternalScript' -and $PgSelf -and ([System.IO.File]::ReadAllText($PgSelf) -match 'function Invoke-PgUninstall')) { exit $PgExitCode }
}

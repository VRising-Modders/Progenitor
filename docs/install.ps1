<#
.SYNOPSIS
  Installs or updates Progenitor, and BepInEx when it is missing, in V Rising or a V Rising dedicated server.

.DESCRIPTION
  Finds the game through Steam (or uses -GamePath), refuses while that install is running, installs the
  BepInEx pack for V Rising from Thunderstore when BepInEx is missing or incomplete, then installs the
  latest Progenitor release from GitHub. Files it replaces (Progenitor's, and BepInEx's own files when it
  repairs an incomplete BepInEx; BepInEx settings are never replaced) are moved to
  BepInEx\progenitor-backups\install-<time> first; when anything fails part way, they are put back. The
  last three install backups are kept. Running it again updates or repairs.

  Read this script before you run it:
    https://vrising-modders.github.io/Progenitor/install.ps1

  One line, in PowerShell:
    irm https://vrising-modders.github.io/Progenitor/install.ps1 | iex

  With options:
    & ([scriptblock]::Create((irm https://vrising-modders.github.io/Progenitor/install.ps1))) -GamePath "D:\Games\VRising"

  Downloaded:
    powershell -ExecutionPolicy Bypass -File install.ps1 -GamePath "D:\Games\VRising"

  With "irm | iex" no options can be passed; set them as environment variables first instead:
    PROGENITOR_GAME_PATH, PROGENITOR_SERVER=1, PROGENITOR_WHATIF=1, PROGENITOR_FORCE=1,
    PROGENITOR_OFFLINE_PACKAGE, PROGENITOR_OFFLINE_BEPINEX, PROGENITOR_LATEST_BEPINEX=1,
    PROGENITOR_STEAM_PATH, PROGENITOR_FEED_URL.

.PARAMETER GamePath
  The folder that holds VRising.exe or VRisingServer.exe. Default: V Rising in your Steam libraries.

.PARAMETER Server
  Install into the dedicated server: "V Rising Dedicated Server" in your Steam libraries, or -GamePath.

.PARAMETER WhatIf
  Print what would happen and change nothing.

.PARAMETER Force
  Go on when a V Rising process is running whose folder Windows does not name. Use it only when that
  process runs from another folder.

.PARAMETER OfflinePackage
  Install this Progenitor zip instead of downloading the latest release. A SHA256SUMS file beside it is
  used to check it.

.PARAMETER OfflineBepInEx
  Install BepInEx from this BepInExPack_V_Rising zip instead of downloading it.

.PARAMETER LatestBepInEx
  Download the newest BepInExPack_V_Rising on Thunderstore instead of the version this script was
  reviewed with. That download is checked only for completeness, not against a known sha256.

.PARAMETER SteamPath
  Look for the game in this Steam folder instead of the one Windows has registered.

.PARAMETER FeedUrl
  The release list to read (an https address or a local file). Default: Progenitor's release list on
  its website.
#>
[CmdletBinding()]
param(
  [string]$GamePath = $env:PROGENITOR_GAME_PATH,
  [switch]$Server = ($env:PROGENITOR_SERVER -match '^(1|true|yes)$'),
  [switch]$WhatIf = ($env:PROGENITOR_WHATIF -match '^(1|true|yes)$'),
  [switch]$Force = ($env:PROGENITOR_FORCE -match '^(1|true|yes)$'),
  [string]$OfflinePackage = $env:PROGENITOR_OFFLINE_PACKAGE,
  [string]$OfflineBepInEx = $env:PROGENITOR_OFFLINE_BEPINEX,
  [switch]$LatestBepInEx = ($env:PROGENITOR_LATEST_BEPINEX -match '^(1|true|yes)$'),
  [string]$SteamPath = $env:PROGENITOR_STEAM_PATH,
  [string]$FeedUrl = $env:PROGENITOR_FEED_URL
)

# Where Progenitor's releases come from. The release list (written by the release process) names each release's zip,
# size and sha256. When it cannot be read, the SHA256SUMS of the latest GitHub release names the version and the hash.
$PgFeedUrl = 'https://vrising-modders.github.io/Progenitor/updates/releases.json'
$PgAssetPrefix = 'https://github.com/VRising-Modders/Progenitor/releases/download/'
$PgLatestPrefix = 'https://github.com/VRising-Modders/Progenitor/releases/latest/download/'
$PgPackageId = 'VRising_Modders-Progenitor'
$PgKeepInstallBackups = 3

# The BepInEx pack this script was reviewed with: Thunderstore's download of that exact version and its sha256.
# To move to a newer pack, see docs/releasing.md ("The install scripts").
$PgBepInExVersion = '1.733.2'
$PgBepInExSha256 = 'eabfb53d80bed427ddbd1ecf8af4dcd6e82e152809d44ab3218d0a09984f491c'
$PgBepInExDownloadPrefix = 'https://thunderstore.io/package/download/BepInEx/BepInExPack_V_Rising/'
# Only with -LatestBepInEx: Thunderstore's package endpoint, which names the newest version.
$PgBepInExMeta = 'https://thunderstore.io/api/experimental/package/BepInEx/BepInExPack_V_Rising/'

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

function Initialize-PgNet {
  # Windows PowerShell 5.1 may not offer TLS 1.2 by default; GitHub and Thunderstore require it.
  try { [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12 } catch { }
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
}

function Test-PgLocalSource([string]$Source) {
  return ($Source -notmatch '^[a-zA-Z][a-zA-Z0-9+.-]*://') -or ($Source -match '^file:')
}

function Get-PgLocalPath([string]$Source) {
  if ($Source -match '^file:') { return ([uri]$Source).LocalPath }
  return (Get-PgFullPath $Source)
}

# Downloads a URL into a file. Fails when the transfer is shorter than the server said.
function Save-PgDownload([string]$Url, [string]$OutFile) {
  if (Test-PgLocalSource $Url) {
    Copy-Item -LiteralPath (Get-PgLocalPath $Url) -Destination $OutFile -Force -ErrorAction Stop
    return
  }
  if ($Url -notmatch '^https://') { throw "Refusing to download over an insecure address: $Url" }
  $response = Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -PassThru -ErrorAction Stop
  $length = $null
  $encoding = $null
  try { $length = $response.Headers['Content-Length'] } catch { }
  try { $encoding = $response.Headers['Content-Encoding'] } catch { }
  if ($length -is [array]) { $length = $length[0] }
  if ($encoding -is [array]) { $encoding = $encoding[0] }
  $size = (Get-Item -LiteralPath $OutFile).Length
  if ($length -and -not $encoding -and ([int64]$length -ne $size)) {
    throw "The download from $Url stopped early ($size of $length bytes). Try again."
  }
}

function Get-PgText([string]$Url, [string]$Temp) {
  $file = Join-Path $Temp ([guid]::NewGuid().ToString('N') + '.txt')
  Save-PgDownload $Url $file
  return [System.IO.File]::ReadAllText($file)
}

function Get-PgSha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function ConvertTo-PgVersion([string]$Text) {
  $v = $null
  if ([System.Version]::TryParse(($Text -split '[-+]')[0], [ref]$v)) { return $v }
  return $null
}

# The newest release in the release list, or $null when the list cannot be read.
function Get-PgFeedRelease([string]$Url, [string]$Temp, [string]$Version, [bool]$Quiet) {
  try { $json = (Get-PgText $Url $Temp) | ConvertFrom-Json }
  catch {
    if (-not $Quiet) { Write-PgLine ("The release list is not available (" + $_.Exception.Message + "); using the latest GitHub release.") 'DarkGray' }
    return $null
  }
  if ($json.schema -ne 1 -or -not $json.releases) { return $null }
  $best = $null
  $bestVersion = $null
  foreach ($r in @($json.releases)) {
    if ($r.active -eq $false) { continue }
    if ($r.channel -and $r.channel -ne 'release') { continue }
    if (-not $r.version -or -not $r.url -or -not $r.sha256) { continue }
    if ($r.sha256 -notmatch '^[0-9a-fA-F]{64}$') { continue }
    $v = ConvertTo-PgVersion ([string]$r.version)
    if (-not $v) { continue }
    if ($Version) {
      if ([string]$r.version -eq $Version) { $best = $r; $bestVersion = $v; break }
      continue
    }
    if (-not ([string]$r.url).StartsWith($PgAssetPrefix, [System.StringComparison]::Ordinal)) { continue }
    if (-not $bestVersion -or $v -gt $bestVersion) { $best = $r; $bestVersion = $v }
  }
  if (-not $best) { return $null }
  $size = 0
  if ($best.size) { $size = [int64]$best.size }
  $coreSha = ''
  if ([string]$best.coreSha256 -match '^[0-9a-fA-F]{64}$') { $coreSha = ([string]$best.coreSha256).ToLowerInvariant() }
  return [pscustomobject]@{ Version = [string]$best.version; Url = [string]$best.url; Sha256 = ([string]$best.sha256).ToLowerInvariant(); Size = $size; CoreSha = $coreSha; From = 'the release list'; Verified = $true }
}

# Reads a SHA256SUMS file: the manual-install zip's version and hash.
function Read-PgSums([string]$Text, [string]$FileName) {
  foreach ($line in ($Text -split "`r?`n")) {
    $m = [regex]::Match($line.Trim(), '^([0-9a-fA-F]{64})\s+\*?(Progenitor-(\d+(?:\.\d+){2,3})\.zip)$')
    if (-not $m.Success) { continue }
    if ($FileName -and $m.Groups[2].Value -ne $FileName) { continue }
    return [pscustomobject]@{ Sha256 = $m.Groups[1].Value.ToLowerInvariant(); Name = $m.Groups[2].Value; Version = $m.Groups[3].Value }
  }
  return $null
}

function Resolve-PgRelease([string]$Temp, [string]$Feed) {
  $release = Get-PgFeedRelease $Feed $Temp '' $false
  if ($release) { return $release }
  $sums = Read-PgSums (Get-PgText ($PgLatestPrefix + 'SHA256SUMS') $Temp) ''
  if (-not $sums) { throw 'The latest GitHub release does not list a Progenitor zip.' }
  return [pscustomobject]@{ Version = $sums.Version; Url = ($PgLatestPrefix + $sums.Name); Sha256 = $sums.Sha256; Size = 0; CoreSha = ''; From = 'the latest GitHub release'; Verified = $true }
}

function Open-PgZip([string]$Path, [string]$What) {
  try { return [System.IO.Compression.ZipFile]::OpenRead($Path) }
  catch { throw "$What is not a complete zip file ($Path): $($_.Exception.Message)" }
}

function Get-PgEntryName($Entry) {
  $name = $Entry.FullName -replace '\\', '/'
  if ($name.StartsWith('/') -or $name -match '(^|/)\.\.(/|$)' -or $name -match '^[A-Za-z]:') { throw "The zip has an unsafe entry: $($Entry.FullName)" }
  return $name
}

# The files of a Progenitor zip that go into BepInEx\ (manual zip or mod-manager zip), checked before anything is written.
function Read-PgPackage([string]$Path) {
  $zip = Open-PgZip $Path 'The Progenitor download'
  try {
    $files = New-Object System.Collections.Generic.List[object]
    $hasBootstrap = $false
    $hasCore = $false
    $hasRecord = $false
    $hasCoreDll = $false
    foreach ($e in $zip.Entries) {
      $name = Get-PgEntryName $e
      if ($name.EndsWith('/')) { continue }
      if (-not $name.StartsWith('BepInEx/', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
      $rel = $name.Substring(8)
      $first = ($rel -split '/')[0]
      if (-not ($first -in @('plugins', 'patchers') -or $first -like 'progenitor*')) { throw "The Progenitor zip writes outside Progenitor's folders ($name). Use a release zip." }
      if ($rel -match '(^|/)Progenitor\.Dev\.dll$') { throw 'This zip carries the development bridge (Progenitor.Dev.dll). Use a release zip.' }
      if ($rel -eq 'plugins/Progenitor.Bootstrap.dll') { $hasBootstrap = $true }
      if ($rel -eq 'progenitor-core/Progenitor.dll') { $hasCoreDll = $true }
      if ($rel -eq 'progenitor-core/Progenitor.dll' -or $rel -eq 'patchers/progenitor-files.zip') { $hasCore = $true }
      if ($rel -eq 'progenitor-self-install.json') { $hasRecord = $true }
      $files.Add([pscustomobject]@{ Entry = $name; Rel = $rel })
    }
    if (-not $hasBootstrap -or -not $hasCore) { throw 'This zip is not a Progenitor release (no Bootstrap or no Core inside).' }
    return [pscustomobject]@{ Files = $files; HasRecord = $hasRecord; HasCoreDll = $hasCoreDll }
  }
  finally { $zip.Dispose() }
}

# The BepInEx pack's files relative to the game folder: what sits beside BepInEx\core inside the zip.
function Read-PgBepInEx([string]$Path) {
  $zip = Open-PgZip $Path 'The BepInEx download'
  try {
    $prefix = $null
    foreach ($e in $zip.Entries) {
      $name = Get-PgEntryName $e
      $m = [regex]::Match($name, '^(.*?)BepInEx/core/BepInEx\.Core\.dll$', 'IgnoreCase')
      if ($m.Success) { $prefix = $m.Groups[1].Value; break }
    }
    if ($null -eq $prefix) { throw 'The BepInEx download has no BepInEx\core\BepInEx.Core.dll. It is not the V Rising BepInEx pack.' }
    $files = New-Object System.Collections.Generic.List[object]
    $hasShim = $false
    foreach ($e in $zip.Entries) {
      $name = Get-PgEntryName $e
      if ($name.EndsWith('/')) { continue }
      if (-not $name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
      $rel = $name.Substring($prefix.Length)
      # A pack zipped at its root also holds Thunderstore's own listing files; they are not game files.
      if ($prefix -eq '' -and $rel -in @('manifest.json', 'icon.png', 'README.md', 'CHANGELOG.md')) { continue }
      if ($rel -eq 'winhttp.dll') { $hasShim = $true }
      $files.Add([pscustomobject]@{ Entry = $name; Rel = $rel })
    }
    if (-not $hasShim) { throw 'The BepInEx download has no winhttp.dll. It is not the V Rising BepInEx pack.' }
    return $files
  }
  finally { $zip.Dispose() }
}

function Test-PgBepInEx([string]$GameRoot) {
  return (Test-Path -LiteralPath (Join-Path $GameRoot 'BepInEx\core\BepInEx.Core.dll') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $GameRoot 'winhttp.dll') -PathType Leaf)
}

# What the Progenitor files replace: a file, or a whole folder inside a progenitor-* folder (progenitor-core\addresslib).
function Get-PgUnit([string]$Rel) {
  $parts = $Rel -split '/'
  if ($parts.Count -ge 3 -and $parts[0] -like 'progenitor*') { return ($parts[0] + '/' + $parts[1]) }
  return $Rel
}

function Write-PgFileFromZip($Zip, [string]$EntryName, [string]$Dest, $State) {
  $entry = $Zip.GetEntry($EntryName)
  if (-not $entry) { foreach ($e in $Zip.Entries) { if (($e.FullName -replace '\\', '/') -eq $EntryName) { $entry = $e; break } } }
  if (-not $entry) { throw "The zip lost its entry $EntryName." }
  $parent = Split-Path -Parent $Dest
  if (-not (Test-Path -LiteralPath $parent)) {
    $missing = New-Object System.Collections.Generic.List[string]
    $p = $parent
    while ($p -and -not (Test-Path -LiteralPath $p)) { $missing.Insert(0, $p); $p = Split-Path -Parent $p }
    foreach ($d in $missing) { New-Item -ItemType Directory -Path $d -Force | Out-Null; $State.CreatedDirs.Add($d) }
  }
  # Only inside the game folder, and only where nothing is: whatever was there was moved to the backup first.
  if (-not (Test-PgUnder $Dest $State.Root)) { throw "Refusing to write outside the game folder: $Dest" }
  if (Test-Path -LiteralPath $Dest) { throw "$Dest is already there." }
  try { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $Dest, $false) }
  catch {
    # A file this call started is removed; it is not in Created yet.
    if (Test-Path -LiteralPath $Dest -PathType Leaf) { Remove-Item -LiteralPath $Dest -Force -ErrorAction SilentlyContinue }
    throw
  }
  $State.Created.Add($Dest)
  $State.Written++
  if ($State.FailAfter -gt 0 -and $State.Written -ge $State.FailAfter) { throw "Stopped after $($State.Written) files (PROGENITOR_INSTALL_FAIL_AFTER)." }
}

function Undo-PgWrites($State) {
  for ($i = $State.Created.Count - 1; $i -ge 0; $i--) {
    $f = $State.Created[$i]
    try { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force -ErrorAction Stop } } catch { Write-PgLine ("Could not remove " + $f + ": " + $_.Exception.Message) 'Red' }
  }
  # Folders this run created, deepest first and only when empty, so a moved folder can go back to its place.
  for ($i = $State.CreatedDirs.Count - 1; $i -ge 0; $i--) {
    $d = $State.CreatedDirs[$i]
    try { if ((Test-Path -LiteralPath $d) -and -not (Get-ChildItem -LiteralPath $d -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $d -Force } } catch { }
  }
  return (Undo-PgMoves $State)
}

function Invoke-PgInstall {
  $ErrorActionPreference = 'Stop'
  $ProgressPreference = 'SilentlyContinue'
  $dry = [bool]$WhatIf
  $temp = $null
  $state = $null
  try {
    Initialize-PgNet
    if ($dry) { Write-PgLine 'Progenitor installer: dry run (-WhatIf). Nothing is written.' 'Cyan' }
    else { Write-PgLine 'Progenitor installer' 'Cyan' }

    # 1. The target.
    $target = Resolve-PgTarget $GamePath ([bool]$Server) $SteamPath
    $game = $target.Path
    $bep = Join-Path $game 'BepInEx'
    Write-PgLine ("Target: " + $game + " (" + $target.Exe + ")")

    # 2. Not while it runs.
    Assert-PgNotRunning $target 'install' ([bool]$Force)

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('progenitor-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null

    if (-not $dry) {
      $probe = Join-Path $game ('.progenitor-write-test-' + [guid]::NewGuid().ToString('N'))
      try { [System.IO.File]::WriteAllText($probe, ''); Remove-Item -LiteralPath $probe -Force }
      catch { throw "This script can't write to $game. Run PowerShell as administrator, or give your account write access to that folder." }
    }

    # 3. BepInEx: kept when present, otherwise downloaded and checked.
    $bepPresent = Test-PgBepInEx $game
    $bepFiles = $null
    $bepZip = $null
    $bepLabel = 'kept (already installed)'
    if (-not $bepPresent) {
      $bepVersion = ''
      $bepCheck = ''
      if ($OfflineBepInEx) {
        $bepZip = Get-PgFullPath $OfflineBepInEx
        if (-not (Test-Path -LiteralPath $bepZip -PathType Leaf)) { throw "There is no BepInEx zip at $bepZip." }
        $bepVersion = 'from ' + (Split-Path -Leaf $bepZip)
        if ((Get-PgSha256 $bepZip) -eq $PgBepInExSha256) { $bepCheck = 'the reviewed pack ' + $PgBepInExVersion }
        else { $bepCheck = 'not the reviewed pack ' + $PgBepInExVersion + ' (sha256 differs); installing it because you named it' }
      }
      else {
        $url = $PgBepInExDownloadPrefix + $PgBepInExVersion + '/'
        $bepVersion = $PgBepInExVersion
        $expected = $PgBepInExSha256
        if ($LatestBepInEx) {
          $meta = (Get-PgText $PgBepInExMeta $temp) | ConvertFrom-Json
          $latest = $meta.latest
          if (-not $latest -or -not $latest.download_url -or -not $latest.version_number) { throw 'Thunderstore did not name the latest BepInEx pack for V Rising.' }
          $url = [string]$latest.download_url
          if (-not $url.StartsWith($PgBepInExDownloadPrefix, [System.StringComparison]::Ordinal)) { throw "Thunderstore named an unexpected BepInEx download: $url" }
          $bepVersion = [string]$latest.version_number
          if ($bepVersion -ne $PgBepInExVersion) { $expected = '' }
        }
        if (-not $dry) {
          Write-PgLine ("Downloading BepInExPack_V_Rising " + $bepVersion + " from Thunderstore...")
          $bepZip = Join-Path $temp 'BepInExPack_V_Rising.zip'
          Save-PgDownload $url $bepZip
          if ($expected) {
            if ((Get-PgSha256 $bepZip) -ne $expected) { throw "The BepInEx download is not the reviewed BepInExPack_V_Rising $PgBepInExVersion (sha256 differs). Try again later." }
            $bepCheck = 'sha256 matches the reviewed pack'
          }
          else { $bepCheck = 'newest pack (-LatestBepInEx): checked for completeness only' }
        }
      }
      if ($bepZip) { $bepFiles = Read-PgBepInEx $bepZip }
      $bepLabel = 'installed BepInExPack_V_Rising ' + $bepVersion
      if (Test-Path -LiteralPath $bep) { $bepLabel = 'repaired with BepInExPack_V_Rising ' + $bepVersion + ' (it was incomplete; its replaced files are in the backup)' }
      if ($bepCheck) { $bepLabel = $bepLabel + ', ' + $bepCheck }
    }

    # 4. Progenitor: the latest release, or the zip given.
    $feed = $PgFeedUrl
    if ($FeedUrl) { $feed = $FeedUrl }
    $pkgZip = $null
    $verified = ''
    if ($OfflinePackage) {
      $pkgZip = Get-PgFullPath $OfflinePackage
      if (-not (Test-Path -LiteralPath $pkgZip -PathType Leaf)) { throw "There is no Progenitor zip at $pkgZip." }
      $leaf = Split-Path -Leaf $pkgZip
      $version = ''
      $m = [regex]::Match($leaf, '^Progenitor-(\d+(?:\.\d+){2,3})(?:-thunderstore)?\.zip$')
      if ($m.Success) { $version = $m.Groups[1].Value }
      $sha = Get-PgSha256 $pkgZip
      $sumsFile = Join-Path (Split-Path -Parent $pkgZip) 'SHA256SUMS'
      $coreSha = ''
      if ($FeedUrl) {
        $entry = Get-PgFeedRelease $FeedUrl $temp $version $true
        if (-not $entry) { throw "The release list at $FeedUrl does not list $leaf." }
        if ($entry.Sha256 -ne $sha) { throw "$leaf is not the file the release list names (sha256 differs)." }
        $verified = 'sha256 matches the release list'
        $coreSha = $entry.CoreSha
      }
      elseif (Test-Path -LiteralPath $sumsFile -PathType Leaf) {
        $row = $null
        foreach ($line in [System.IO.File]::ReadAllLines($sumsFile)) {
          $mm = [regex]::Match($line.Trim(), '^([0-9a-fA-F]{64})\s+\*?(.+)$')
          if ($mm.Success -and $mm.Groups[2].Value -eq $leaf) { $row = $mm.Groups[1].Value.ToLowerInvariant(); break }
        }
        if (-not $row) { throw "SHA256SUMS beside $leaf has no row for it." }
        if ($row -ne $sha) { throw "$leaf does not match its row in SHA256SUMS (sha256 differs)." }
        $verified = 'sha256 matches SHA256SUMS'
      }
      else { $verified = 'not checked: no SHA256SUMS beside it' }
      $release = [pscustomobject]@{ Version = $version; From = $pkgZip; CoreSha = $coreSha; Verified = [bool]($version -and $verified -like 'sha256 matches*') }
    }
    else {
      $release = Resolve-PgRelease $temp $feed
      if (-not $dry) {
        Write-PgLine ("Downloading Progenitor " + $release.Version + "...")
        $pkgZip = Join-Path $temp 'Progenitor.zip'
        Save-PgDownload $release.Url $pkgZip
        $size = (Get-Item -LiteralPath $pkgZip).Length
        if ($release.Size -gt 0 -and $size -ne $release.Size) { throw "The Progenitor download is $size bytes; the release list says $($release.Size). Try again." }
        if ((Get-PgSha256 $pkgZip) -ne $release.Sha256) { throw "The Progenitor download does not match its published sha256. Try again; if it keeps failing, download the zip by hand from the release page." }
        $verified = 'sha256 matches ' + $release.From
      }
    }
    $package = $null
    if ($pkgZip) { $package = Read-PgPackage $pkgZip }

    $installed = Get-PgInstalledVersion $game
    $backup = New-PgBackupPath (Join-Path $bep 'progenitor-backups') 'install'

    # What the new files replace, and the staged copies a start would otherwise apply over them.
    $units = New-Object System.Collections.Generic.List[string]
    if ($package) {
      foreach ($f in $package.Files) { $u = Get-PgUnit $f.Rel; if (-not ($units -contains $u)) { $units.Add($u) } }
      if (-not $package.HasRecord -and $package.HasCoreDll) { $units.Add('progenitor-self-install.json') }
    }
    else {
      foreach ($u in @('plugins/Progenitor.Bootstrap.dll', 'patchers/Progenitor.Preloader.dll', 'progenitor-core/Progenitor.dll', 'progenitor-core/addresslib', 'progenitor-core/docs', 'progenitor-self-install.json')) { $units.Add($u) }
    }
    foreach ($u in @('plugins/Progenitor.Bootstrap.dll.next', 'plugins/Progenitor.Bootstrap.dll.prev', 'progenitor-core/Progenitor.dll.next')) { $units.Add($u) }
    $replace = @($units | Where-Object { Test-Path -LiteralPath (Join-Path $bep ($_ -replace '/', '\')) })
    $bepOverwrite = @()
    if ($bepFiles) {
      $bepOverwrite = @($bepFiles | Where-Object { ($_.Rel -notlike 'BepInEx/config/*') -and (Test-Path -LiteralPath (Join-Path $game ($_.Rel -replace '/', '\')) -PathType Leaf) } | ForEach-Object { $_.Rel })
    }

    $others = @()
    foreach ($dir in @('plugins', 'patchers')) {
      $d = Join-Path $bep $dir
      if (-not (Test-Path -LiteralPath $d -PathType Container)) { continue }
      foreach ($sub in Get-ChildItem -LiteralPath $d -Directory -ErrorAction SilentlyContinue) {
        foreach ($n in @('Progenitor.Bootstrap.dll', 'Progenitor.Preloader.dll', 'progenitor-files.zip')) {
          $p = Join-Path $sub.FullName $n
          if (Test-Path -LiteralPath $p -PathType Leaf) { $others += $p }
        }
      }
    }

    if ($dry) {
      Write-PgLine ''
      Write-PgLine 'Plan:'
      Write-PgLine ('  BepInEx:     ' + ($bepLabel -replace '^installed', 'would install' -replace '^repaired', 'would repair'))
      $now = 'none'
      if ($installed) { $now = Format-PgInstalled $installed }
      $what = $release.Version
      if (-not $what) { $what = 'Progenitor' }
      Write-PgLine ('  Progenitor:  would install ' + $what + ' from ' + $release.From + ' (installed now: ' + $now + ')')
      if ($verified) { Write-PgLine ('  Check:       ' + $verified) }
      if ($replace.Count -gt 0 -or $bepOverwrite.Count -gt 0) {
        Write-PgLine ('  Backup:      would move these to ' + $backup + ':')
        foreach ($u in $replace) { Write-PgLine ('                 BepInEx/' + $u) }
        foreach ($u in $bepOverwrite) { Write-PgLine ('                 ' + $u) }
      }
      else { Write-PgLine '  Backup:      nothing to back up' }
      if ($package) {
        Write-PgLine ('  Writes:      ' + $package.Files.Count + ' Progenitor files into ' + $bep)
        foreach ($f in $package.Files) { Write-PgLine ('                 BepInEx/' + $f.Rel) }
      }
      else { Write-PgLine ('  Writes:      BepInEx/plugins/Progenitor.Bootstrap.dll, BepInEx/patchers/Progenitor.Preloader.dll, BepInEx/progenitor-core/') }
      if ($bepFiles) { Write-PgLine ('               and ' + $bepFiles.Count + ' BepInEx files into ' + $game + ' (existing BepInEx settings are kept)') }
      foreach ($o in $others) { Write-PgLine ("  Note:        another copy of Progenitor's files is at $o. Remove it with uninstall.ps1 or by hand if Progenitor loads twice.") 'Yellow' }
      Write-PgLine ''
      Write-PgLine 'Nothing was changed. Run again without -WhatIf to install.' 'Cyan'
      return 0
    }

    # 5-8. Back up, then write; on any failure put everything back.
    $failAfter = 0
    if ($env:PROGENITOR_INSTALL_FAIL_AFTER -match '^\d+$') { $failAfter = [int]$env:PROGENITOR_INSTALL_FAIL_AFTER }
    $state = [pscustomobject]@{
      Root = $game; Backup = $backup; Moved = (New-Object System.Collections.Generic.List[object])
      Created = (New-Object System.Collections.Generic.List[string]); CreatedDirs = (New-Object System.Collections.Generic.List[string])
      Written = 0; FailAfter = $failAfter
    }
    foreach ($u in $replace) { Move-PgToBackup $state (Join-Path $bep ($u -replace '/', '\')) }
    foreach ($u in $bepOverwrite) { Move-PgToBackup $state (Join-Path $game ($u -replace '/', '\')) }

    $keptConfig = 0
    if ($bepFiles) {
      $zip = Open-PgZip $bepZip 'The BepInEx download'
      try {
        foreach ($f in $bepFiles) {
          $dest = Join-Path $game ($f.Rel -replace '/', '\')
          if (Test-Path -LiteralPath $dest -PathType Leaf) { $keptConfig++; continue }
          Write-PgFileFromZip $zip $f.Entry $dest $state
        }
      }
      finally { $zip.Dispose() }
    }

    $zip = Open-PgZip $pkgZip 'The Progenitor download'
    try {
      foreach ($f in $package.Files) { Write-PgFileFromZip $zip $f.Entry (Join-Path $bep ($f.Rel -replace '/', '\')) $state }
    }
    finally { $zip.Dispose() }

    $core = Join-Path $bep 'progenitor-core\Progenitor.dll'
    $boot = Join-Path $bep 'plugins\Progenitor.Bootstrap.dll'
    # Core is the one the release list names, when it names one.
    $coreVersion = ''
    if ($package.HasCoreDll) {
      $coreHash = Get-PgSha256 $core
      if ($release.CoreSha -and $coreHash -ne $release.CoreSha) { throw "The installed Core is not the one the release list names (sha256 differs)." }
      $coreVersion = Get-PgDllVersion $core
    }
    # Which release this is: the version the release list or SHA256SUMS names for exactly this zip (checked by sha256).
    # Core's own version is the release of Core's last change, which can be older than the release, so it is used only
    # when nothing checked names the release (a zip named by hand with no SHA256SUMS beside it).
    $newVersion = ''
    if ($release.Verified -and $release.Version) { $newVersion = $release.Version }
    elseif ($coreVersion) { $newVersion = $coreVersion }

    # The self-install record says which Progenitor this is, so Progenitor's own updates recognize the files as theirs.
    # A zip that carries one wrote it above; a mod-manager zip unpacks Core at the first start and gets none here, and
    # an install whose version is unknown gets none either.
    $recordNote = ''
    if (-not $package.HasRecord -and $package.HasCoreDll) {
      if ($newVersion) {
        $record = [ordered]@{
          schema = 1; package = $PgPackageId; version = $newVersion
          coreSha256 = $coreHash; bootstrapSha256 = (Get-PgSha256 $boot)
          installedUtc = (Get-Date).ToUniversalTime().ToString('o'); source = 'installer'
        }
        $recordPath = Join-Path $bep 'progenitor-self-install.json'
        if (Test-Path -LiteralPath $recordPath) { throw "$recordPath is already there." }
        [System.IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
        $state.Created.Add($recordPath)
      }
      else { $recordNote = 'The version of this zip is unknown (no SHA256SUMS or release list names it, and Core reports none), so no self-install record was written; Progenitor will not update itself until it is reinstalled from a release.' }
    }

    # 5. Only the last few install backups are kept.
    $pruned = 0
    $backups = Join-Path $bep 'progenitor-backups'
    if (Test-Path -LiteralPath $backups -PathType Container) {
      $old = @(Get-ChildItem -LiteralPath $backups -Directory | Where-Object { $_.Name -like 'install-*' } | Sort-Object Name -Descending | Select-Object -Skip $PgKeepInstallBackups)
      foreach ($o in $old) {
        if ($o.FullName -ieq $backup) { continue }
        try { Remove-Item -LiteralPath $o.FullName -Recurse -Force -ErrorAction Stop; $pruned++ } catch { }
      }
    }

    # 6. Report.
    Write-PgLine ''
    Write-PgLine 'Progenitor is installed.' 'Green'
    Write-PgLine ('  Target:      ' + $game)
    Write-PgLine ('  BepInEx:     ' + $bepLabel)
    $was = ''
    if ($installed -and $installed -eq $newVersion) { $was = ' (reinstalled)' }
    elseif ($installed -eq 'unknown') { $was = ' (replaced a Progenitor of unknown version)' }
    elseif ($installed) { $was = ' (was ' + $installed + ')' }
    $shown = $newVersion
    if (-not $shown) { $shown = '(version unknown)' }
    Write-PgLine ('  Progenitor:  ' + $shown + $was + ', ' + $verified)
    if ($state.Moved.Count -gt 0) { Write-PgLine ('  Backup:      ' + $backup) }
    else { Write-PgLine '  Backup:      none needed (nothing was replaced)' }
    if ($pruned -gt 0) { Write-PgLine ('               removed ' + $pruned + ' older install backups; the last ' + $PgKeepInstallBackups + ' are kept') }
    if ($recordNote) { Write-PgLine ('  Note:        ' + $recordNote) 'Yellow' }
    if ($keptConfig -gt 0) { Write-PgLine ('  Settings:    kept your ' + $keptConfig + ' existing BepInEx settings files') }
    foreach ($o in $others) { Write-PgLine ("  Note:        another copy of Progenitor's files is at $o. Remove it with uninstall.ps1 or by hand if Progenitor loads twice.") 'Yellow' }
    if ($target.Side -eq 'server') { Write-PgLine 'Next: start the server. The first start with BepInEx is slow while it prepares its files.' 'Cyan' }
    else { Write-PgLine 'Next: start V Rising, then open the Mods menu on the title screen.' 'Cyan' }
    return 0
  }
  catch {
    $message = $_.Exception.Message
    if ($state -and ($state.Moved.Count -gt 0 -or $state.Created.Count -gt 0)) {
      $failed = Undo-PgWrites $state
      Write-PgFail ("Install failed: " + $message)
      if ($failed -eq 0) { Write-PgFail 'Every change was undone; the game folder is as it was.' }
      else { Write-PgFail ("Some files could not be put back. The copies are in " + $state.Backup) }
    }
    else { Write-PgFail ("Install failed: " + $message); Write-PgFail 'Nothing was changed.' }
    return 1
  }
  finally {
    if ($temp -and (Test-Path -LiteralPath $temp)) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
  }
}

$PgExitCode = [int](@(Invoke-PgInstall)[-1])
# Run as a file, a failure ends with exit code 1. Run through "irm | iex" or a scriptblock, it never closes the window.
if ($PgExitCode -ne 0) {
  $global:LASTEXITCODE = $PgExitCode
  $PgSelf = $MyInvocation.MyCommand.Path
  if ($MyInvocation.MyCommand.CommandType -eq 'ExternalScript' -and $PgSelf -and ([System.IO.File]::ReadAllText($PgSelf) -match 'function Invoke-PgInstall')) { exit $PgExitCode }
}

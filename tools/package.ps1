<#
.SYNOPSIS
  Build a Release Thunderstore zip under packaging/out/. Never writes into a game folder.

.DESCRIPTION
  Builds Progenitor.Bootstrap, Progenitor.Preloader and Progenitor.Core (and Progenitor.Dev
  when -IncludeDev) in Release, then assembles

    packaging/out/Progenitor-<version>.zip

  with Thunderstore metadata from packaging/thunderstore/ and

    BepInEx/plugins/Progenitor.Bootstrap.dll
    BepInEx/patchers/Progenitor.Preloader.dll
    BepInEx/progenitor-core/Progenitor.dll

  The preloader patcher carries out the Mods menu's next-start plugin actions and keeps the
  per-session logs (BepInEx/progenitor-logs) that Get help -> Collect logs bundles. All three
  DLLs are asserted present in the finished zip.

  When -IncludeDev is passed, also includes

    BepInEx/progenitor-reload/Progenitor.Dev.dll

  Dev is omitted from the public zip on purpose: the debug bridge is a loopback socket
  for development machines and must not land on a public server. That omission is asserted,
  not just implied: the staged tree and the finished zip are both scanned for
  Progenitor.Dev.*, *.token and the progenitor-dev.enable opt-in marker, and packaging fails
  if one is present without -IncludeDev. With -IncludeDev, the Release Dev DLL is also checked
  to make sure the extra-instance Steam skip (PROGENITOR_EXTRA_INSTANCE, Debug only) is not in it.

  The package version is the repository-wide version computed from git by
  tools/version/ProgenitorVersion.targets (docs/versioning.md). Each DLL keeps its own component
  version (the release of its own last change), so an unchanged Bootstrap is byte-identical to the
  previous package's. The zip is named with the full package version (year.month.release[.sub]);
  manifest.json gets the three-part Thunderstore number (year.month.release). Nobody bumps a
  version by hand.

  Refuses to write under C:\VRisingServer, the isolated test server (<workspace>\_testserver and
  PROGENITOR_TEST_ROOT; tools/common/Workspace.ps1), or the Steam V Rising install. When the test
  server folder cannot be resolved it refuses every path.

.PARAMETER IncludeDev
  Also build Progenitor.Dev and copy Progenitor.Dev.dll into BepInEx/progenitor-reload
  inside the zip. For local/dev packages only.

.PARAMETER Version
  Explicit version override (year.month.release or year.month.release.sub) for the package AND
  every DLL in it. Default: computed from git. A release package is built from a clean checkout
  of main, which computes a three-part version by itself.
#>
[CmdletBinding()]
param(
  [switch]$IncludeDev,
  [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent

# The one fence (tools/common/Fence.ps1, R40 finding 4). This script already had the correct
# GetFullPath-plus-boundary shape; it now shares it with deploy.ps1, stage-test-server.ps1 and
# test-server.ps1 rather than being the only copy that was right, and gains the resolution the
# other three needed anyway (\\?\ and admin-share spellings, 8.3 short names, reparse points).
. (Join-Path $PSScriptRoot "common\Fence.ps1")

$forbidden = $ProgenitorPackageForbiddenRoots

function Format-SumsRow {
  <#
    The one row formatter for SHA256SUMS. It is the PowerShell half of
    ReloadAllowPolicy.FormatSumsLine / FormatSumsEntryLine, and the grammar it writes is
    documented once on ReloadAllowPolicy.RowGrammar:

      <64 hex sha256><two spaces><name>,  name = <leaf> | <container>!<path>

    Both forms are rows ReloadAllowPolicy.Parse accepts, so any row in this file can be
    pasted into BepInEx/config/progenitor-allow.txt without failing the file closed --
    R30 finding 11, where the per-entry rows were rejected as "not a leaf name" and one
    pasted row turned the whole allowlist Malformed (which refuses Core too).
    ReloadHardeningTests runs THIS function and feeds its output to the C# parser.
  #>
  param(
    [Parameter(Mandatory = $true)][string]$Hash,
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Container = ""
  )
  $leaf = if ($Container) { $Container + "!" + $Name } else { $Name }
  return ($Hash.ToLowerInvariant() + "  " + $leaf)
}

function Assert-NotForbidden {
  # Thin wrapper over the shared fence, kept under its old name because every call site below
  # reads as a sentence with it. The comparison logic lives in tools/common/Fence.ps1.
  param([Parameter(Mandatory = $true)][string]$Path)
  Assert-NotForbiddenPath -Path $Path -ForbiddenRoots $forbidden `
    -Message "refusing to write under forbidden path"
}

function Assert-NoDevPayload {
  <#
    The public zip must not carry the debug bridge. tools/package.ps1 has always simply omitted
    the file; this asserts it, so a future edit that copies the whole build output (or an
    accidental -IncludeDev in a release script) fails the package instead of shipping a loopback
    control socket plus the extra-instance Steam skip to players.
  #>
  param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][bool]$DevExpected
  )
  $devFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter "Progenitor.Dev.*" -ErrorAction SilentlyContinue)
  if (-not $DevExpected -and $devFiles.Count -gt 0) {
    throw "packaging assertion failed: the public zip must not contain the Dev bridge, found $($devFiles.Count) file(s): $(($devFiles | ForEach-Object { $_.FullName }) -join ', ')"
  }
  if ($DevExpected -and $devFiles.Count -eq 0) {
    throw "packaging assertion failed: -IncludeDev was passed but no Progenitor.Dev file was staged"
  }
  $tokens = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter "*.token" -ErrorAction SilentlyContinue)
  if ($tokens.Count -gt 0) {
    throw "packaging assertion failed: a bridge token file was staged: $(($tokens | ForEach-Object { $_.FullName }) -join ', ')"
  }
  $markers = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter "progenitor-dev.enable" -ErrorAction SilentlyContinue)
  if ($markers.Count -gt 0) {
    throw "packaging assertion failed: the Dev bridge opt-in marker was staged: $(($markers | ForEach-Object { $_.FullName }) -join ', ')"
  }
}

function Assert-NoDeveloperArtefacts {
  <#
    Everything the dev loop creates that is NOT part of a release, asserted on the staged tree
    (docs/dev-loop.md, "Deploy audit 2026-09-06"). None of these is copied by this script today;
    the point is that a later edit which widens a copy cannot ship one silently.

      *.delta            method-body deltas written by tools/enc into a reload folder. A delta is
                         an unsigned metadata patch keyed to one loaded image's baseline sha256 --
                         meaningless in a zip and a load path players never asked for.
      *.delta-state      the reloader's published sequence state. Runtime bookkeeping, per tree.
      progenitor-allow.txt   the operator's allowlist. Shipping one would either lock a player's
                         tree to OUR hashes or, worse, hand them a file they think they wrote.
      progenitor-pending/    Core's quarantine for accepted server packages. Nothing scans it and
                         nothing may pre-seed it.
      build-*.json       the Resilience build fingerprint Core writes beside the address tables.
                         A shipped one is a baseline nobody captured on that machine, and it mutes
                         the first real drift diff after a game patch.
      driftmap-*.json    the compat rewriter's per-build map. Allowlist-vetted and placed by hand;
                         never a package payload.
      tools/enc, tools/driftmap, tools/accessgen build output -- those are repo-side tools; the
                         access layer is GENERATED CODE COMPILED INTO Core, so access.txt and the
                         generator are not needed at runtime and must not be in the zip.
  #>
  param([Parameter(Mandatory = $true)][string]$Root)
  $bad = New-Object System.Collections.Generic.List[string]
  foreach ($pattern in @("*.delta", "*.delta-state", "progenitor-allow.txt", "build-*.json",
                         "driftmap-*.json", "access.txt", "*.deps.json", "*.pdb")) {
    foreach ($f in @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue)) {
      $bad.Add($pattern + " -> " + $f.FullName)
    }
  }
  foreach ($d in @(Get-ChildItem -LiteralPath $Root -Recurse -Directory -ErrorAction SilentlyContinue)) {
    if ($d.Name -eq "progenitor-pending" -or $d.Name -eq "enc" -or $d.Name -eq "driftmap" -or $d.Name -eq "accessgen") {
      $bad.Add("folder -> " + $d.FullName)
    }
  }
  if ($bad.Count -gt 0) {
    throw "packaging assertion failed: developer-only artifacts staged: $($bad -join '; ')"
  }
}

function Assert-NoExtraInstanceAuth {
  <#
    Ops/ExtraInstanceServerAuth.cs compiles its Steam-skip half only under PROGENITOR_EXTRA_INSTANCE,
    which src/Progenitor.Dev/Progenitor.Dev.csproj defines for the Debug configuration only. This
    proves it for the DLL actually being packaged: the nested Harmony prefix class AuthenticateHook
    is in the Debug assembly's metadata and must not be in a Release one.
  #>
  param([Parameter(Mandatory = $true)][string]$DllPath)
  $bytes = [System.IO.File]::ReadAllBytes($DllPath)
  $text = [System.Text.Encoding]::ASCII.GetString($bytes)
  foreach ($needle in @("AuthenticateHook", "AuthenticatePrefixParameterNames")) {
    if ($text.Contains($needle)) {
      throw "packaging assertion failed: $DllPath still contains the extra-instance Steam skip ('$needle'). It must be built without PROGENITOR_EXTRA_INSTANCE (Release)."
    }
  }
}

function Get-ProgenitorVersion {
  <#
    The build version, computed by the same MSBuild target every project build runs
    (tools/version/ProgenitorVersion.targets), so the package cannot disagree with the DLLs.
    Returns @{ Version; Thunderstore }. Refuses 0.0.0.0 (git unavailable): a package must carry
    a real version.
  #>
  param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [string]$Override = ""
  )
  $targets = Join-Path $RepoRoot "tools\version\ProgenitorVersion.targets"
  $msbuildArgs = @("msbuild", $targets, "-nologo", "-t:ProgenitorComputeVersion",
    "-getProperty:ProgenitorVersion", "-getProperty:ProgenitorThunderstoreVersion")
  if ($Override) { $msbuildArgs += ("-p:ProgenitorVersion=" + $Override) }
  $json = (& dotnet @msbuildArgs) -join "`n"
  if ($LASTEXITCODE -ne 0) { throw "version computation failed: $json" }
  $props = ($json | ConvertFrom-Json).Properties
  $ver = [string]$props.ProgenitorVersion
  if ($ver -notmatch '^\d+\.\d+\.\d+(\.\d+)?$' -or $ver -eq "0.0.0.0") {
    throw "cannot package version '$ver' (see docs/versioning.md; git must be available with origin/main fetched)"
  }
  return @{ Version = $ver; Thunderstore = [string]$props.ProgenitorThunderstoreVersion }
}

Assert-NotForbidden $repo

$explicitVersion = $Version   # PowerShell names are case-insensitive: $version below replaces it
$versionInfo = Get-ProgenitorVersion -RepoRoot $repo -Override $explicitVersion
$version = $versionInfo.Version
$thunderstoreVersion = $versionInfo.Thunderstore
Write-Host "Progenitor $version (Release; Thunderstore version_number $thunderstoreVersion)"
# Components carry their own versions unless the caller forced one for everything.
$versionArgs = @()
if ($explicitVersion) { $versionArgs = @("-p:ProgenitorVersion=$version") }
if ($version -ne $thunderstoreVersion) {
  Write-Warning "$version is not a main build (it has a sub-release number). Its Thunderstore number $thunderstoreVersion is the main build's; never upload this zip."
}

$bootstrapProj = Join-Path $repo "src\Progenitor.Bootstrap\Progenitor.Bootstrap.csproj"
Assert-NotForbidden $bootstrapProj
& dotnet build $bootstrapProj -c Release -nologo -v q @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Bootstrap Release build failed" }

$preloaderProj = Join-Path $repo "src\Progenitor.Preloader\Progenitor.Preloader.csproj"
Assert-NotForbidden $preloaderProj
& dotnet build $preloaderProj -c Release -nologo -v q @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Preloader Release build failed" }

$coreProj = Join-Path $repo "src\Progenitor.Core\Progenitor.Core.csproj"
Assert-NotForbidden $coreProj
& dotnet build $coreProj -c Release -nologo -v q @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Core Release build failed" }

$devDll = $null
if ($IncludeDev) {
  $devProj = Join-Path $repo "src\Progenitor.Dev\Progenitor.Dev.csproj"
  Assert-NotForbidden $devProj
  & dotnet build $devProj -c Release -nologo -v q @versionArgs
  if ($LASTEXITCODE -ne 0) { throw "Dev Release build failed" }
  $devDll = Join-Path $repo "src\Progenitor.Dev\bin\Release\net6.0\Progenitor.Dev.dll"
  if (-not (Test-Path -LiteralPath $devDll)) { throw "built Progenitor.Dev.dll not found: $devDll" }
  Assert-NoExtraInstanceAuth $devDll
}

$bootstrapDll = Join-Path $repo "src\Progenitor.Bootstrap\bin\Release\net6.0\Progenitor.Bootstrap.dll"
if (-not (Test-Path -LiteralPath $bootstrapDll)) { throw "built Progenitor.Bootstrap.dll not found: $bootstrapDll" }

$preloaderDll = Join-Path $repo "src\Progenitor.Preloader\bin\Release\net6.0\Progenitor.Preloader.dll"
if (-not (Test-Path -LiteralPath $preloaderDll)) { throw "built Progenitor.Preloader.dll not found: $preloaderDll" }

$coreDll = Join-Path $repo "src\Progenitor.Core\bin\Release\net6.0\Progenitor.dll"
if (-not (Test-Path -LiteralPath $coreDll)) { throw "built Progenitor.dll not found: $coreDll" }

$packRoot = Join-Path $repo "packaging"
$tsRoot = Join-Path $packRoot "thunderstore"
$outDir = Join-Path $packRoot "out"
$stage = Join-Path $outDir "_stage"
$zipPath = Join-Path $outDir ("Progenitor-" + $version + ".zip")

Assert-NotForbidden $outDir
Assert-NotForbidden $stage
Assert-NotForbidden $zipPath

foreach ($required in @("manifest.json", "README.md", "CHANGELOG.md")) {
  $p = Join-Path $tsRoot $required
  if (-not (Test-Path -LiteralPath $p)) { throw "missing $p" }
}

if (Test-Path -LiteralPath $stage) {
  Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$manifestSrc = Join-Path $tsRoot "manifest.json"
$manifestDest = Join-Path $stage "manifest.json"
$manifestText = Get-Content -LiteralPath $manifestSrc -Raw
$stamped = [regex]::Replace(
  $manifestText,
  '"version_number"\s*:\s*"[^"]*"',
  ('"version_number": "' + $thunderstoreVersion + '"')
)
# PS 5.1 Set-Content -Encoding utf8 writes a BOM; Thunderstore accepts UTF-8 no BOM.
$utf8 = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($manifestDest, $stamped, $utf8)

Copy-Item -LiteralPath (Join-Path $tsRoot "README.md") -Destination (Join-Path $stage "README.md") -Force
Copy-Item -LiteralPath (Join-Path $tsRoot "CHANGELOG.md") -Destination (Join-Path $stage "CHANGELOG.md") -Force
# The mod-authoring guide at the zip root, beside the README (its links point into the repository).
Copy-Item -LiteralPath (Join-Path $repo "PROGENITOR_GUIDE.md") -Destination (Join-Path $stage "PROGENITOR_GUIDE.md") -Force

$icon = Join-Path $tsRoot "icon.png"
if (Test-Path -LiteralPath $icon) {
  Copy-Item -LiteralPath $icon -Destination (Join-Path $stage "icon.png") -Force
} else {
  Write-Warning "icon.png is missing (see packaging/thunderstore/ICON.txt). Thunderstore will reject a zip without a 256x256 PNG."
}

$pluginsDir = Join-Path $stage "BepInEx\plugins"
New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
Copy-Item -LiteralPath $bootstrapDll -Destination (Join-Path $pluginsDir "Progenitor.Bootstrap.dll") -Force
# The preloader patcher; BepInEx loads BepInEx/patchers before the chainloader.
$patchersDir = Join-Path $stage "BepInEx\patchers"
New-Item -ItemType Directory -Force -Path $patchersDir | Out-Null
Copy-Item -LiteralPath $preloaderDll -Destination (Join-Path $patchersDir "Progenitor.Preloader.dll") -Force
$coreDir = Join-Path $stage "BepInEx\progenitor-core"
New-Item -ItemType Directory -Force -Path $coreDir | Out-Null
Copy-Item -LiteralPath $coreDll -Destination (Join-Path $coreDir "Progenitor.dll") -Force
# Address-library tables ship next to Core so Bootstrap can resolve its detour sites from disk.
$tablesSrc = Join-Path $PSScriptRoot "addresslib"
# {client,server}-<build>.json only. tools/addresslib is a build-output folder (its .gitignore
# hides *.json), and Core writes build-<side>-<build>.json into the SAME folder name inside a game
# tree, so a wildcard here is one stray copy away from shipping a Resilience fingerprint or a hand
# -placed driftmap-<build>.json as though it were an address table.
$tables = @(Get-ChildItem -Path $tablesSrc -Filter "*.json" -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -like "client-*.json" -or $_.Name -like "server-*.json" })
if ($tables) {
  $tablesDir = Join-Path $coreDir "addresslib"
  New-Item -ItemType Directory -Force -Path $tablesDir | Out-Null
  foreach ($table in $tables) { Copy-Item -LiteralPath $table.FullName -Destination (Join-Path $tablesDir $table.Name) -Force }
} else {
  Write-Warning "no address tables under tools/addresslib; the package resolves detours by prefix scan only"
}

# The Progenitor guide, when the repository has it: BepInEx/progenitor-core/docs/guide/progenitor-guide.html is the
# file the Mods menu's Help guide tile and Progenitor's Help button open (ModHelp.ProgenitorGuidePath). The guide is one
# self-contained HTML file; nothing else in docs/guide is copied.
$guideSrc = Join-Path $repo "docs\guide\progenitor-guide.html"
if (Test-Path -LiteralPath $guideSrc) {
  $guideDir = Join-Path $coreDir "docs\guide"
  New-Item -ItemType Directory -Force -Path $guideDir | Out-Null
  Copy-Item -LiteralPath $guideSrc -Destination (Join-Path $guideDir "progenitor-guide.html") -Force
} else {
  Write-Warning "no docs/guide/progenitor-guide.html; the package has no Help guide"
}

if ($IncludeDev) {
  $reloadDir = Join-Path $stage "BepInEx\progenitor-reload"
  New-Item -ItemType Directory -Force -Path $reloadDir | Out-Null
  Copy-Item -LiteralPath $devDll -Destination (Join-Path $reloadDir "Progenitor.Dev.dll") -Force

  # Same sidecar copy as tools/deploy.ps1: <reload>/<AssemblyName>/{localization,assets}.
  $devDir = Split-Path -Parent $devDll
  $asmName = [System.IO.Path]::GetFileNameWithoutExtension($devDll)
  $locSrc = Join-Path $devDir "localization"
  if (Test-Path $locSrc) {
    $locDest = Join-Path $reloadDir (Join-Path $asmName "localization")
    if (Test-Path $locDest) {
      Remove-Item $locDest -Recurse -Force
    }
    $locParent = Join-Path $reloadDir $asmName
    New-Item -ItemType Directory -Force -Path $locParent | Out-Null
    Copy-Item $locSrc $locDest -Recurse -Force
    Write-Host "$asmName/localization -> reload folder"
  }
  $assetSrc = Join-Path $devDir "assets"
  if (Test-Path $assetSrc) {
    $assetDest = Join-Path $reloadDir (Join-Path $asmName "assets")
    if (Test-Path $assetDest) {
      Remove-Item $assetDest -Recurse -Force
    }
    $assetParent = Join-Path $reloadDir $asmName
    New-Item -ItemType Directory -Force -Path $assetParent | Out-Null
    Copy-Item $assetSrc $assetDest -Recurse -Force
    Write-Host "$asmName/assets -> reload folder"
  }
}

if (Test-Path -LiteralPath $zipPath) {
  Remove-Item -LiteralPath $zipPath -Force
}

# Assert the staged tree before it is sealed into a zip.
Assert-NoDevPayload -Root $stage -DevExpected ([bool]$IncludeDev)
Assert-NoDeveloperArtefacts -Root $stage

Add-Type -AssemblyName System.IO.Compression.FileSystem
$compression = [System.IO.Compression.CompressionLevel]::Optimal
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, $compression, $false)

# ...and again on the artifact itself, which is what actually reaches a player.
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
  $devEntries = @($zip.Entries | Where-Object { $_.Name -like "Progenitor.Dev.*" -or $_.Name -like "*.token" -or $_.Name -eq "progenitor-dev.enable" })
  if (-not $IncludeDev -and $devEntries.Count -gt 0) {
    throw "packaging assertion failed: $zipPath contains $(($devEntries | ForEach-Object { $_.FullName }) -join ', ')"
  }
  if ($IncludeDev -and -not ($zip.Entries | Where-Object { $_.Name -eq "Progenitor.Dev.dll" })) {
    throw "packaging assertion failed: -IncludeDev was passed but $zipPath has no Progenitor.Dev.dll"
  }
  # The public layout: these three DLLs at exactly these paths. Windows PowerShell's CreateFromDirectory writes '\'
  # separators, so entry names are compared with '/'.
  foreach ($requiredEntry in @("BepInEx/plugins/Progenitor.Bootstrap.dll", "BepInEx/patchers/Progenitor.Preloader.dll", "BepInEx/progenitor-core/Progenitor.dll")) {
    if (-not ($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $requiredEntry })) {
      throw "packaging assertion failed: $zipPath has no $requiredEntry"
    }
  }
  # ...and the developer-only artifacts, on the artifact itself. Same list as
  # Assert-NoDeveloperArtefacts, matched against zip entry paths (which use '/').
  $junk = @($zip.Entries | Where-Object {
    $_.Name -like "*.delta" -or $_.Name -like "*.delta-state" -or $_.Name -eq "progenitor-allow.txt" -or
    $_.Name -like "build-*.json" -or $_.Name -like "driftmap-*.json" -or $_.Name -eq "access.txt" -or
    $_.Name -like "*.pdb" -or $_.Name -like "*.deps.json" -or $_.FullName -like "*progenitor-pending/*"
  })
  if ($junk.Count -gt 0) {
    throw "packaging assertion failed: $zipPath contains developer-only artifacts: $(($junk | ForEach-Object { $_.FullName }) -join ', ')"
  }
} finally {
  $zip.Dispose()
}

Remove-Item -LiteralPath $stage -Recurse -Force

# SHA256SUMS next to the zips (job FRLD; threat model §12). Thunderstore carries no checksum
# and r2modman/Gale install without verifying one, so the only integrity story an operator can
# have is a hash we publish. The zip THIS run produced is listed, plus every file inside it,
# through Format-SumsRow -- the one formatter for the grammar ReloadAllowPolicy.RowGrammar
# documents, which is also what BepInEx/config/progenitor-allow.txt parses. Both row forms
# ('<name>' and '<zip>!<path>') are valid allowlist rows, so any row here pastes straight in.
# Only this invocation's zip is attested: globbing packaging/out attested stale zips from a
# previous version's run (R30 finding 11).
$sumsPath = Join-Path $outDir ("SHA256SUMS")
Assert-NotForbidden $sumsPath
$sums = New-Object System.Collections.Generic.List[string]
$sums.Add("# SHA256SUMS - Progenitor $version, written by tools/package.ps1")
$sums.Add("# Rows are '<sha256>  <name>'. Verify a download with:")
$sums.Add("#   Get-FileHash <zip> -Algorithm SHA256")
$sums.Add("# '<zip>!<path>' rows are the files inside that zip. Any row below -- either form --")
$sums.Add("# can be pasted as-is into BepInEx/config/progenitor-allow.txt to allowlist that file")
$sums.Add("# for the reload folder or Core; the name is matched on its leaf.")
$sums.Add("# See docs/modules.md, Hot reload.")
foreach ($z in @(Get-Item -LiteralPath $zipPath)) {
  $sums.Add("")
  $sums.Add((Format-SumsRow -Hash (Get-FileHash -LiteralPath $z.FullName -Algorithm SHA256).Hash -Name $z.Name))
  $archive = [System.IO.Compression.ZipFile]::OpenRead($z.FullName)
  try {
    foreach ($entry in @($archive.Entries | Sort-Object FullName)) {
      if ($entry.FullName.EndsWith("/")) { continue }
      $sha = [System.Security.Cryptography.SHA256]::Create()
      try {
        $stream = $entry.Open()
        try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
      } finally { $sha.Dispose() }
      $hex = ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
      $sums.Add((Format-SumsRow -Hash $hex -Name $entry.FullName -Container $z.Name))
    }
  } finally {
    $archive.Dispose()
  }
}
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllLines($sumsPath, $sums, $utf8NoBom)

Write-Host "wrote $zipPath"
Write-Host "wrote $sumsPath (zip + every zip entry, sha256)"
if ($IncludeDev) {
  Write-Host "included BepInEx/progenitor-reload/Progenitor.Dev.dll (-IncludeDev)"
} else {
  Write-Host "public layout: BepInEx/plugins/Progenitor.Bootstrap.dll + BepInEx/patchers/Progenitor.Preloader.dll + BepInEx/progenitor-core/Progenitor.dll (asserted; Dev omitted, asserted)"
}

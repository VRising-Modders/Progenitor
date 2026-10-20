<#
.SYNOPSIS
  Build an offline dummy Progenitor package and a local index that serves it, so the Install tab's
  hot-load lane can be exercised with no network and nothing published (job HOT1).

.DESCRIPTION
  Builds the chosen sample (-Sample HotDemo by default, job HOT2) and assembles

    <Out>/Progenitor-<Package>-<Version>.zip    (Package defaults to the sample's name, Version to 0.1.0)
        Progenitor.<Sample>.dll        the built plugin image
        progenitor.json                id/version/side/hotLoadable + the sha256 of that image, and
                                       compatibility.peers when the image's [NetworkCompatibility]
                                       sets Peers (read from the built DLL, so it always agrees)
        manifest.json                  Thunderstore package manifest
        README.md                      the listing page body
        icon.png                       256x256, written here (no image library needed)
    <Out>/index.json                   one package, one version, Thunderstore v1 shape,
                                       file:/// download_url and icon
    <Out>/README.md, <Out>/CHANGELOG.md   read only while the index lists one package; a listing's
                                       README is otherwise its own zip's README.md

  Point the client at it with

    [Install]
    LocalIndexPath = <Out>

  in BepInEx/config/progenitor.cfg. The catalog merges this index in front of the fetched
  one, downloads the zip through the file:// transport, inspects it, plans, and — when admission
  admits the image — writes the DLL into BepInEx/progenitor-reload/ where the reloader picks it up
  within a second. Read the merge back with the bridge op `install_local`.

  This script writes only under -Out (plus the sample's own bin/obj from the build). It contacts no
  game process, no server and no network.

.PARAMETER Kind
  Progenitor (the default) packages a Progenitor sample as described above. The other two build
  ordinary BepInEx packages, compiled here against the installed game's BepInEx\core, for tests and
  live checks that need a third-party-shaped package without naming a real one:

    Plain   one plain BepInEx plugin in the standard Thunderstore layout,
            <Out>/<Owner>-<Package>-<Version>.zip with BepInEx/plugins/<PluginName>/<PluginName>.dll,
            manifest.json (depends on BepInExPack_V_Rising), README.md, CHANGELOG.md and icon.png.
            -Paperwork adds review files a real package may carry (SOURCE_REVIEW.md at the root and
            the plugin's source under Source/), which the install route must leave out.
    Pair    a library and a mod that depends on it: <Owner>-<Library>-<Version>.zip and
            <Owner>-<Package>-<Version>.zip. The dependent's manifest lists the library's package,
            its plugin carries [BepInDependency] on the library's GUID, and its assembly references
            and calls the library's, so the load order and dependency closure have something real
            to resolve.

  -Publishable (Progenitor kind) shapes the package the way docs/publishing.md asks a mod author to:
  <Out>/<Owner>-<Package>-<Version>.zip with a side-prefixed description that says "Requires
  Progenitor.", a website_url, the BepInExPack dependency only (Progenitor is not a package), the
  README requirement block, CHANGELOG.md and LICENSE. -PublishFlaws builds the same package with
  deliberate faults for tools/publish-check.ps1's tests: a BOM in manifest.json, no side prefix, no
  BepInExPack dependency but a forbidden VRising_Modders-Progenitor-<ProgenitorVersion> one, a 128x128 icon, no README requirement block, a progenitor.json
  whose sha256 and side are wrong, and a stray dev bridge token.

  Both write <Out>/index.json listing every package built (with -Append, merged into an existing
  one). Plugin GUIDs are <owner>.<pluginname>, lower-cased. Nothing is published; no game process is
  contacted. The game folder comes from VRISING_GAME_DIR, else the default Steam path.

.NOTES
  -Side defaults to the side the chosen sample's image really declares, and nothing else is a valid
  default: HotDemo carries [ProgenitorSide(Side.Both)] and so is admissible for a client target;
  ReloadExample carries [ProgenitorSide(Side.Server)] and is refused for one with SideMismatch. Pass
  -Side only to match a plugin that actually loads on the side you name; declaring a side the image
  does not carry produces a package the admission evaluator refuses, which is the point of the check.

  Apply now also needs, on the process you are installing into:

    1. a reviewed qualification record for THIS EXACT IMAGE in
       BepInEx/config/progenitor-qualification.json. Author one with the bridge op

         install_local {"action":"qualify","zip":"<Out>\Progenitor-<Sample>-0.1.0.zip",
                        "target":"Client","evidence":"<where the review lives>"}

       A record is a review claim, never self-certification; a record embedded in a package is
       ignored and reported.
    2. an available Bootstrap admission snapshot (ProcessHost.AdmissionSnapshotCapability >= 1,
       job HOT2). A Bootstrap that predates it makes every admission Unknown.

  With either missing, admission is Unknown and the install is the honest restart route.
#>
[CmdletBinding()]
param(
    # Folder the zip, the index and the documents are written to. Created when missing.
    [Parameter(Mandatory = $true)]
    [string] $Out,

    # Which sample to package. HotDemo is [ProgenitorSide(Side.Both)] and is the one a client can
    # hot load; ReloadExample is server-only and stays available for the server lane. Sample is the
    # full Progenitor Sample (ServerMustHave, Exact) that the join flow's live recipe requires (JOINFLOW2).
    [ValidateSet('HotDemo', 'ReloadExample', 'Sample', 'FilmExample', 'NecroArmyExample', 'BossVariantsExample', 'UpdateProbe', 'UpdateProbeServer', 'UpdateProbeBoth')]
    [string] $Sample = 'HotDemo',

    # JOINFLOW2: the package version (Thunderstore version_number and progenitor.json version). The join
    # screens install the exact version a server requires, so this must be that version.
    [string] $Version = '0.1.0',

    # JOINFLOW2: the listing name (Thunderstore package name; the full name is Progenitor-<Package>).
    # Defaults to the sample's name.
    [string] $Package,

    # JOINFLOW2: declare hotLoadable false in progenitor.json, so every install takes the restart route.
    [switch] $NotHotLoadable,

    # JOINFLOW2: add this package (or this version of it) to an existing <Out>/index.json instead of
    # replacing the file. A version already listed is replaced; the new version is listed first.
    [switch] $Append,

    # JOINFLOW2: package this DLL instead of building the sample (an older build, for "Use version X").
    [string] $Dll,

    # Build configuration of the sample.
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # The side progenitor.json declares. Must match what the plugin image really loads in; the
    # default is the side the chosen sample's [ProgenitorSide] actually carries.
    [ValidateSet('server', 'client', 'both')]
    [string] $Side,

    # Skip dotnet build and use the DLL already in the sample's output folder.
    [switch] $NoBuild,

    # What to build: a Progenitor sample package (default), one plain BepInEx plugin package, or a
    # library plus a dependent mod (see .PARAMETER Kind).
    [ValidateSet('Progenitor', 'Plain', 'Pair')]
    [string] $Kind = 'Progenitor',

    # Plain/Pair: the Thunderstore owner (namespace) of the generated packages.
    [string] $Owner = 'ExampleTeam',

    # Plain/Pair: the plugin's DLL and folder name. Defaults to the package name.
    [string] $PluginName,

    # Pair: the library's package name (its DLL and folder use the same name).
    [string] $Library = 'ExampleLibrary',

    # Plain: add SOURCE_REVIEW.md and Source/ to the zip, as some real packages ship them.
    [switch] $Paperwork,

    # Progenitor kind: shape the package as docs/publishing.md asks (see .DESCRIPTION).
    [switch] $Publishable,

    # Progenitor kind: a -Publishable package with deliberate faults, for the publish checker's tests.
    [switch] $PublishFlaws,

    # -PublishFlaws: the version in the Progenitor package dependency it plants as a fault.
    [string] $ProgenitorVersion = '2026.10.1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot

# One table, so the plugin id, the declared side and the project can never drift apart. DeclaredSide
# is what the sample's [ProgenitorSide] really carries — the honest default for -Side.
$samples = @{
    HotDemo       = @{ Project = 'Progenitor.HotDemo'; PluginId = 'progenitor.hotdemo'; DeclaredSide = 'both' }
    ReloadExample = @{ Project = 'Progenitor.ReloadExample'; PluginId = 'progenitor.reloadexample'; DeclaredSide = 'server' }
    Sample        = @{ Project = 'Progenitor.Sample'; PluginId = 'progenitor.sample'; DeclaredSide = 'both' }
    # Install test matrix (2026-10-08): a client-only, a both-sides and a server-only package besides HotDemo and
    # ReloadExample, so every side has a hot and a restart-required case (-NotHotLoadable for the restart ones).
    # FilmExample is the client-only case (the Dye all sample it replaced is part of Core since 2026-10-08).
    FilmExample      = @{ Project = 'Progenitor.FilmExample'; PluginId = 'progenitor.film'; DeclaredSide = 'client' }
    NecroArmyExample = @{ Project = 'Progenitor.NecroArmyExample'; PluginId = 'progenitor.necroarmy'; DeclaredSide = 'both' }
    # BossVariantsExample, not TimedBuffExample: TimedBuffExample is ServerMustHave and must be on both sides.
    BossVariantsExample = @{ Project = 'Progenitor.BossVariantsExample'; PluginId = 'progenitor.bossvariants'; DeclaredSide = 'server' }
    # AUTOUPDATE2: the auto-update live tests' package (one build per version; samples/Progenitor.UpdateProbe/README.md).
    UpdateProbe = @{ Project = 'Progenitor.UpdateProbe'; PluginId = 'progenitor.updateprobe'; DeclaredSide = 'client' }
    # AUTOUPDATE3: the server's update tests: a server-only probe and one both sides run (built with
    # -p:UpdateProbeVariant=Server / Both; pass the build with -Dll).
    UpdateProbeServer = @{ Project = 'Progenitor.UpdateProbe'; PluginId = 'progenitor.updateprobe.server'; DeclaredSide = 'server'; DllName = 'Progenitor.UpdateProbe.Server.dll' }
    UpdateProbeBoth = @{ Project = 'Progenitor.UpdateProbe'; PluginId = 'progenitor.updateprobe.both'; DeclaredSide = 'both'; DllName = 'Progenitor.UpdateProbe.Both.dll' }
}

$chosen = $samples[$Sample]
$projectName = $chosen.Project
$project = Join-Path $repo "samples\$projectName\$projectName.csproj"
$dllName = if ($chosen.ContainsKey('DllName')) { $chosen.DllName } else { "$projectName.dll" }
$pluginId = $chosen.PluginId
if (-not $PSBoundParameters.ContainsKey('Side')) { $Side = $chosen.DeclaredSide }
$version = $Version
$hotLoadable = -not $NotHotLoadable
# Plain and Pair packages keep the -Owner parameter (the same variable: PowerShell names ignore case).
if ($PublishFlaws) { $Publishable = $true }
if ($Publishable -and $Kind -ne 'Progenitor') { throw '-Publishable and -PublishFlaws apply to -Kind Progenitor only' }
# A publishable package is published under the author's own team (-Owner), not Progenitor's.
if ($Kind -eq 'Progenitor' -and -not $Publishable) { $owner = 'Progenitor' }
$package = if ($Package) { $Package } else { $Sample }
$zipName = "$owner-$package-$version.zip"

if (-not (Test-Path -LiteralPath $project)) { throw "sample project not found: $project" }

function New-Directory([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
}

# ---- a 256x256 PNG, written by hand so no image library is needed ------------------------------
function New-SolidPng([string] $path, [int] $size, [byte] $r, [byte] $g, [byte] $b) {
    $crcTable = New-Object 'System.UInt32[]' 256
    for ($n = 0; $n -lt 256; $n++) {
        [uint32] $c = $n
        for ($k = 0; $k -lt 8; $k++) {
            if ($c -band 1) { $c = [uint32]([uint32]3988292384 -bxor ([uint32]($c -shr 1))) } else { $c = [uint32]($c -shr 1) }
        }
        $crcTable[$n] = $c
    }

    function Get-Crc32([byte[]] $bytes) {
        [uint32] $c = [uint32]::MaxValue
        foreach ($byte in $bytes) { $c = [uint32]($crcTable[[int](($c -bxor [uint32]$byte) -band 0xFF)] -bxor [uint32]($c -shr 8)) }
        return [uint32]($c -bxor [uint32]::MaxValue)
    }

    function Get-BigEndian([uint32] $value) {
        $b = [BitConverter]::GetBytes($value)
        if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($b) }
        return $b
    }

    function New-Chunk([string] $type, [byte[]] $data) {
        $typeBytes = [Text.Encoding]::ASCII.GetBytes($type)
        $body = $typeBytes + $data
        return (Get-BigEndian([uint32] $data.Length)) + $body + (Get-BigEndian(Get-Crc32 $body))
    }

    # Raw scanlines: one filter byte (0 = none) then size RGB triples.
    $raw = New-Object 'System.Collections.Generic.List[byte]'
    for ($y = 0; $y -lt $size; $y++) {
        $raw.Add([byte] 0)
        for ($x = 0; $x -lt $size; $x++) { $raw.Add($r); $raw.Add($g); $raw.Add($b) }
    }
    $rawBytes = $raw.ToArray()

    # zlib stream: 2-byte header, raw deflate, adler32 of the uncompressed bytes.
    $ms = New-Object System.IO.MemoryStream
    $deflate = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $deflate.Write($rawBytes, 0, $rawBytes.Length)
    $deflate.Dispose()
    $compressed = $ms.ToArray()
    $ms.Dispose()

    [uint32] $a = 1
    [uint32] $bb = 0
    foreach ($byte in $rawBytes) {
        $a = ($a + $byte) % 65521
        $bb = ($bb + $a) % 65521
    }
    $adler = Get-BigEndian([uint32](($bb -shl 16) -bor $a))
    $zlib = [byte[]] @(0x78, 0x9C) + $compressed + $adler

    $ihdr = (Get-BigEndian([uint32] $size)) + (Get-BigEndian([uint32] $size)) + [byte[]] @(8, 2, 0, 0, 0)
    $bytes = [byte[]] @(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) `
        + (New-Chunk 'IHDR' $ihdr) `
        + (New-Chunk 'IDAT' $zlib) `
        + (New-Chunk 'IEND' ([byte[]] @()))
    [IO.File]::WriteAllBytes($path, $bytes)
}

# ---- [NetworkCompatibility(..., Peers = "...")] from the image's bytes --------------------------
# Mod compatibility C3: package inspection refuses a package whose progenitor.json compatibility.peers
# disagrees with the plugin image's Peers, so the field is copied from the built DLL, never typed twice.
# A named string property is stored in a custom-attribute blob as 0x54 (property) 0x0E (string), the
# member name as a length-prefixed UTF-8 string ("Peers"), then the value the same way (0xFF: null).
# No assembly is loaded (Windows PowerShell 5.1 cannot load a net6.0 plugin image anyway).
function Get-DeclaredPeers([string] $path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    # ISO-8859-1 maps every byte to the char of the same value, so String.IndexOf finds byte patterns.
    $text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
    $marker = [string]::new([char[]] @(0x54, 0x0E, 0x05, 0x50, 0x65, 0x65, 0x72, 0x73))   # property, string, "Peers"
    $found = @()
    $i = $text.IndexOf($marker, [StringComparison]::Ordinal)
    while ($i -ge 0) {
        $at = $i + $marker.Length
        $i = $text.IndexOf($marker, $at, [StringComparison]::Ordinal)
        if ($at -ge $bytes.Length) { continue }
        $len = [int] $bytes[$at]
        if ($len -eq 0xFF -or $len -ge 0x80) { continue }   # null, or longer than any valid range (64 chars)
        if ($at + 1 + $len -gt $bytes.Length) { continue }
        $found += [Text.Encoding]::UTF8.GetString($bytes, $at + 1, $len).Trim()
    }
    $distinct = @($found | Where-Object { $_.Length -gt 0 } | Select-Object -Unique)
    if ($distinct.Count -gt 1) {
        throw "the image declares more than one Peers value ($($distinct -join ', ')); package one plugin per zip"
    }
    if ($distinct.Count -eq 1) { return $distinct[0] }
    return ''
}

# The Progenitor (Core) assembly version the image references, read from metadata only; $null when it does not
# reference Core or this PowerShell cannot read references without loading (reflection-only load is Windows PowerShell's).
function Get-ProgenitorReference([string] $path) {
    try {
        $asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($path)
        $ref = @($asm.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'Progenitor' })
        if ($ref.Count -gt 0) { return $ref[0].Version }
    }
    catch { Write-Host "progenitor range not written: $($_.Exception.Message)" }
    return $null
}

function Get-FileUrl([string] $path) {
    return ([Uri] (Resolve-Path -LiteralPath $path).Path).AbsoluteUri
}

# ---- Plain / Pair: ordinary BepInEx packages, compiled here --------------------------------------
function Get-GameBepInExCore {
    $game = if ($env:VRISING_GAME_DIR) { $env:VRISING_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\VRising' }
    $core = Join-Path $game 'BepInEx\core'
    foreach ($name in 'BepInEx.Core.dll', 'BepInEx.Unity.IL2CPP.dll', 'Il2CppInterop.Runtime.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $core $name))) { throw "$name not found under $core (set VRISING_GAME_DIR)" }
    }
    return $core
}

# Compiles one plugin DLL into $outDir and returns its path. $references are extra DLL paths.
function New-PluginDll([string] $name, [string] $guid, [string] $version, [string] $source,
                       [string] $outDir, [string] $core, [string[]] $references) {
    $proj = Join-Path ([IO.Path]::GetTempPath()) ("progenitor-plain-" + [Guid]::NewGuid().ToString('N'))
    New-Directory $proj
    try {
        $refs = @(
            (Join-Path $core 'BepInEx.Core.dll'),
            (Join-Path $core 'BepInEx.Unity.IL2CPP.dll'),
            (Join-Path $core 'Il2CppInterop.Runtime.dll')
        ) + @($references | Where-Object { $_ })
        $items = ($refs | ForEach-Object {
            "    <Reference Include=""$([IO.Path]::GetFileNameWithoutExtension($_))""><HintPath>$_</HintPath><Private>false</Private></Reference>"
        }) -join "`n"
        $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <AssemblyName>$name</AssemblyName>
    <Version>$version</Version>
    <Nullable>disable</Nullable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <Deterministic>true</Deterministic>
    <DebugType>none</DebugType>
  </PropertyGroup>
  <ItemGroup>
$items
  </ItemGroup>
</Project>
"@
        [IO.File]::WriteAllText((Join-Path $proj "$name.csproj"), $csproj)
        [IO.File]::WriteAllText((Join-Path $proj 'Plugin.cs'), $source)
        & dotnet build (Join-Path $proj "$name.csproj") -c Release -o (Join-Path $proj 'out') --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet build of $name failed ($LASTEXITCODE)" }
        New-Directory $outDir
        $dllPath = Join-Path $outDir "$name.dll"
        Copy-Item -LiteralPath (Join-Path $proj "out\$name.dll") -Destination $dllPath -Force
        return $dllPath
    }
    finally {
        Remove-Item -LiteralPath $proj -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Zips one ordinary package and returns its index row. $files maps a zip path to a local file.
function New-OrdinaryZip([string] $outFull, [string] $packageName, [string] $version, [string] $description,
                         [string[]] $dependencies, [System.Collections.Specialized.OrderedDictionary] $files, [string] $readmeBody) {
    $stage = Join-Path ([IO.Path]::GetTempPath()) ("progenitor-plainzip-" + [Guid]::NewGuid().ToString('N'))
    New-Directory $stage
    try {
        foreach ($entry in $files.GetEnumerator()) {
            $target = Join-Path $stage ($entry.Key -replace '/', '\')
            New-Directory (Split-Path -Parent $target)
            Copy-Item -LiteralPath $entry.Value -Destination $target -Force
        }

        $manifest = [ordered] @{
            name           = $packageName
            version_number = $version
            website_url    = ''
            description    = $description
            dependencies   = @($dependencies)
        }
        [IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), (ConvertTo-Json -InputObject $manifest -Depth 4))
        [IO.File]::WriteAllText((Join-Path $stage 'README.md'), "# $packageName`n`n$readmeBody`n`nGenerated by ``tools/make-dummy-package.ps1``; nothing here was published.`n")
        [IO.File]::WriteAllText((Join-Path $stage 'CHANGELOG.md'), "# $version`n`nGenerated test package.`n")
        New-SolidPng (Join-Path $stage 'icon.png') 256 68 68 102

        $zipName = "$Owner-$packageName-$version.zip"
        $zipPath = Join-Path $outFull $zipName
        if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }

    $now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $versionRow = [ordered] @{
        name           = $packageName
        full_name      = "$Owner-$packageName-$version"
        description    = $description
        icon           = ''
        version_number = $version
        dependencies   = @($dependencies)
        download_url   = (Get-FileUrl $zipPath)
        downloads      = 0
        date_created   = $now
        website_url    = ''
        is_active      = $true
        uuid4          = [Guid]::NewGuid().ToString()
        file_size      = (Get-Item -LiteralPath $zipPath).Length
    }
    Write-Host "Wrote $zipPath"
    return [ordered] @{
        name             = $packageName
        full_name        = "$Owner-$packageName"
        owner            = $Owner
        package_url      = ''
        date_created     = $now
        date_updated     = $now
        is_deprecated    = $false
        is_pinned        = $false
        has_nsfw_content = $false
        categories       = @('Client', 'Server', 'Mods')
        versions         = @($versionRow)
    }
}

if ($Kind -ne 'Progenitor') {
    $core = Get-GameBepInExCore
    New-Directory $Out
    $outFull = (Resolve-Path -LiteralPath $Out).Path
    $build = Join-Path ([IO.Path]::GetTempPath()) ("progenitor-plaindll-" + [Guid]::NewGuid().ToString('N'))
    New-Directory $build
    $bepInExPack = 'BepInEx-BepInExPack_V_Rising-1.733.2'
    $rows = @()
    try {
        if ($Kind -eq 'Plain') {
            $package = if ($PSBoundParameters.ContainsKey('Package')) { $Package } else { 'ExamplePlugin' }
            $plugin = if ($PluginName) { $PluginName } else { $package }
            $guid = ("$Owner.$plugin").ToLowerInvariant()
            $source = @"
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace $plugin;

/// <summary>A generated plain BepInEx plugin: it only logs that it loaded.</summary>
[BepInPlugin("$guid", "$plugin", "$Version")]
public sealed class Plugin : BasePlugin
{
    public override void Load() => Log.LogInfo("$plugin $Version loaded.");
}
"@
            $dllPath = New-PluginDll $plugin $guid $Version $source $build $core @()
            $files = [ordered] @{ "BepInEx/plugins/$plugin/$plugin.dll" = $dllPath }
            if ($Paperwork) {
                $src = Join-Path $build 'Plugin.cs'
                [IO.File]::WriteAllText($src, $source)
                $review = Join-Path $build 'SOURCE_REVIEW.md'
                [IO.File]::WriteAllText($review, "# Source review`n`nThe plugin's source is under Source/. Generated test package.`n")
                $files['SOURCE_REVIEW.md'] = $review
                $files['Source/Plugin.cs'] = $src
            }
            $rows += New-OrdinaryZip $outFull $package $Version "A generated plain BepInEx plugin ($guid)." @($bepInExPack) $files "A plain BepInEx plugin that logs one line when it loads."
        }
        else {
            $package = if ($PSBoundParameters.ContainsKey('Package')) { $Package } else { 'ExampleDependent' }
            $plugin = if ($PluginName) { $PluginName } else { $package }
            $libGuid = ("$Owner.$Library").ToLowerInvariant()
            $guid = ("$Owner.$plugin").ToLowerInvariant()
            $libSource = @"
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace $Library;

/// <summary>What a dependent mod calls.</summary>
public static class Api
{
    public static string Greeting(string who) => "$Library greets " + who + ".";
}

/// <summary>A generated library plugin other mods depend on.</summary>
[BepInPlugin("$libGuid", "$Library", "$Version")]
public sealed class Plugin : BasePlugin
{
    public override void Load() => Log.LogInfo("$Library $Version loaded.");
}
"@
            $libDll = New-PluginDll $Library $libGuid $Version $libSource (Join-Path $build 'lib') $core @()
            $depSource = @"
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace $plugin;

/// <summary>A generated mod that needs ${Library}: a BepInEx dependency and an assembly reference.</summary>
[BepInPlugin("$guid", "$plugin", "$Version")]
[BepInDependency("$libGuid")]
public sealed class Plugin : BasePlugin
{
    public override void Load() => Log.LogInfo($Library.Api.Greeting("$plugin"));
}
"@
            $depDll = New-PluginDll $plugin $guid $Version $depSource (Join-Path $build 'dep') $core @($libDll)
            $rows += New-OrdinaryZip $outFull $Library $Version "A generated library mod ($libGuid)." @($bepInExPack) ([ordered] @{ "BepInEx/plugins/$Library/$Library.dll" = $libDll }) "A library other mods depend on."
            $rows += New-OrdinaryZip $outFull $package $Version "A generated mod that depends on $Library ($guid)." @($bepInExPack, "$Owner-$Library-$Version") ([ordered] @{ "BepInEx/plugins/$plugin/$plugin.dll" = $depDll }) "A mod that needs $Library."
        }
    }
    finally {
        Remove-Item -LiteralPath $build -Recurse -Force -ErrorAction SilentlyContinue
    }

    $indexPath = Join-Path $outFull 'index.json'
    $index = @($rows)
    if ($Append -and (Test-Path -LiteralPath $indexPath)) {
        $names = @($rows | ForEach-Object { $_.full_name })
        $existing = ConvertFrom-Json (Get-Content -LiteralPath $indexPath -Raw)
        $index = @($existing | Where-Object { $names -notcontains $_.full_name }) + @($rows)
    }
    [IO.File]::WriteAllText($indexPath, (ConvertTo-Json -InputObject @($index) -Depth 8))
    Write-Host "Wrote $indexPath"
    Write-Host "Set [Install] LocalIndexPath = $outFull in BepInEx/config/progenitor.cfg to serve these packages."
    return
}

# ---- build ------------------------------------------------------------------------------------
if ($Dll) {
    if (-not (Test-Path -LiteralPath $Dll)) { throw "-Dll not found: $Dll" }
    # PowerShell names are case-insensitive: $image, not $dll, so the [string] -Dll parameter
    # is not overwritten (and coerced back to a string) by the FileInfo.
    $image = Get-Item -LiteralPath $Dll
}
else {
    if (-not $NoBuild) {
        Write-Host "Building $project ($Configuration)..."
        & dotnet build $project -c $Configuration --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
    }

    $image = Get-ChildItem -LiteralPath (Join-Path $repo "samples\$projectName\bin") -Recurse -Filter $dllName |
        Where-Object { $_.FullName -like "*\$Configuration\*" } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $image) { throw "built $dllName not found under samples\$projectName\bin ($Configuration)" }
}

$sha = (Get-FileHash -LiteralPath $image.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Image: $($image.FullName)"
Write-Host "sha256: $sha"

# ---- stage and zip ----------------------------------------------------------------------------
New-Directory $Out
$outFull = (Resolve-Path -LiteralPath $Out).Path
$stage = Join-Path ([IO.Path]::GetTempPath()) ("progenitor-dummy-" + [Guid]::NewGuid().ToString('N'))
New-Directory $stage

try {
    Copy-Item -LiteralPath $image.FullName -Destination (Join-Path $stage $dllName)

    $progenitorFields = [ordered] @{
        id          = $pluginId
        version     = $version
        side        = $Side
        hotLoadable = $hotLoadable
        sha256      = $sha
    }
    $peers = Get-DeclaredPeers $image.FullName
    if ($peers) {
        $progenitorFields['compatibility'] = [ordered] @{ peers = $peers }
        Write-Host "compatibility.peers: $peers (from the image's [NetworkCompatibility])"
    }
    # The Progenitor Index's range: this Core release or newer (what publish-check -WriteProgenitorJson writes).
    $coreRef = Get-ProgenitorReference $image.FullName
    if ($coreRef) {
        $progenitorFields['progenitor'] = ">=$($coreRef.Major).$($coreRef.Minor).$([Math]::Max($coreRef.Build, 0))"
        Write-Host "progenitor: $($progenitorFields['progenitor']) (the Core the image was compiled against)"
    }
    if ($PublishFlaws) {
        # Wrong on purpose: another file's hash and a side the image does not declare.
        $progenitorFields['sha256'] = ('0' * 64)
        $progenitorFields['side'] = if ($Side -eq 'server') { 'client' } else { 'server' }
    }
    $progenitorJson = $progenitorFields | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $stage 'progenitor.json'), $progenitorJson)

    $sideLabel = @{ client = 'Client-only'; server = 'Server-only'; both = 'Client and server' }[$Side]
    $readmeExtra = ''
    if ($Publishable) {
        $description = "$sideLabel $([char]0x00B7) Requires Progenitor. The $Sample sample, packaged the way docs/publishing.md asks."
        $dependencies = @('BepInEx-BepInExPack_V_Rising-1.733.2')
        $website = 'https://example.com/progenitor-sample'
        $readmeExtra = @"

> **Requires Progenitor.** This mod is built on [Progenitor](https://github.com/VRising-Modders/Progenitor), a modding framework for V Rising. Install Progenitor first, then install this mod from Progenitor's Mods menu.
"@
        if ($PublishFlaws) {
            $description = "The $Sample sample with deliberate packaging faults."
            $dependencies = @("VRising_Modders-Progenitor-$ProgenitorVersion")
            $readmeExtra = ''
        }
    }
    else {
        $description = "Dummy Progenitor package: the $Sample sample, packaged locally to exercise the Install tab."
        $dependencies = @()
        $website = ''
    }

    $manifest = [ordered] @{
        name          = $package
        version_number = $version
        website_url   = $website
        description   = $description
        dependencies  = @($dependencies)
    } | ConvertTo-Json -Depth 4
    # UTF-8 without a BOM (Thunderstore rejects one); -PublishFlaws writes one on purpose.
    [IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), $manifest, (New-Object System.Text.UTF8Encoding($PublishFlaws.IsPresent)))

    $readme = @"
# $package

A dummy package built by ``tools/make-dummy-package.ps1``. It carries the Progenitor $Sample sample
plugin and a ``progenitor.json`` that declares ``hotLoadable: $($hotLoadable.ToString().ToLowerInvariant())`` for side ``$Side``.
$readmeExtra
Nothing here was published. The listing exists only in the local ``index.json`` beside this file.
"@
    [IO.File]::WriteAllText((Join-Path $stage 'README.md'), $readme)

    if ($Publishable) {
        [IO.File]::WriteAllText((Join-Path $stage 'CHANGELOG.md'), "# $version`n`nGenerated test package.`n")
        [IO.File]::WriteAllText((Join-Path $stage 'LICENSE'), "Generated test package (a test fixture built from a Progenitor sample).`nLicensed under the GNU Lesser General Public License, version 2.1 only (LGPL-2.1-only),`nthe same license as the Progenitor repository. See LICENSE in the repository root.`n")
    }
    if ($PublishFlaws) {
        [IO.File]::WriteAllText((Join-Path $stage 'progenitor-dev.token'), "not-a-real-token`n")
    }

    New-SolidPng (Join-Path $stage 'icon.png') ($(if ($PublishFlaws) { 128 } else { 256 })) 34 102 68

    $zipPath = Join-Path $outFull $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

    Copy-Item -LiteralPath (Join-Path $stage 'README.md') -Destination (Join-Path $outFull 'README.md') -Force
    [IO.File]::WriteAllText((Join-Path $outFull 'CHANGELOG.md'), "# $version`n`nFirst local build of the dummy package.`n")
    Copy-Item -LiteralPath (Join-Path $stage 'icon.png') -Destination (Join-Path $outFull 'icon.png') -Force
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- the local index --------------------------------------------------------------------------
$zipUrl = Get-FileUrl (Join-Path $outFull $zipName)
$iconUrl = Get-FileUrl (Join-Path $outFull 'icon.png')
$size = (Get-Item -LiteralPath (Join-Path $outFull $zipName)).Length
$now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$versionRow = [ordered] @{
    name           = $package
    full_name      = "$owner-$package-$version"
    description    = 'Dummy Progenitor package for the Install tab hot-load lane.'
    icon           = $iconUrl
    version_number = $version
    dependencies   = @()
    download_url   = $zipUrl
    downloads      = 0
    date_created   = $now
    website_url    = ''
    is_active      = $true
    uuid4          = [Guid]::NewGuid().ToString()
    file_size      = $size
}

$packageRow = [ordered] @{
    name           = $package
    full_name      = "$owner-$package"
    owner          = $owner
    package_url    = ''
    date_created   = $now
    date_updated   = $now
    is_deprecated  = $false
    is_pinned      = $false
    has_nsfw_content = $false
    categories     = @('Client', 'Server', 'Oakveil Update', 'Mods')
    versions       = @($versionRow)
}

$indexPath = Join-Path $outFull 'index.json'
$index = @($packageRow)
if ($Append -and (Test-Path -LiteralPath $indexPath)) {
    # JOINFLOW2: keep the other packages and versions already listed; this version is listed first.
    # Not piped: Windows PowerShell 5.1 emits a parsed array as one object from a pipeline.
    $existing = ConvertFrom-Json (Get-Content -LiteralPath $indexPath -Raw)
    $index = @()
    $merged = $false
    foreach ($row in $existing) {
        if ($row.full_name -eq "$owner-$package") {
            $others = @($row.versions | Where-Object { $_.version_number -ne $version })
            $row.versions = @($versionRow) + $others
            $row.date_updated = $now
            $merged = $true
        }
        $index += $row
    }
    if (-not $merged) { $index += $packageRow }
}

# The v1 index is an array even with one package; piping a single object to ConvertTo-Json unwraps it (live 2026-09-12: 'the package index must be a JSON array of packages').
[IO.File]::WriteAllText($indexPath, (ConvertTo-Json -InputObject @($index) -Depth 8))

Write-Host ""
Write-Host "Wrote:"
Write-Host "  $zipPath"
Write-Host "  $indexPath"
Write-Host ""
Write-Host "Set [Install] LocalIndexPath = $outFull in BepInEx/config/progenitor.cfg,"
Write-Host "then check the merge with the bridge op: install_local {""action"":""status""}."
Write-Host ""
Write-Host "Apply now also needs a reviewed record for these exact bytes. Author one with:"
Write-Host "  install_local {""action"":""qualify"",""zip"":""$zipPath"",""target"":""Client"",""evidence"":""<where the review lives>""}"
Write-Host "A record is a review claim, never self-certification; an embedded one is ignored."
if ($Side -eq 'server') {
    Write-Host ""
    Write-Warning "progenitor.json declares side 'server': a client target is not admitted for it. Install it to the local server, or package the HotDemo sample (-Sample HotDemo), which declares Side.Both."
}

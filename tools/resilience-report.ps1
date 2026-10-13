<#
.SYNOPSIS
  Offline update-resilience report for a V Rising install (roadmap M4 item 1).

.DESCRIPTION
  Runs the SAME checks Progenitor runs at startup, without launching the game and without
  loading a single game assembly:

    * fingerprint GameAssembly.dll (the address-library build id plus a full SHA-256) and
      every DLL in <game>\BepInEx\interop (name, assembly version, length, SHA-256);
    * compare with the fingerprint stored beside the address tables as
      build-<side>-<build>.json, reporting added / removed / changed interop assemblies;
    * report whether an address table <side>-<build>.json exists for the build now installed
      and which of Progenitor's detour sites still have an entry in it;
    * re-resolve, with System.Reflection.Metadata, every ProjectM.* / Unity.* type and member
      that Progenitor.dll's IL references, and name the ones that no longer bind;
    * on a build id that has no archive yet, copy <game>\BepInEx\interop to
      interop-<side>-<build>\ beside the address tables, keeping the newest few per side, and
      then print the archives on disk and the exact tools/driftmap command line for this build.
      BepInEx regenerates the interop folder in place on a game update, so the archive is the
      only way the rewriter's mandatory --old input can exist at all (job FUPD4).

  It does this by generating a tiny console project OUTSIDE the repo that compiles
  src\Progenitor.Core\Resilience\*.cs and src\Progenitor.Core\Native\*.cs directly, so the tool
  and the game share one implementation instead of a second copy of the rules. First run pays
  a `dotnet build`; later runs reuse it (-Rebuild forces a fresh one).

  Read-only against the game folder except for the stored fingerprint (-NoWrite skips even that).
  It never starts, stops or touches a game process.

.PARAMETER GameDir
  V Rising install. Default: $env:VRISING_GAME_DIR, else the Steam default path.

.PARAMETER Side
  client or server. Decides which <side>-<build>.json is expected. Default: client.

.PARAMETER CoreDll
  Progenitor.dll whose game references are checked. Default: the one deployed in
  <game>\BepInEx\progenitor-core, else this repo's build output.

.PARAMETER TablesDir
  Extra folder(s) to search for <side>-<build>.json, tried after the deployed Core folder.
  Default: this repo's tools\addresslib.

.PARAMETER StoreDir
  Where build-<side>-<build>.json is read and written. Default: the deployed Core folder's
  addresslib subfolder (the same place the game writes it), so a report before launch and the
  game's own startup block agree.

.PARAMETER NoWrite
  Do not store the fingerprint, and do not archive the interop folder. Use for a read-only
  inspection of somebody else's install.

.PARAMETER NoArchive
  Do not archive the interop folder even when the fingerprint is stored. The archives on disk
  and the driftmap command line are still printed.

.PARAMETER ArchiveKeep
  How many archived interop sets to keep for this side (1..10). Default: the [Resilience]
  InteropArchives value in BepInEx\config\progenitor.cfg, else 3.

.PARAMETER NoMembers
  Skip the member-level scan (fingerprint and address table only). Much faster.

.PARAMETER FastPath
  Reuse a stored hash when a file's length and last-write time are unchanged. The game does
  this at startup; the tool does not, unless asked.

.PARAMETER RunnerDir
  Where the generated runner project lives. Default: $env:LOCALAPPDATA\Progenitor\resilience-runner.

.PARAMETER Rebuild
  Regenerate and rebuild the runner even when it looks current.

.OUTPUTS
  The same block the game logs. Exit code: 0 unchanged, 2 changed but nothing of ours breaks,
  1 something breaks (no address table for this build, a detour site without an entry, a
  member reference that no longer binds, or a scan in which no reference could be checked at
  all), 3 the tool itself failed (a bad -GameDir, a runner build failure, an unknown argument).

  "Unchanged" needs a stored fingerprint to compare against, so the very first run on a healthy
  install exits 2, not 0; the second run exits 0.

.EXAMPLE
  pwsh tools/resilience-report.ps1
  pwsh tools/resilience-report.ps1 -Side server -NoWrite
  pwsh tools/resilience-report.ps1 -GameDir 'D:\Games\VRising' -NoMembers
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [ValidateSet('client', 'server')]
    [string]$Side = 'client',
    [string]$CoreDll,
    [string[]]$TablesDir,
    [string]$StoreDir,
    [switch]$NoWrite,
    [switch]$NoArchive,
    [ValidateRange(1, 10)]
    [int]$ArchiveKeep = 0,
    [switch]$NoMembers,
    [switch]$FastPath,
    [string]$RunnerDir,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Fail([string]$message) {
    # NOT Write-Error: under $ErrorActionPreference = 'Stop' that raises a terminating error,
    # unwinds past the `exit 3` below and the host exits 1 - so every tool failure read as
    # "something of ours breaks" (R31 finding 25). WriteErrorLine just writes to stderr.
    $host.UI.WriteErrorLine("resilience-report: $message")
    exit 3
}

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    if ($env:VRISING_GAME_DIR) { $GameDir = $env:VRISING_GAME_DIR }
    else { $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\VRising' }
}
if (-not (Test-Path -LiteralPath $GameDir)) { Fail "Game folder not found: $GameDir" }

$gameAssembly = Join-Path $GameDir 'GameAssembly.dll'
if (-not (Test-Path -LiteralPath $gameAssembly)) { Fail "GameAssembly.dll not found in $GameDir" }
$interopDir = Join-Path $GameDir 'BepInEx\interop'
$deployedCoreDir = Join-Path $GameDir 'BepInEx\progenitor-core'

if ([string]::IsNullOrWhiteSpace($CoreDll)) {
    $candidates = @(
        (Join-Path $deployedCoreDir 'Progenitor.dll'),
        (Join-Path $repoRoot 'src\Progenitor.Core\bin\Release\net6.0\Progenitor.dll'),
        (Join-Path $repoRoot 'src\Progenitor.Core\bin\Debug\net6.0\Progenitor.dll')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { $CoreDll = $candidate; break }
    }
}
if ([string]::IsNullOrWhiteSpace($CoreDll)) {
    Write-Warning 'No Progenitor.dll found; the member-level check will be skipped.'
    $CoreDll = ''
}

if (-not $TablesDir) { $TablesDir = @((Join-Path $repoRoot 'tools\addresslib')) }
$tableSearch = @($deployedCoreDir) + $TablesDir

if ([string]::IsNullOrWhiteSpace($StoreDir)) {
    $addressSub = Join-Path $deployedCoreDir 'addresslib'
    if (Test-Path -LiteralPath $addressSub) { $StoreDir = $addressSub } else { $StoreDir = $deployedCoreDir }
}

if ([string]::IsNullOrWhiteSpace($RunnerDir)) {
    $localApp = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($localApp)) { $localApp = $env:TEMP }
    $RunnerDir = Join-Path $localApp 'Progenitor\resilience-runner'
}

# ---------------------------------------------------------------------------
# Generate the runner. It lives outside the repo so it does not inherit
# Directory.Build.props (net6.0 + a hard requirement on the interop folder).
# ---------------------------------------------------------------------------
$projectPath = Join-Path $RunnerDir 'ResilienceRunner.csproj'
$programPath = Join-Path $RunnerDir 'Program.cs'

$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <!-- GENERATED by tools/resilience-report.ps1. Edits are overwritten. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>ResilienceRunner</AssemblyName>
    <RootNamespace>Progenitor.Tools</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <InvariantGlobalization>true</InvariantGlobalization>
    <!-- Same switch Progenitor.Tests uses: it compiles out AddressLibrary's BepInEx
         Plugin.Logger calls, which is exactly what a game-free runner needs. -->
    <DefineConstants>`$(DefineConstants);PROGENITOR_TESTS</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <!-- Named, not globbed: Native\KeyTableProbe.cs is the one file in that folder that
         does reference the game. -->
    <Compile Include="$repoRoot\src\Progenitor.Core\Native\AddressId.cs" />
    <Compile Include="$repoRoot\src\Progenitor.Core\Native\AddressTable.cs" />
    <Compile Include="$repoRoot\src\Progenitor.Core\Native\AddressLibrary.cs" />
    <Compile Include="$repoRoot\src\Progenitor.Core\Native\DetourSite.cs" />
    <!-- The two non-Native game-free files Resilience compiles against: the drift map the
         report joins (FCOMPAT3) and the attribute whose AnySignature constant the member scan
         compares to (FACC). Both were needed the moment those jobs landed; without them this
         runner does not compile, which is a thing only a fresh -Rebuild finds out. -->
    <Compile Include="$repoRoot\src\Progenitor.Core\Compat\DriftMap.cs" />
    <Compile Include="$repoRoot\src\Progenitor.Core\Access\AccessMemberAttribute.cs" />
    <Compile Include="$repoRoot\src\Progenitor.Core\Resilience\*.cs" />
  </ItemGroup>
</Project>
"@

$program = @'
// GENERATED by tools/resilience-report.ps1. The checks live in Progenitor.Core; this is argv.
using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Native;
using Progenitor.Resilience;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Offline the tool hashes everything: correctness beats the startup fast path.
        // The interop archive is on for the same reason it is on in the game: BepInEx
        // regenerates BepInEx/interop in place, so a build whose set was never copied can never
        // be a `driftmap --old` again. --no-write and --no-archive both turn it off.
        var options = new ResilienceOptions { UseLengthAndTimeFastPath = false, ArchiveInterop = true };
        var tableDirs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : "";
            switch (args[i])
            {
                case "--side": options.Side = Next(); break;
                case "--game-assembly": options.GameAssemblyPath = Next(); break;
                case "--interop": options.InteropDirectory = Next(); break;
                case "--tables": tableDirs.Add(Next()); break;
                case "--store": options.StoreDirectory = Next(); break;
                case "--core": options.CoreAssemblyPath = Next(); break;
                case "--no-write": options.WriteStore = false; options.ArchiveInterop = false; break;
                case "--no-archive": options.ArchiveInterop = false; break;
                case "--archive-keep": options.ArchiveRetain = InteropArchive.ClampRetain(Next()); break;
                case "--no-members": options.ScanMembers = false; break;
                case "--fast-path": options.UseLengthAndTimeFastPath = true; break;
                default:
                    Console.Error.WriteLine("unknown argument: " + args[i]);
                    return 3;
            }
        }

        if (tableDirs.Count > 0) options.AddressTableDirectory = tableDirs[0];
        options.ArchiveDirectory = options.StoreDirectory;
        options.TableLookup = (side, build) =>
        {
            foreach (var dir in tableDirs)
            {
                var table = ResilienceCheck.LookupTableFile(dir, side, build);
                if (table != null) return table;
            }
            return null;
        };

        if (!string.IsNullOrEmpty(options.CoreAssemblyPath))
        {
            try { options.CoreVersion = System.Reflection.AssemblyName.GetAssemblyName(options.CoreAssemblyPath).Version?.ToString() ?? ""; }
            catch { options.CoreVersion = ""; }
        }

        ResilienceResult result;
        try { result = ResilienceCheck.Run(options); }
        catch (Exception e)
        {
            Console.Error.WriteLine("resilience check failed: " + e);
            return 3;
        }

        Console.WriteLine(result.Report);
        // Same prefix the game uses (Resilience.RunPass), so the `Resilience:` grep the report's
        // own doc promises finds the tool's notes too.
        foreach (var note in result.Notes) Console.WriteLine("  note        Resilience: " + note);
        if (result.StoredPath.Length > 0) Console.WriteLine("  stored      " + result.StoredPath);
        Console.WriteLine("  paths       game=" + options.GameAssemblyPath);
        Console.WriteLine("              interop=" + options.InteropDirectory);
        Console.WriteLine("              core=" + (options.CoreAssemblyPath.Length == 0 ? "(none)" : options.CoreAssemblyPath));

        // Job FUPD4. The two things an operator needs the morning after a game update: which
        // interop sets still exist, and the one command that turns two of them into a drift map.
        string archiveRoot = options.ArchiveDirectory.Length > 0 ? options.ArchiveDirectory : options.StoreDirectory;
        var archives = InteropArchive.List(archiveRoot, options.Side);
        Console.WriteLine("  archives    " + (archives.Count == 0
            ? "none in " + archiveRoot
            : archives.Count + " in " + archiveRoot + " (newest first)"));
        foreach (var entry in archives)
            Console.WriteLine("              " + entry.FolderName
                              + (string.Equals(entry.Build, result.Current.Build, StringComparison.Ordinal)
                                  ? "   <- the build installed now"
                                  : "")
                              + "   " + entry.ModifiedUtc.ToString("u"));
        Console.WriteLine("  driftmap    " + InteropArchive.FormatDriftMapCommand(
            archiveRoot, options.Side, result.Current.Build, options.InteropDirectory));

        if (result.Diff.HasBreakage) return 1;
        if (!result.Diff.Unchanged) return 2;
        return 0;
    }
}
'@

$needsWrite = $Rebuild -or (-not (Test-Path -LiteralPath $projectPath)) -or (-not (Test-Path -LiteralPath $programPath))
if (-not $needsWrite) {
    # TrimEnd: Set-Content adds a trailing newline, so a raw compare would always differ
    # and every run would rebuild the runner.
    $existing = (Get-Content -LiteralPath $projectPath -Raw).TrimEnd()
    if ($existing -ne $csproj.TrimEnd()) { $needsWrite = $true }
    $existingProgram = (Get-Content -LiteralPath $programPath -Raw).TrimEnd()
    if ($existingProgram -ne $program.TrimEnd()) { $needsWrite = $true }
}

if ($needsWrite) {
    New-Item -ItemType Directory -Force -Path $RunnerDir | Out-Null
    Set-Content -LiteralPath $projectPath -Value $csproj -Encoding utf8
    Set-Content -LiteralPath $programPath -Value $program -Encoding utf8
}

$runnerExe = Join-Path $RunnerDir 'bin\Release\net8.0\ResilienceRunner.dll'
# Every folder the generated csproj compiles from, or an edit in one of them would not rebuild
# the runner and the report would quietly be the previous version's.
$sourceStamp = (Get-ChildItem -Path (Join-Path $repoRoot 'src\Progenitor.Core\Resilience'), (Join-Path $repoRoot 'src\Progenitor.Core\Native'), (Join-Path $repoRoot 'src\Progenitor.Core\Compat'), (Join-Path $repoRoot 'src\Progenitor.Core\Access') -Filter *.cs |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
$needsBuild = $Rebuild -or $needsWrite -or (-not (Test-Path -LiteralPath $runnerExe))
if (-not $needsBuild) {
    if ((Get-Item -LiteralPath $runnerExe).LastWriteTimeUtc -lt $sourceStamp) { $needsBuild = $true }
}

if ($needsBuild) {
    Write-Host "Building the offline runner in $RunnerDir ..."
    $build = & dotnet build $projectPath -c Release -nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        $build | Write-Host
        Fail "runner build failed (exit $LASTEXITCODE)"
    }
}

$runnerArgs = @(
    $runnerExe,
    '--side', $Side,
    '--game-assembly', $gameAssembly,
    '--interop', $interopDir,
    '--store', $StoreDir
)
foreach ($dir in $tableSearch) { $runnerArgs += @('--tables', $dir) }
if (-not [string]::IsNullOrWhiteSpace($CoreDll)) { $runnerArgs += @('--core', $CoreDll) }
if ($NoWrite) { $runnerArgs += '--no-write' }
if ($NoArchive) { $runnerArgs += '--no-archive' }
if ($ArchiveKeep -gt 0) { $runnerArgs += @('--archive-keep', "$ArchiveKeep") }
if ($NoMembers) { $runnerArgs += '--no-members' }
if ($FastPath) { $runnerArgs += '--fast-path' }

& dotnet $runnerArgs
exit $LASTEXITCODE

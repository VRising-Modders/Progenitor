<#
.SYNOPSIS
  Check a built mod package before publishing it on Thunderstore, or write its progenitor.json
  from the plugin DLL (docs/publishing.md).

.DESCRIPTION
  Runs tools/publishcheck (a game-free .NET tool: it reads the zip or folder and the DLL's metadata,
  loads no assembly and contacts nothing). Two uses:

    Check a package (a zip, or the folder you zip). One PASS / WARN / FAIL line per check:

      powershell -File tools/publish-check.ps1 -Package out\MyTeam-MyMod-1.2.0.zip

    Write progenitor.json from the DLL's own attributes ([BepInPlugin], [ProgenitorSide],
    [NetworkCompatibility] Peers) and its sha256. Only hotLoadable is your call:

      powershell -File tools/publish-check.ps1 -Dll bin\Release\net6.0\MyMod.dll -WriteProgenitorJson pkg\progenitor.json -HotLoadable

  Exit code 0 when nothing failed (warnings allowed), 1 when a check failed, 2 on a usage or input
  error. The checks and what each one means are listed in docs/publishing.md.

.PARAMETER Package
  The package zip, or the folder whose contents become the zip root.

.PARAMETER Json
  Print the check report as JSON instead of lines.

.PARAMETER Dll
  The plugin DLL to describe in progenitor.json.

.PARAMETER WriteProgenitorJson
  Where to write progenitor.json (UTF-8 without a byte-order mark). Omit to print it.

.PARAMETER HotLoadable
  Declare hotLoadable: true. Refused when the DLL reads ctx.UnsafeHarmony or registers its own
  IL2CPP types, which cannot be reloaded in place.

.PARAMETER Id
  The plugin GUID to describe when the DLL declares more than one [BepInPlugin].
#>
[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Check', Position = 0)]
    [string] $Package,

    [Parameter(ParameterSetName = 'Check')]
    [switch] $Json,

    [Parameter(Mandatory = $true, ParameterSetName = 'Write')]
    [string] $Dll,

    [Parameter(ParameterSetName = 'Write')]
    [string] $WriteProgenitorJson,

    [Parameter(ParameterSetName = 'Write')]
    [switch] $HotLoadable,

    [Parameter(ParameterSetName = 'Write')]
    [string] $Id
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'publishcheck\Progenitor.PublishCheck.csproj'

& dotnet build $project -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "building tools/publishcheck failed ($LASTEXITCODE); run: dotnet build $project -c Release"
    exit 2
}

$exe = Join-Path $PSScriptRoot 'publishcheck\bin\Release\net8.0\publishcheck.dll'
if ($PSCmdlet.ParameterSetName -eq 'Check') {
    $toolArgs = @('check', (Resolve-Path -LiteralPath $Package).Path)
    if ($Json) { $toolArgs += '--json' }
}
else {
    $toolArgs = @('progenitor-json', (Resolve-Path -LiteralPath $Dll).Path)
    if ($HotLoadable) { $toolArgs += '--hot-loadable' }
    if ($Id) { $toolArgs += @('--id', $Id) }
    # dotnet runs with its own working directory; resolve a relative path against this PowerShell location.
    if ($WriteProgenitorJson) { $toolArgs += @('--out', $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WriteProgenitorJson)) }
}

& dotnet $exe @toolArgs
exit $LASTEXITCODE

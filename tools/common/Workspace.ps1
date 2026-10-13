<#
.SYNOPSIS
  The one workspace resolver for PowerShell tools. Dot-source it:

    . (Join-Path $PSScriptRoot "common\Workspace.ps1")

.DESCRIPTION
  The "workspace" is the folder that holds the MAIN checkout of this repository and its sibling
  folders (<workspace>\_testserver, <workspace>\_tools,
  <workspace>\_headless, <workspace>\_observe ...). Nothing here hard-codes a machine path.

  Resolution order:
    1. $env:PROGENITOR_WORKSPACE, when set.
    2. git rev-parse --path-format=absolute --git-common-dir, run in the repository that contains
       this file. The common dir is <main checkout>\.git for the main checkout and for every linked
       worktree, so its parent is the main checkout and THAT folder's parent is the workspace.
    3. The same answer read from git's own metadata without the git executable: a '.git' FILE
       (linked worktree) names its gitdir, whose 'commondir' file names the common dir; a '.git'
       DIRECTORY is the common dir.
    4. The parent of the repository root that contains this file.
  Returns $null when none of these yields an existing folder; callers that need a default then
  refuse (Resolve-ProgenitorWorkspacePath throws) rather than guess.

  Read-only: it runs git rev-parse and reads files, and never writes.
  The Node twin is tools/common/workspace.mjs; keep the two in step.
#>

if (-not (Test-Path variable:script:ProgenitorWorkspaceCache)) { $script:ProgenitorWorkspaceCache = $null }

function Get-ProgenitorRepoRoot {
  # tools\common\Workspace.ps1 -> repository root.
  return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
}

function Get-ProgenitorGitCommonDirFromFiles {
  param([Parameter(Mandatory = $true)][string]$RepoRoot)
  $dotGit = Join-Path $RepoRoot ".git"
  if (Test-Path -LiteralPath $dotGit -PathType Container) { return $dotGit }
  if (-not (Test-Path -LiteralPath $dotGit -PathType Leaf)) { return $null }
  $line = (Get-Content -LiteralPath $dotGit -TotalCount 1 -ErrorAction SilentlyContinue)
  if (-not $line -or $line -notmatch '^gitdir:\s*(.+)$') { return $null }
  $gitDir = $Matches[1].Trim()
  if (-not [System.IO.Path]::IsPathRooted($gitDir)) { $gitDir = Join-Path $RepoRoot $gitDir }
  $commonFile = Join-Path $gitDir "commondir"
  if (-not (Test-Path -LiteralPath $commonFile -PathType Leaf)) { return $gitDir }
  $common = (Get-Content -LiteralPath $commonFile -TotalCount 1 -ErrorAction SilentlyContinue)
  if (-not $common) { return $null }
  $common = $common.Trim()
  if (-not [System.IO.Path]::IsPathRooted($common)) { $common = Join-Path $gitDir $common }
  return [System.IO.Path]::GetFullPath($common)
}

function Get-ProgenitorGitCommonDir {
  param([Parameter(Mandatory = $true)][string]$RepoRoot)
  if (Get-Command git -CommandType Application -ErrorAction SilentlyContinue) {
    $eap = $ErrorActionPreference
    try {
      $ErrorActionPreference = "Continue"
      $out = & git -C $RepoRoot rev-parse --path-format=absolute --git-common-dir 2>$null
      if ($LASTEXITCODE -eq 0 -and $out) {
        $dir = ([string]($out | Select-Object -First 1)).Trim()
        if ($dir -and (Test-Path -LiteralPath $dir -PathType Container)) {
          return [System.IO.Path]::GetFullPath($dir)
        }
      }
    } catch {
      # fall through to the metadata read
    } finally {
      $ErrorActionPreference = $eap
    }
  }
  return Get-ProgenitorGitCommonDirFromFiles -RepoRoot $RepoRoot
}

function Get-ProgenitorWorkspaceRoot {
  <# The workspace folder, or $null when it cannot be resolved. Cached per session. #>
  if ($env:PROGENITOR_WORKSPACE) {
    return [System.IO.Path]::GetFullPath($env:PROGENITOR_WORKSPACE).TrimEnd('\')
  }
  if ($script:ProgenitorWorkspaceCache) { return $script:ProgenitorWorkspaceCache }

  $repo = Get-ProgenitorRepoRoot
  $candidate = $null
  $common = Get-ProgenitorGitCommonDir -RepoRoot $repo
  if ($common) {
    $main = Split-Path ($common.TrimEnd('\', '/')) -Parent
    if ($main) { $candidate = Split-Path $main -Parent }
  }
  if (-not $candidate) { $candidate = Split-Path $repo -Parent }
  if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Container)) { return $null }
  $script:ProgenitorWorkspaceCache = [System.IO.Path]::GetFullPath($candidate).TrimEnd('\')
  return $script:ProgenitorWorkspaceCache
}

function Get-ProgenitorMainCheckout {
  <# The main checkout (the folder that holds git's common dir); the repository root if unknown. #>
  $repo = Get-ProgenitorRepoRoot
  $common = Get-ProgenitorGitCommonDir -RepoRoot $repo
  if ($common) { return (Split-Path ($common.TrimEnd('\', '/')) -Parent) }
  return $repo
}

function Get-ProgenitorWorkspacePath {
  <# <workspace>\<Child>, or $null when the workspace cannot be resolved. #>
  param([Parameter(Mandatory = $true)][string]$Child)
  $ws = Get-ProgenitorWorkspaceRoot
  if (-not $ws) { return $null }
  return (Join-Path $ws $Child)
}

function Resolve-ProgenitorWorkspacePath {
  <#
    <workspace>\<Child>, or a terminating error naming the parameter the caller can pass instead.
    Use it for a parameter default: an unresolvable workspace must refuse, never guess a path.
  #>
  param(
    [Parameter(Mandatory = $true)][string]$Child,
    [string]$Parameter = ""
  )
  $p = Get-ProgenitorWorkspacePath -Child $Child
  if ($p) { return $p }
  $hint = if ($Parameter) { "pass $Parameter or set PROGENITOR_WORKSPACE" } else { "set PROGENITOR_WORKSPACE" }
  throw "cannot resolve the Progenitor workspace (the folder that holds the main checkout) for '$Child'; $hint"
}

<#
.SYNOPSIS
  The one path fence. Dot-sourced by tools/deploy.ps1, tools/stage-test-server.ps1,
  tools/package.ps1 and tools/test-server.ps1.

.DESCRIPTION
  R40 finding 4. Three of those four scripts used to fence C:\VRisingServer with

      if ($root -like "C:\VRisingServer*") { throw ... }

  on the RAW parameter string, while package.ps1 ten lines away had the correct
  GetFullPath-plus-boundary form. A -like on the raw string sees whatever spelling the
  caller typed, and Win32 accepts many spellings of the same directory:

      C:/VRisingServer            forward slashes (accepted everywhere by Win32)
      C:\VRisingServer\           trailing separator
      c:\vrisingserver            case
      \\?\C:\VRisingServer        extended-length prefix (also \\.\, also //?/)
      \\localhost\C$\VRisingServer   admin share for the local machine
      C:\VRISIN~1                 8.3 short name
      <workspace>\..\VRisingServer   dot segments
      <junction> -> C:\VRisingServer    a reparse point aimed at the live tree

  `stage-test-server.ps1 -GameDir C:/VRisingServer -TestRoot C:/VRisingServer` passed the
  old fence and reached `robocopy /MIR`, which DELETES destination files absent from the
  source. That is the one thing the fence exists to make impossible.

  So: every root parameter of every script goes through Assert-NotForbiddenPath before any
  write. The comparison is on a canonical form -- device prefixes stripped, slashes
  normalized, the local admin share mapped back to its drive, GetFullPath applied, 8.3
  segments expanded and reparse points followed for the part of the path that exists,
  trailing separators trimmed -- and it is a DIRECTORY-BOUNDARY compare, OrdinalIgnoreCase,
  so C:\VRisingServerBackup is not under C:\VRisingServer.

  R41 finding 6. The canonical form is only defined for TWO path shapes: `<drive>:\...` and
  `\\server\share\...`. `\\?\` and `\\.\` also prefix the whole Win32 DEVICE namespace --
  `\\?\Volume{…}\`, `\\?\GLOBALROOT\Device\HarddiskVolume3\`, `\\.\PhysicalDrive0`, `\\?\pipe\` --
  and for those the remainder after the four-character strip is NOT rooted, so GetFullPath
  resolved it against the CURRENT DIRECTORY and the fence compared a plausible-looking path
  under the repo instead of the volume the caller actually named. Both spellings therefore
  passed. Anything that is not one of the two recognized shapes is now REFUSED OUTRIGHT, before
  any comparison, as an "unrecognised path form" -- the fence would rather stop a legitimate
  exotic spelling than let a device path through, because nothing here has any business being
  handed one. Same for the empty string, and for anything still unrooted after normalization.

  R42 finding 8. Two more indirections are neither a spelling nor a reparse point, so neither the
  normalizer nor GetFullPath sees through them:

      subst S: C:\VRisingServer     a DOS-device drive definition; S:\ IS the live server
      net use Z: \\localhost\vrising  a mapped letter over a share of the same tree
      \\localhost\vrising\Plugins   a plain (non-admin) share of the live server

  `\\localhost\C$` was resolved by spelling, which made the omission worse: a reader who sees the
  admin share handled reasonably infers that share indirection in general is. It was not.
  ConvertTo-FenceCanonicalPath now resolves a drive letter through its DEFINITION before comparing
  -- QueryDosDevice answers subst (`\??\C:\VRisingServer`) and mapped letters
  (`\Device\LanmanRedirector\;Z:…\server\share`, whose UNC Get-PSDrive's DisplayRoot states
  directly) in one call -- and resolves a share of THIS machine through the local share table
  (Get-SmbShare, or `net share` as a fallback). Each redirection is fed back through the whole
  canonicalizer, so a subst onto a mapped letter onto a share still lands on a real local path.

  The failure mode is refusal: a drive that exists but whose definition cannot be read, or a local
  share the share table does not name, returns the unrecognised-form verdict. A letter with NO
  definition at all is left alone -- it names nothing, so nothing can be written through it, and
  refusing it would refuse `D:\...` on a machine without a D:.

  R43. "No definition" and "could not ask" are different answers. When the QueryDosDevice lookup
  itself is UNAVAILABLE -- the Add-Type that builds the P/Invoke fails (csc trouble in %TEMP%),
  or the call errors with anything but ERROR_FILE_NOT_FOUND -- the fence cannot tell a subst from
  a disk: a subst of C:\VRisingServer reports DriveType Fixed, so falling back to DriveType failed
  OPEN. In that state every letter that EXISTS is refused except the system drive, which is the
  boot volume and cannot be a subst. A letter that does not exist is still left alone.

  Everything here is read-only: it resolves and compares, it never creates or writes.
#>

# The roots deploy.ps1, stage-test-server.ps1 and test-server.ps1 refuse. The live server has
# players on it; nothing in this repo may write into it under any spelling.
$ProgenitorLiveServerRoots = @("C:\VRisingServer")

# package.ps1 additionally refuses the two trees a release zip must never be assembled inside.
# The verdict the resolvers return for a path whose FORM the fence does not recognize (R41
# finding 6). It is deliberately not a path -- '!' and ':' past the drive colon cannot both occur
# in a real Windows path -- so no comparison can ever confuse it with one, and Test-PathUnderRoot
# answers TRUE for it against every root: unrecognised means refused, not "somewhere else".
$ProgenitorFenceUnrecognisedPath = '!fence:unrecognised-path-form!'

# The verdict for a forbidden root this machine cannot place: the isolated test server's folder
# comes from the workspace resolver (tools/common/Workspace.ps1), not a literal. When neither the
# resolver nor PROGENITOR_TEST_ROOT can name it, the fence must not quietly drop that root -- that
# would make the forbidden list SMALLER -- so this marker stands in for it, and
# Assert-NotForbiddenPath refuses EVERY path while it is in the list.
$ProgenitorFenceUnresolvedRoot = '!fence:unresolved-root!'

. (Join-Path $PSScriptRoot "Workspace.ps1")
$ProgenitorFenceTestServerRoots = @()
$_fenceTestRoot = Get-ProgenitorWorkspacePath -Child "_testserver"
if ($_fenceTestRoot) { $ProgenitorFenceTestServerRoots += $_fenceTestRoot }
if ($env:PROGENITOR_TEST_ROOT) { $ProgenitorFenceTestServerRoots += $env:PROGENITOR_TEST_ROOT }
if ($ProgenitorFenceTestServerRoots.Count -eq 0) { $ProgenitorFenceTestServerRoots = @($ProgenitorFenceUnresolvedRoot) }
Remove-Variable _fenceTestRoot -ErrorAction SilentlyContinue

$ProgenitorPackageForbiddenRoots = @("C:\VRisingServer") + $ProgenitorFenceTestServerRoots + @(
  "C:\Program Files (x86)\Steam\steamapps\common\VRising"
)

function ConvertTo-FenceNormalizedPath {
  <#
    Spelling normalization that needs no filesystem access: slashes, device prefixes, the
    local admin share, then GetFullPath (which resolves . and .. and any relative form).

    Returns $ProgenitorFenceUnrecognisedPath for any input whose form the fence does not
    recognize (R41 finding 6): the empty string, an extended-length prefix over anything but a
    drive letter or a UNC share, and anything still not rooted as `<drive>:\...` or
    `\\server\share\...` once GetFullPath has run.
  #>
  param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Path)

  if ([string]::IsNullOrWhiteSpace($Path)) { return $ProgenitorFenceUnrecognisedPath }
  $p = $Path.Trim().Trim('"')
  # Win32 accepts '/' as a separator in every API these scripts reach, so the fence must too.
  $p = $p.Replace('/', '\')

  # \\?\ , \\.\ and their UNC forms are the same path with a longer name.
  if ($p.StartsWith('\\?\UNC\', [System.StringComparison]::Ordinal) -or
      $p.StartsWith('\\.\UNC\', [System.StringComparison]::Ordinal)) {
    $p = '\\' + $p.Substring(8)
  } elseif ($p.StartsWith('\\?\', [System.StringComparison]::Ordinal) -or
            $p.StartsWith('\\.\', [System.StringComparison]::Ordinal)) {
    # ...but ONLY over a drive letter. Over the device namespace the remainder is relative
    # ('Volume{…}\VRisingServer', 'GLOBALROOT\Device\HarddiskVolume3\VRisingServer'), and
    # GetFullPath would silently anchor it to the current directory (R41 finding 6). Refuse.
    $afterPrefix = $p.Substring(4)
    if ($afterPrefix -notmatch '^[A-Za-z]:(\\|$)') { return $ProgenitorFenceUnrecognisedPath }
    $p = $afterPrefix
  }

  # \\localhost\C$\... and \\<this machine>\C$\... are this machine's C:\.
  $m = [regex]::Match($p, '^\\\\(?<host>[^\\]+)\\(?<drive>[A-Za-z])\$(?<rest>\\.*)?$')
  if ($m.Success) {
    $host_ = $m.Groups['host'].Value
    $localNames = @('localhost', '.', '127.0.0.1', '::1', $env:COMPUTERNAME) | Where-Object { $_ }
    foreach ($n in $localNames) {
      if ([string]::Equals($host_, $n, [System.StringComparison]::OrdinalIgnoreCase)) {
        $p = $m.Groups['drive'].Value + ':' + $m.Groups['rest'].Value
        break
      }
    }
  }

  try { $p = [System.IO.Path]::GetFullPath($p) } catch { return $ProgenitorFenceUnrecognisedPath }

  # The two forms the fence knows how to canonicalize and compare. A genuinely relative path the
  # caller typed ('_testserver\server') is rooted by GetFullPath above and lands here as a drive
  # path, which is fine; what does NOT land here is a device name, a bare '\\', a '\\server' with
  # no share, or anything GetFullPath left unrooted. Those are refused rather than guessed at.
  if ($p -notmatch '^[A-Za-z]:\\' -and $p -notmatch '^\\\\[^\\]+\\[^\\]+') {
    return $ProgenitorFenceUnrecognisedPath
  }
  return $p
}

# The three system lookups the drive/share resolution below needs, as overridable scriptblocks
# (R42 finding 8). $null means "ask Windows"; a test sets one to drive a mapping it cannot create
# on the machine it runs on -- a `net use` needs a real share and a second host, and a subst of the
# LIVE SERVER must never be created to prove it is refused. Each takes one string and returns one
# string or $null.
$ProgenitorFenceDosDeviceLookup = $null    # 'S' -> '\??\C:\VRisingServer' | '\Device\HarddiskVolume3' | ...
$ProgenitorFenceDisplayRootLookup = $null  # 'Z' -> '\\localhost\vrising'
$ProgenitorFenceShareLookup = $null        # 'vrising' -> 'C:\VRisingServer'

# What Get-FenceDosDeviceTarget answers when it could not ASK (R43): the P/Invoke did not build, or
# QueryDosDevice failed for a reason other than "no such device". Distinct from $null, which means
# the letter has no definition. A test's $ProgenitorFenceDosDeviceLookup may return it (or throw)
# to drive the unavailable path. Not a path, for the same reason as the unrecognised sentinel.
$ProgenitorFenceDosDeviceUnavailable = '!fence:dos-device-lookup-unavailable!'

function Get-FenceDosDeviceTarget {
  <#
    The NT target of a drive letter, via QueryDosDevice -- the one call that answers `subst` and
    `net use` both. $null when the letter has no definition (which is also what a letter that does
    not exist looks like); $ProgenitorFenceDosDeviceUnavailable when the lookup itself could not be
    made -- the caller must not read that as "no definition" (R43).
  #>
  param([Parameter(Mandatory = $true)][string]$Letter)

  if ($ProgenitorFenceDosDeviceLookup) {
    try { $v = & $ProgenitorFenceDosDeviceLookup $Letter } catch { return $ProgenitorFenceDosDeviceUnavailable }
    if ($null -eq $v -or $v -eq '') { return $null }
    return [string]$v
  }

  try {
    if (-not ('Progenitor.FenceNative' -as [type])) {
      Add-Type -Namespace 'Progenitor' -Name 'FenceNative' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "QueryDosDeviceW", SetLastError = true)]
public static extern uint QueryDosDevice(string lpDeviceName, System.Text.StringBuilder lpTargetPath, uint ucchMax);
'@ -ErrorAction Stop
    }
    $buffer = New-Object System.Text.StringBuilder 2048
    $written = [Progenitor.FenceNative]::QueryDosDevice($Letter + ':', $buffer, 2048)
    if ($written -eq 0) {
      # ERROR_FILE_NOT_FOUND is the one failure that means "no definition". Anything else --
      # ERROR_INSUFFICIENT_BUFFER, access denied -- is a question the fence could not ask.
      if ([System.Runtime.InteropServices.Marshal]::GetLastWin32Error() -eq 2) { return $null }
      return $ProgenitorFenceDosDeviceUnavailable
    }
    $target = $buffer.ToString()
    if ([string]::IsNullOrWhiteSpace($target)) { return $null }
    return $target
  } catch { return $ProgenitorFenceDosDeviceUnavailable }
}

function Get-FenceDriveDisplayRoot {
  <#
    The UNC a mapped letter displays, straight from the PowerShell drive table. This is the
    documented spelling of a `net use` mapping; the LanmanRedirector device string is the fallback.
  #>
  param([Parameter(Mandatory = $true)][string]$Letter)

  if ($ProgenitorFenceDisplayRootLookup) {
    $v = & $ProgenitorFenceDisplayRootLookup $Letter
    if ($null -eq $v -or $v -eq '') { return $null }
    return [string]$v
  }

  try {
    $d = Get-PSDrive -Name $Letter -PSProvider FileSystem -ErrorAction Stop
    if ($d -and -not [string]::IsNullOrWhiteSpace($d.DisplayRoot)) { return [string]$d.DisplayRoot }
  } catch { }
  return $null
}

function Get-FenceLocalSharePath {
  <#
    The local directory a share of THIS machine publishes. Get-SmbShare first; `net share <name>`
    when the SMB module is absent. $null when neither can name it -- which the caller turns into a
    refusal, because an unresolvable local share is exactly the case this rule exists for.
  #>
  param([Parameter(Mandatory = $true)][string]$Share)

  if ($ProgenitorFenceShareLookup) {
    $v = & $ProgenitorFenceShareLookup $Share
    if ($null -eq $v -or $v -eq '') { return $null }
    return [string]$v
  }

  try {
    $s = Get-SmbShare -Name $Share -ErrorAction Stop
    if ($s) {
      $p = @($s)[0].Path
      if (-not [string]::IsNullOrWhiteSpace($p)) { return [string]$p }
    }
  } catch { }

  try {
    $lines = & net.exe share $Share 2>$null
    foreach ($line in @($lines)) {
      $m = [regex]::Match([string]$line, '^\s*Path\s+(?<p>\S.*?)\s*$')
      if ($m.Success) { return $m.Groups['p'].Value }
    }
  } catch { }
  return $null
}

function Resolve-FenceMappedPath {
  <#
    One step of DEFINITION resolution (R42 finding 8). Returns:

      the same path   there is no indirection to follow (a real local volume, a remote share, or a
                      drive letter with no definition at all)
      another path    the letter or share is defined in terms of that one; the caller re-canonicalizes
      the sentinel    the definition could not be read and the letter or share is not plainly local

    Takes a path already through ConvertTo-FenceNormalizedPath, so it is `<drive>:\...` or
    `\\server\share\...` and nothing else.
  #>
  param([Parameter(Mandatory = $true)][string]$Path)

  $m = [regex]::Match($Path, '^(?<letter>[A-Za-z]):(?<rest>\\.*)?$')
  if ($m.Success) {
    $letter = $m.Groups['letter'].Value
    $rest = $m.Groups['rest'].Value
    $target = Get-FenceDosDeviceTarget $letter

    if ($target -eq $ProgenitorFenceDosDeviceUnavailable) {
      # The lookup could not be made (R43). DriveType cannot settle it -- a subst reports Fixed --
      # so every letter that exists is refused, except the system drive: the boot volume cannot be
      # a subst. A letter that does not exist still names nothing and is left alone.
      $sys = [string]$env:SystemDrive
      if ($sys -match '^[A-Za-z]:$' -and [string]::Equals($sys.Substring(0, 1), $letter, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path
      }
      $type = $null
      try { $type = (New-Object System.IO.DriveInfo($letter + ':')).DriveType } catch { $type = $null }
      if ($type -eq [System.IO.DriveType]::NoRootDirectory) { return $Path }
      return $ProgenitorFenceUnrecognisedPath
    }

    if ($target) {
      # subst over a UNC path, and subst over a drive path -- the '\??\' object-manager prefix.
      $u = [regex]::Match($target, '^\\\?\?\\UNC\\(?<unc>.+)$')
      if ($u.Success) { return ('\\' + $u.Groups['unc'].Value.TrimEnd('\') + $rest) }
      $s = [regex]::Match($target, '^\\\?\?\\(?<sub>[A-Za-z]:.*)$')
      if ($s.Success) { return ($s.Groups['sub'].Value.TrimEnd('\') + $rest) }

      # A mapped letter. DisplayRoot states the UNC; the device string carries it too, after the
      # ';Z:0000000000000000' session segment.
      if ($target -match '^\\Device\\(LanmanRedirector|Mup)(\\|$)') {
        $display = Get-FenceDriveDisplayRoot $letter
        if ($display) { return ($display.TrimEnd('\') + $rest) }
        $n = [regex]::Match($target, '^\\Device\\(?:LanmanRedirector|Mup)\\(?:;[^\\]*\\)?(?<unc>[^\\]+\\.+)$')
        if ($n.Success) { return ('\\' + $n.Groups['unc'].Value.TrimEnd('\') + $rest) }
        return $ProgenitorFenceUnrecognisedPath
      }

      # A real local volume: \Device\HarddiskVolume3, \Device\CdRom0, and friends. No indirection.
      if ($target -match '^\\Device\\(HarddiskVolume|Harddisk|Volume|CdRom|Floppy|RamDisk)') { return $Path }

      # Something else entirely in the object manager -- a filter driver's own volume name, a
      # device this fence has never seen. It may or may not be an indirection; the fence does not
      # guess, and its failure mode is refusal.
      return $ProgenitorFenceUnrecognisedPath
    }

    # No definition readable. A letter that does not exist names nothing and cannot be written
    # through, so it is left alone; a letter that DOES exist and whose definition we could not read
    # is refused unless it is plainly a fixed local disk.
    $type = $null
    try { $type = (New-Object System.IO.DriveInfo($letter + ':')).DriveType } catch { $type = $null }
    if ($null -eq $type) { return $Path }
    if ($type -eq [System.IO.DriveType]::NoRootDirectory) { return $Path }
    if ($type -eq [System.IO.DriveType]::Fixed) { return $Path }
    return $ProgenitorFenceUnrecognisedPath
  }

  $u = [regex]::Match($Path, '^\\\\(?<server>[^\\]+)\\(?<share>[^\\]+)(?<rest>\\.*)?$')
  if ($u.Success) {
    $server = $u.Groups['server'].Value
    $localNames = @('localhost', '.', '127.0.0.1', '::1', $env:COMPUTERNAME) | Where-Object { $_ }
    $isLocal = $false
    foreach ($n in $localNames) {
      if ([string]::Equals($server, $n, [System.StringComparison]::OrdinalIgnoreCase)) { $isLocal = $true; break }
    }
    # A share on another host cannot be this machine's live server, and there is no local table to
    # ask about it. Left alone, as it was before -- \\fileserver\share is a legitimate root.
    if (-not $isLocal) { return $Path }

    $sharePath = Get-FenceLocalSharePath $u.Groups['share'].Value
    if (-not $sharePath) { return $ProgenitorFenceUnrecognisedPath }
    return ($sharePath.TrimEnd('\') + $u.Groups['rest'].Value)
  }

  return $Path
}

function Test-FenceShortNameForm {
  <#
    True when a segment has the SHAPE Windows gives an 8.3 alias: up to six characters, a tilde,
    a digit or two, and at most a three-character extension -- VRISIN~1, PROGRA~1, RUNNE~1.LOC.
    It says nothing about what the segment resolves to; ConvertTo-FenceCanonicalPath asks the
    filesystem that. A long name that merely contains a tilde (`my~notes`, `archive~2026`) is not
    this shape and keeps its best-effort treatment.
  #>
  param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Segment)

  if ([string]::IsNullOrEmpty($Segment)) { return $false }
  $name = $Segment
  $ext = ''
  $dot = $Segment.LastIndexOf('.')
  if ($dot -ge 0) {
    $name = $Segment.Substring(0, $dot)
    $ext = $Segment.Substring($dot + 1)
  }

  if ($name.Length -gt 8) { return $false }
  if ($ext.Length -gt 0 -and $ext -notmatch '^[^.\\/:*?"<>|\s]{1,3}$') { return $false }
  return ($name -match '^[^.\\/:*?"<>|\s]{1,6}~[0-9]{1,6}$')
}

function ConvertTo-FenceCanonicalPath {
  <#
    Everything ConvertTo-FenceNormalizedPath does, plus the two forms that can only be
    settled against the filesystem, resolved segment by segment from the root down for
    whatever part of the path exists:

      8.3 short names  C:\VRISIN~1 -> C:\VRisingServer. Measured on both hosts: the expansion of
                       an alias that RESOLVES is GetFullPath's, above, and it has already
                       happened by the time this loop runs. What GetFileSystemEntries adds here
                       is a literal-name lookup -- on .NET Framework (Windows PowerShell 5.1) it
                       also matches short names, on .NET it does not -- so the arm below settles a
                       directory really NAMED `ab~1`, not the alias case. A segment of alias
                       SHAPE which the filesystem does not resolve to exactly one entry is REFUSED
                       (the unrecognised-form verdict) rather than kept as a literal name.
                       Found by the CI baseline, where C:\VRisingServer does not exist: the
                       pass was best-effort, so `C:\VRISIN~1` stayed `C:\VRISIN~1`, compared
                       as a name of its own and walked straight through the fence. An alias
                       nobody can resolve is the fence's own "I cannot tell where this points"
                       case -- and it is the one spelling whose meaning can CHANGE under the
                       fence, because the alias starts resolving the moment the directory it
                       abbreviates is created.
      reparse points   a junction or symlink is checked at its TARGET, so _testserver aimed at
                       the live server is refused. Followed for ancestors, not just the leaf,
                       because the roots these scripts pass are mostly derived paths whose leaf
                       does not exist yet.

    A path that does not exist normalizes and stops there -- there is nothing to resolve, and
    the fence must still hold for a folder the run is about to create. The ONE exception is the
    alias shape above: a segment shaped like an 8.3 alias that answers to nothing is refused
    rather than taken literally, so a folder a run means to CREATE cannot be named `run~1` and
    still pass the fence. That is the intended trade. The fence cannot tell an alias from a name
    by looking, an alias is the one spelling whose meaning arrives later, and a long name costs
    the caller nothing; the diagnostic names this case so the answer is to spell it differently.

    Before either of those, the drive letter or local share is resolved through its DEFINITION
    (R42 finding 8, Resolve-FenceMappedPath), and the redirected path is fed back through this
    function from the top so a chain of mappings resolves and so 8.3 and reparse resolution then
    run on the REAL path rather than on the mapping's spelling.
  #>
  param(
    [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Path,
    [int]$Depth = 0
  )

  $full = ConvertTo-FenceNormalizedPath $Path
  # An unrecognised form is a verdict, not a path: propagate it untouched, resolve nothing.
  if ($full -eq $ProgenitorFenceUnrecognisedPath) { return $ProgenitorFenceUnrecognisedPath }

  # Drive-letter and local-share definitions, before anything is compared or touched on disk.
  $mapped = Resolve-FenceMappedPath -Path $full
  if ($mapped -eq $ProgenitorFenceUnrecognisedPath) { return $ProgenitorFenceUnrecognisedPath }
  if (-not [string]::Equals($mapped, $full, [System.StringComparison]::OrdinalIgnoreCase)) {
    # A mapping cycle (S: substed onto a path under S:) is a form the fence cannot canonicalize,
    # so it takes the refusing verdict rather than the last path it happened to be holding.
    if ($Depth -ge 16) { return $ProgenitorFenceUnrecognisedPath }
    return ConvertTo-FenceCanonicalPath -Path $mapped -Depth ($Depth + 1)
  }

  if ($Depth -gt 16) { return $full }   # cyclic junctions: stop, keep what we have

  $root = ""
  try { $root = [System.IO.Path]::GetPathRoot($full) } catch { }
  if ([string]::IsNullOrEmpty($root)) { return $full }

  $rest = $full.Substring($root.Length)
  $segments = $rest.Split([char]'\', [System.StringSplitOptions]::RemoveEmptyEntries)
  $current = $root

  foreach ($seg in $segments) {
    $parent = $current.TrimEnd('\')
    if ($parent -match '^[A-Za-z]:$') { $parent = $parent + '\' }
    $candidate = [System.IO.Path]::Combine($parent, $seg)

    if ($seg.Contains('~')) {
      $hits = @()
      try { $hits = @([System.IO.Directory]::GetFileSystemEntries($parent, $seg)) } catch { $hits = @() }
      if ($hits.Count -eq 1) {
        $candidate = $hits[0]
      } elseif (Test-FenceShortNameForm $seg) {
        # Nothing (or more than one thing) answers to this alias here, and its shape says it is
        # an alias rather than a name. Refuse: comparing the literal `VRISIN~1` against
        # `C:\VRisingServer` answers "somewhere else" for a spelling that reaches the live tree
        # on any machine where that directory exists.
        return $ProgenitorFenceUnrecognisedPath
      }
    }

    $item = $null
    try { $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop } catch { $item = $null }
    if ($item -and (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
      $target = $null
      try { $target = $item.Target } catch { $target = $null }
      if ($target -is [System.Array]) { $target = @($target)[0] }
      if ($target) {
        if (-not [System.IO.Path]::IsPathRooted($target)) {
          $target = [System.IO.Path]::Combine($parent, [string]$target)
        }
        $resolved = ConvertTo-FenceCanonicalPath -Path ([string]$target) -Depth ($Depth + 1)
        # The verdict propagates out of the recursion the same way it propagates out of the
        # normalizer: a link whose TARGET the fence cannot place makes the whole path unplaceable.
        # Combining the sentinel with the remaining segments would build a path-shaped string that
        # is under no root at all, which is the one answer this verdict must never turn into.
        if ($resolved -eq $ProgenitorFenceUnrecognisedPath) { return $ProgenitorFenceUnrecognisedPath }
        $candidate = $resolved
      }
    }
    $current = $candidate
  }

  return $current
}

function Test-PathUnderRoot {
  <#
    True when $Path IS $Root or sits inside it. Both sides are canonicalized first, trailing
    separators are trimmed, and the containment test is on a '\'-terminated boundary so a
    sibling whose name merely starts with the root's name (C:\VRisingServerBackup) is NOT
    under it. OrdinalIgnoreCase: Windows paths are case-insensitive.

    A $Path whose form the fence does not recognize is under EVERY root (R41 finding 6) -- the
    predicate's one job is to keep a write out of a forbidden tree, and "I cannot tell where this
    points" must answer the same way as "it points there". An unrecognised ROOT contains nothing;
    the roots are this file's own literals, so that case only arises from a caller's mistake.
  #>
  param(
    [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Path,
    [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Root
  )

  $pCanon = ConvertTo-FenceCanonicalPath $Path
  $rCanon = ConvertTo-FenceCanonicalPath $Root
  if ($pCanon -eq $ProgenitorFenceUnrecognisedPath) { return $true }
  if ($rCanon -eq $ProgenitorFenceUnrecognisedPath) { return $false }

  $p = $pCanon.TrimEnd('\')
  $r = $rCanon.TrimEnd('\')
  if ($p -eq "" -or $r -eq "") { return $false }
  if ($p.Equals($r, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
  return $p.StartsWith($r + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NotForbiddenPath {
  <#
    Throw if $Path is, or is inside, any of $ForbiddenRoots. Call it on every root a script
    will touch -- the parameters AND the paths derived from them -- before the first write.
  #>
  param(
    [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Path,
    [Parameter(Mandatory = $true)][string[]]$ForbiddenRoots,
    [string]$Message = "refusing to touch a forbidden root"
  )

  # Form first, before any root is even looked at (R41 finding 6). A device, volume-GUID or
  # GLOBALROOT path has no canonical form this fence can compare, and the diagnostic for it must
  # name the SPELLING the caller typed -- printing a "canonical" path that quietly became relative
  # to the working directory was the worst part of the old behavior.
  $canonical = ConvertTo-FenceCanonicalPath $Path
  if ($canonical -eq $ProgenitorFenceUnrecognisedPath) {
    throw ("$Message : '$Path' is an unrecognised path form -- the fence cannot place it, so it " +
           "is refused without comparison. Either its FORM is not one of the two it accepts -- '<drive>:\...' and " +
           "'\\server\share\...'; device, volume-GUID (\\?\Volume{...}\), GLOBALROOT and other " +
           "non-drive, non-UNC spellings are not -- or it names something this machine cannot " +
           "resolve: an 8.3 alias (NAME~1) that answers to no directory here, or a junction " +
           "whose target is itself unplaceable.")
  }

  foreach ($root in $ForbiddenRoots) {
    if ($root -eq $ProgenitorFenceUnresolvedRoot) {
      throw ("$Message : '$Path' cannot be checked -- a forbidden root (the isolated test server " +
             "folder) could not be resolved, so every path is refused. Set PROGENITOR_WORKSPACE " +
             "to the folder that holds the main checkout, or PROGENITOR_TEST_ROOT.")
    }
  }
  foreach ($root in $ForbiddenRoots) {
    if (Test-PathUnderRoot -Path $Path -Root $root) {
      throw "$Message : '$Path' resolves to '$canonical', which is under '$root'"
    }
  }
}

function Assert-NotLiveServerPath {
  <#
    The C:\VRisingServer fence, for the three dev-loop scripts. Same function, fixed root list.
  #>
  param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Path)
  Assert-NotForbiddenPath -Path $Path -ForbiddenRoots $ProgenitorLiveServerRoots `
    -Message "refusing to touch the live server"
}

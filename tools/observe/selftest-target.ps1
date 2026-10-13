<#
.SYNOPSIS
  The self-test target for tools/observe.ps1. A WinForms window that writes down every real
  keyboard and mouse event Windows delivers to it.

.DESCRIPTION
  Started by `tools/observe.ps1 selftest`. Never run it by hand except to debug the self-test.

  This exists so the input path can be proved against something that is NOT the game. It is a
  real top-level window receiving real OS input, so a pass here means SendInput reached a
  foreground window through the ordinary Windows input queue -- the same queue V Rising reads.
  What it CANNOT prove is how a particular game reads that queue (raw input vs. virtual keys vs.
  DirectInput), which is why the tool sends a scan code alongside every virtual key.

  Each line of -LogPath is one fact, in the format the caller's regexes expect:

    READY <hwnd>              the window exists and is shown
    ACTIVATED                 the window became the active window (i.e. focus was taken)
    KEYDOWN vk=<n> ctrl=<b> alt=<b> shift=<b>
    CHAR <one character>      a WM_CHAR, which is what KEYEVENTF_UNICODE produces
    MOUSEDOWN <Button> <x>,<y>    client coordinates of the form
    WHEEL <delta>             120 per notch, negative = toward the user

  Writes are flushed per line: the caller reads the file while this process is still alive.
#>
param(
  [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = "Stop"

# Match the caller's DPI awareness before creating any window, or on a scaled display the
# client coordinates this window reports would be logical units while the caller sends physical
# ones, and the click assertion would fail for a reason that has nothing to do with SendInput.
try {
  Add-Type -Namespace SelfTest -Name Dpi -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr SetThreadDpiAwarenessContext(System.IntPtr ctx);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();
'@ | Out-Null
  if ([SelfTest.Dpi]::SetThreadDpiAwarenessContext([IntPtr](-4)) -eq [IntPtr]::Zero) {
    [void][SelfTest.Dpi]::SetProcessDPIAware()
  }
} catch {
  try { [void][SelfTest.Dpi]::SetProcessDPIAware() } catch {}
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:Writer = [System.IO.StreamWriter]::new($LogPath, $false, [System.Text.UTF8Encoding]::new($false))
$script:Writer.AutoFlush = $true

function Log { param([string]$Line) $script:Writer.WriteLine($Line) }

trap {
  try { Log ("ERROR " + $_.Exception.Message) } catch {}
  try { $script:Writer.Flush() } catch {}
  exit 1
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "Progenitor observe self-test"
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(60, 60)
$form.ClientSize = New-Object System.Drawing.Size(560, 420)
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.TopMost = $true
$form.KeyPreview = $true
$form.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 32)

# The screenshot assertion checks that the capture is not near-uniform, so the window has to
# contain something. These blocks are that something: a solid gray form would fail the blank
# check for a reason that has nothing to do with the capture path.
$palette = @(
  [System.Drawing.Color]::FromArgb(220, 60, 60),
  [System.Drawing.Color]::FromArgb(60, 200, 120),
  [System.Drawing.Color]::FromArgb(80, 140, 240),
  [System.Drawing.Color]::FromArgb(240, 200, 60),
  [System.Drawing.Color]::FromArgb(200, 90, 220),
  [System.Drawing.Color]::FromArgb(240, 240, 240)
)
for ($i = 0; $i -lt $palette.Count; $i++) {
  $p = New-Object System.Windows.Forms.Panel
  $p.BackColor = $palette[$i]
  $p.Size = New-Object System.Drawing.Size(80, 60)
  $p.Location = New-Object System.Drawing.Point((20 + $i * 88), 130)
  # Clicks must reach the FORM (the caller asserts form-client coordinates), so keep the
  # color blocks out of the click target area at y=300.
  $form.Controls.Add($p)
}

$label = New-Object System.Windows.Forms.Label
$label.Text = "Progenitor observe.ps1 self-test target." + [Environment]::NewLine +
              "Every real key press, click and wheel tick this window receives is written to:" +
              [Environment]::NewLine + $LogPath
$label.Location = New-Object System.Drawing.Point(20, 210)
$label.Size = New-Object System.Drawing.Size(520, 70)
$label.ForeColor = [System.Drawing.Color]::White
$form.Controls.Add($label)

$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true
$box.Location = New-Object System.Drawing.Point(20, 20)
$box.Size = New-Object System.Drawing.Size(520, 100)
$box.Font = New-Object System.Drawing.Font("Consolas", 11)
$form.Controls.Add($box)

$onKeyDown = {
  param($sender, $e)
  Log ("KEYDOWN vk={0} ctrl={1} alt={2} shift={3}" -f [int]$e.KeyCode, $e.Control, $e.Alt, $e.Shift)
}
$onKeyPress = {
  param($sender, $e)
  Log ("CHAR " + $e.KeyChar)
}
$onMouseDown = {
  param($sender, $e)
  Log ("MOUSEDOWN {0} {1},{2}" -f $e.Button, $e.X, $e.Y)
}
$onWheel = {
  param($sender, $e)
  Log ("WHEEL " + $e.Delta)
}

$form.Add_KeyDown($onKeyDown)
$form.Add_KeyPress($onKeyPress)
$form.Add_MouseDown($onMouseDown)
$form.Add_MouseWheel($onWheel)
# The wheel goes to whatever control has focus, so the text box needs the same handler or a
# scroll before the click would be logged by nobody and read as a SendInput failure.
$box.Add_MouseWheel($onWheel)
$box.Add_MouseDown($onMouseDown)
$form.Add_Activated({ Log "ACTIVATED" })
$form.Add_Shown({
  Log ("READY " + $form.Handle)
})
$form.Add_FormClosed({
  try { $script:Writer.Flush(); $script:Writer.Dispose() } catch {}
})

[System.Windows.Forms.Application]::Run($form)

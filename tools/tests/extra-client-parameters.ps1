# Exercise the launcher's actual parameter binder without executing its body.
$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot '..\extra-client.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $launcher).Path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$probe = [ScriptBlock]::Create($ast.ParamBlock.Extent.Text + "`nreturn `$Index")
foreach ($index in @(1, 9, 10, 29, 99)) {
    $actual = & $probe -Index $index
    if ($actual -ne $index) { throw "Index $index was not accepted" }
}
foreach ($index in @(-1, 0, 100, [int]::MaxValue)) {
    $refused = $false
    try { $null = & $probe -Index $index }
    catch [System.Management.Automation.ParameterBindingException] { $refused = $true }
    if (-not $refused) { throw "Invalid index $index was accepted" }
}
Write-Output 'Extra-client parameter checks passed (5 accepted, 4 refused); no clients launched.'

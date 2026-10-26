[CmdletBinding()]
param(
    [string]$Path = 'tests/spike'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $repoRoot '.tools\psmodules\Pester\5.7.1\Pester.psd1'

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Pester is not bootstrapped. Run scripts/bootstrap-spike-tests.ps1 first.'
}

Import-Module $manifestPath -Force

$testPath = if ([System.IO.Path]::IsPathRooted($Path)) {
    $Path
} else {
    Join-Path $repoRoot $Path
}

$result = Invoke-Pester -Path $testPath -Output Detailed -PassThru
if ($result.FailedCount -gt 0 -or $result.FailedContainersCount -gt 0) {
    exit 1
}

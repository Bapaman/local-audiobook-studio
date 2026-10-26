[CmdletBinding()]
param(
    [string]$ToolsRoot = (Join-Path $PSScriptRoot '..\.tools')
)

$ErrorActionPreference = 'Stop'
$pesterVersion = '5.7.1'
$moduleRoot = Join-Path $ToolsRoot 'psmodules'
$manifestPath = Join-Path $moduleRoot "Pester\$pesterVersion\Pester.psd1"

if (-not (Test-Path -LiteralPath $manifestPath)) {
    New-Item -ItemType Directory -Force -Path $moduleRoot | Out-Null
    Save-Module `
        -Name Pester `
        -RequiredVersion $pesterVersion `
        -Repository PSGallery `
        -Path $moduleRoot `
        -Force
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Pester $pesterVersion was not saved to $manifestPath."
}

Write-Output $manifestPath

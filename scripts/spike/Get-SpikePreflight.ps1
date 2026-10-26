[CmdletBinding()]
param(
    [string]$EnvironmentPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$modulePath = Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1'
Import-Module $modulePath -Force

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot '.artifacts\preflight.json'
}

$environment = if ([string]::IsNullOrWhiteSpace($EnvironmentPath)) {
    Get-SpikeEnvironment
} else {
    Get-Content -Raw -LiteralPath $EnvironmentPath | ConvertFrom-Json
}

$issues = @(Test-SpikeEnvironment $environment)
$report = [pscustomobject]@{
    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Environment   = $environment
    Issues        = $issues
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$partialPath = "$OutputPath.part"
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $partialPath -Encoding utf8NoBOM
Move-Item -Force -LiteralPath $partialPath -Destination $OutputPath

if ($issues.Count -eq 0) {
    Write-Output 'Spike environment is ready.'
    exit 0
}

$issues | Format-Table Code, Severity, Message, Remediation -AutoSize | Out-Host
exit 1

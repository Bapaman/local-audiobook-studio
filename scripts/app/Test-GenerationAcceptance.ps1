[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testProject = Join-Path $repositoryRoot 'tests\LocalAudiobookStudio.App.Tests\LocalAudiobookStudio.App.Tests.csproj'

# This default gate starts only the actual App in a newly owned synthetic root.
# It tests both unconfigured and explicit-but-missing CPU configuration. No model,
# user recording, default catalog, native inference or GPU training is selected.
Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet test --project $testProject -c $Configuration -p:RestoreLockedMode=true -p:NuGetAudit=false --no-restore -- --filter-class '*WinExeAcceptanceTests'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output 'Actual App: launch/auth/project snapshot/readiness/Exit gate passed.'
    Write-Output 'Real CPU audio: NOT RUN. Real GPU/training/listening quality: NOT RUN.'
    Write-Output 'Production browser generation and the whole solution remain separate gates.'
} finally { Pop-Location }
exit 0

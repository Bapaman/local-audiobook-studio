[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solution = Join-Path $repositoryRoot 'LocalAudiobookStudio.sln'
if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
    throw "Solution not found: $solution"
}

$testExitCode = 0
Push-Location -LiteralPath $repositoryRoot
try {
    & dotnet restore $solution --locked-mode "-p:Configuration=$Configuration"
    if ($LASTEXITCODE -ne 0) {
        $testExitCode = $LASTEXITCODE
    } else {
        & dotnet test --solution $solution -c $Configuration --no-restore
        $testExitCode = $LASTEXITCODE
        if ($testExitCode -eq 0) {
            $client = Join-Path $repositoryRoot 'src\LocalAudiobookStudio.App\ClientApp'
            & npm --prefix $client ci
            $testExitCode = $LASTEXITCODE
            if ($testExitCode -eq 0) { & npm --prefix $client run test; $testExitCode = $LASTEXITCODE }
            if ($testExitCode -eq 0) { & npm --prefix $client run build; $testExitCode = $LASTEXITCODE }
            if ($testExitCode -eq 0) { & npm --prefix $client run test:e2e; $testExitCode = $LASTEXITCODE }
        }
    }
} finally {
    Pop-Location
}
exit $testExitCode

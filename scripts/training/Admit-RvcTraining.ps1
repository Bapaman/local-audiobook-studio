#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$DatasetRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ManifestSha256,
    [Parameter(Mandatory)][string]$PreflightRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$PreflightSha256,
    [Parameter(Mandatory)][string]$CorpusRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$CorpusSha256,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$BaseAssetRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [switch]$Experimental
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$ownedOutput = (Resolve-Path -LiteralPath $OutputRoot).Path
if (@(Get-ChildItem -LiteralPath $ownedOutput -Force).Count -ne 0) { throw 'training.admission.output_not_empty' }
$validationRoot = Join-Path $ownedOutput 'validation'
$acceptanceRoot = Join-Path $ownedOutput 'acceptance'
$null = New-Item -ItemType Directory -Path $validationRoot
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Resolve-Path -LiteralPath $PythonPath).Path
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $projectRoot
foreach ($item in @('-I', (Join-Path $PSScriptRoot 'Admit-RvcTraining.py'),
    '--dataset-root', (Resolve-Path -LiteralPath $DatasetRoot).Path, '--manifest-sha256', $ManifestSha256,
    '--preflight-root', (Resolve-Path -LiteralPath $PreflightRoot).Path, '--preflight-sha256', $PreflightSha256,
    '--corpus-root', (Resolve-Path -LiteralPath $CorpusRoot).Path, '--corpus-sha256', $CorpusSha256,
    '--source-root', (Resolve-Path -LiteralPath $SourceRoot).Path,
    '--base-asset-root', (Resolve-Path -LiteralPath $BaseAssetRoot).Path,
    '--output-root', $validationRoot)) { $start.ArgumentList.Add($item) }
if ($Experimental) { $start.ArgumentList.Add('--experimental') }
foreach ($name in @('OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS', 'NUMBA_NUM_THREADS')) {
    $start.EnvironmentVariables[$name] = '2'
}
$start.EnvironmentVariables['CUDA_VISIBLE_DEVICES'] = ''
$start.EnvironmentVariables['RVC_CUDA_GRAPH'] = '0'
$start.EnvironmentVariables['HF_HUB_OFFLINE'] = '1'
$start.EnvironmentVariables['TRANSFORMERS_OFFLINE'] = '1'
$start.EnvironmentVariables.Remove('LIBROSA_CACHE_DIR')
$child = [Diagnostics.Process]::new()
$child.StartInfo = $start
try {
    if (-not $child.Start()) { throw 'training.admission.start_failed' }
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(60000)) {
        $child.Kill($true)
        if (-not $child.WaitForExit(5000)) { throw 'training.admission.cleanup_unconfirmed' }
        throw 'training.admission.timeout: no automatic timeout/load increase'
    }
    $controlText = $stdout.GetAwaiter().GetResult()
    $null = $stderr.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw 'training.admission.failed' }
    $control = $controlText.Trim() | ConvertFrom-Json
    if ($control.phase -ne 'experimental_training_candidate_saved' -or $control.training_admitted -ne $false -or
        $control.candidate_sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'training.admission.control_invalid' }
} finally {
    $child.Dispose()
}
# Native validation already exited successfully inside its fixed60s deadline.
# This separate cheap metadata phase never imports Torch or constructs models.
$null = New-Item -ItemType Directory -Path $acceptanceRoot
& $start.FileName -I (Join-Path $PSScriptRoot 'Finalize-RvcAdmission.py') `
    --candidate-root (Join-Path $validationRoot 'candidate') --candidate-sha256 $control.candidate_sha256 `
    --output-root $acceptanceRoot --worker-exit-code 0 --worker-deadline-ms 60000
if ($LASTEXITCODE -ne 0) { throw 'training.admission.acceptance_failed' }

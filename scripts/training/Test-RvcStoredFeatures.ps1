#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$DatasetRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ManifestSha256,
    [Parameter(Mandatory)][string]$FeatureRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$FeatureSha256,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][ValidateCount(1,3)][ValidatePattern('^clip-[0-9]{6}$')][string[]]$ClipIds
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Resolve-Path -LiteralPath $PythonPath).Path
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $projectRoot
foreach ($item in @('-I', (Join-Path $PSScriptRoot 'Manage-RvcCorpus.py'),
    '--dataset-root', (Resolve-Path -LiteralPath $DatasetRoot).Path, '--manifest-sha256', $ManifestSha256,
    'validate-stored', '--shard', (Resolve-Path -LiteralPath $FeatureRoot).Path, $FeatureSha256,
    '--source-root', (Resolve-Path -LiteralPath $SourceRoot).Path)) { $start.ArgumentList.Add($item) }
foreach ($clipId in $ClipIds) {
    $start.ArgumentList.Add('--clip-id')
    $start.ArgumentList.Add($clipId)
}
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
    if (-not $child.Start()) { throw 'features.stored.start_failed' }
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(60000)) {
        $child.Kill($true)
        if (-not $child.WaitForExit(5000)) { throw 'features.stored.cleanup_unconfirmed' }
        throw 'features.stored.timeout: no automatic timeout/load increase'
    }
    Write-Output $stdout.GetAwaiter().GetResult()
    $null = $stderr.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw 'features.stored.failed' }
} finally {
    $child.Dispose()
}

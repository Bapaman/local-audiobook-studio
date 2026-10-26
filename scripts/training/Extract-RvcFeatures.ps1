#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$DatasetRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ManifestSha256,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$AssetRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][ValidateCount(1,3)][ValidatePattern('^clip-[0-9]{6}$')][string[]]$ClipIds,
    [switch]$VerifyNativeBatches
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resolvedPython = (Resolve-Path -LiteralPath $PythonPath).Path
$resolvedDataset = (Resolve-Path -LiteralPath $DatasetRoot).Path
$resolvedSource = (Resolve-Path -LiteralPath $SourceRoot).Path
$resolvedAssets = (Resolve-Path -LiteralPath $AssetRoot).Path
$resolvedOutput = (Resolve-Path -LiteralPath $OutputRoot).Path
# A new explicit cache for this invocation, never search/clear old caches.
$cacheParent = Join-Path $projectRoot '.artifacts/rf'
New-Item -ItemType Directory -Path $cacheParent -Force | Out-Null
$ownedCache = Join-Path $cacheParent ([Guid]::NewGuid().ToString('N').Substring(0,16))
# Numba appends a source hash, function filename and16-character temp suffix.
# Keep its path below legacy MAX_PATH without registry/power/driver changes.
if ($ownedCache.Length -gt 128) { throw 'features.preview.cache_path_too_long' }
New-Item -ItemType Directory -Path $ownedCache | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $resolvedPython
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $projectRoot
foreach ($item in @('-I', (Join-Path $PSScriptRoot 'Extract-RvcFeatures.py'),
    '--dataset-root', $resolvedDataset, '--manifest-sha256', $ManifestSha256,
    '--source-root', $resolvedSource, '--asset-root', $resolvedAssets,
    '--output-root', $resolvedOutput)) { $start.ArgumentList.Add($item) }
foreach ($clipId in $ClipIds) {
    $start.ArgumentList.Add('--clip-id')
    $start.ArgumentList.Add($clipId)
}
if ($VerifyNativeBatches) { $start.ArgumentList.Add('--verify-native-batches') }
foreach ($name in @('OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS', 'NUMBA_NUM_THREADS')) {
    $start.EnvironmentVariables[$name] = '2'
}
$start.EnvironmentVariables['CUDA_VISIBLE_DEVICES'] = ''
$start.EnvironmentVariables['RVC_CUDA_GRAPH'] = '0'
$start.EnvironmentVariables['NUMBA_CACHE_DIR'] = $ownedCache
$start.EnvironmentVariables['HF_HOME'] = Join-Path $ownedCache 'hf'
$start.EnvironmentVariables['HF_HUB_OFFLINE'] = '1'
$start.EnvironmentVariables['TRANSFORMERS_OFFLINE'] = '1'
$start.EnvironmentVariables.Remove('LIBROSA_CACHE_DIR')
$child = [Diagnostics.Process]::new()
$child.StartInfo = $start
try {
    if (-not $child.Start()) { throw 'features.preview.start_failed' }
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(60000)) {
        $child.Kill($true) # Only the exact child started here and its descendants.
        if (-not $child.WaitForExit(5000)) { throw 'features.preview.cleanup_unconfirmed' }
        throw 'features.preview.timeout: no automatic timeout/load increase'
    }
    Write-Output $stdout.GetAwaiter().GetResult()
    # Native stderr is drained to avoid deadlock; private paths are not echoed.
    $null = $stderr.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw 'features.preview.failed' }
} finally {
    $child.Dispose()
}

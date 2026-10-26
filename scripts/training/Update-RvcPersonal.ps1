#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$RequestPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$RequestSha256,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resolvedOutput = (Resolve-Path -LiteralPath $OutputRoot).Path
if (@(Get-ChildItem -LiteralPath $resolvedOutput -Force).Count -ne 0) { throw 'training.personal.output_not_empty' }
$ownedCache = Join-Path $projectRoot ('.artifacts/rf/' + [Guid]::NewGuid().ToString('N').Substring(0,16))
if ($ownedCache.Length -gt 128 -or (Test-Path -LiteralPath $ownedCache)) { throw 'training.personal.cache_invalid' }
$null = New-Item -ItemType Directory -Path $ownedCache
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Resolve-Path -LiteralPath $PythonPath).Path
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $projectRoot
foreach ($item in @('-I', (Join-Path $PSScriptRoot 'Update-RvcPersonal.py'),
    '--request', (Resolve-Path -LiteralPath $RequestPath).Path, '--request-sha256', $RequestSha256,
    '--output-root', $resolvedOutput)) { $start.ArgumentList.Add($item) }
foreach ($name in @('OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS', 'NUMBA_NUM_THREADS')) {
    $start.EnvironmentVariables[$name] = '2'
}
$start.EnvironmentVariables['CUDA_VISIBLE_DEVICES'] = ''
$start.EnvironmentVariables['RVC_CUDA_GRAPH'] = '0'
$start.EnvironmentVariables['HF_HUB_OFFLINE'] = '1'
$start.EnvironmentVariables['TRANSFORMERS_OFFLINE'] = '1'
$start.EnvironmentVariables['NUMBA_CACHE_DIR'] = $ownedCache
$start.EnvironmentVariables.Remove('LIBROSA_CACHE_DIR')
$child = [Diagnostics.Process]::new()
$child.StartInfo = $start
try {
    if (-not $child.Start()) { throw 'training.personal.start_failed' }
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(60000)) {
        $child.Kill($true)
        if (-not $child.WaitForExit(5000)) { throw 'training.personal.cleanup_unconfirmed' }
        $safeLines = $stdout.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { $_.Trim() }
        foreach ($line in $safeLines) {
            $event = $line | ConvertFrom-Json
            if ($event.phase -ne 'personal_checkpoint_control') { Write-Output $line }
        }
        $null = $stderr.GetAwaiter().GetResult()
        throw 'training.personal.timeout: unaccepted, no automatic deadline/load increase'
    }
    $lines = $stdout.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { $_.Trim() }
    $controls = @()
    foreach ($line in $lines) {
        $event = $line | ConvertFrom-Json
        if ($event.phase -eq 'personal_checkpoint_control') { $controls += $line }
        else { Write-Output $line }
    }
    $null = $stderr.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw 'training.personal.failed: previous complete point unchanged' }
    if ($controls.Count -ne 1) { throw 'training.personal.control_missing' }
} finally {
    $child.Dispose()
}
# Accept only the timely successful child and its returned manifest identity.
# This separate phase performs bounded metadata reads, never native ML imports.
$controls[0] | & $start.FileName -I (Join-Path $PSScriptRoot 'Accept-RvcUpdate.py') `
    --output-root $resolvedOutput --worker-exit-code 0 --worker-deadline-ms 60000 `
    --request (Resolve-Path -LiteralPath $RequestPath).Path --request-sha256 $RequestSha256
if ($LASTEXITCODE -ne 0) { throw 'training.personal.acceptance_failed' }

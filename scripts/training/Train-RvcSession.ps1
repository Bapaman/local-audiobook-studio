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
$resolvedRequest = (Resolve-Path -LiteralPath $RequestPath).Path
if (@(Get-ChildItem -LiteralPath $resolvedOutput -Force).Count -ne 0) { throw 'training.session.output_not_empty' }
$ownedCache = Join-Path $projectRoot ('.artifacts/rf/' + [Guid]::NewGuid().ToString('N').Substring(0,16))
if ($ownedCache.Length -gt 128 -or (Test-Path -LiteralPath $ownedCache)) { throw 'training.session.cache_invalid' }
$null = New-Item -ItemType Directory -Path $ownedCache
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Resolve-Path -LiteralPath $PythonPath).Path
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $projectRoot
foreach ($item in @('-I', (Join-Path $PSScriptRoot 'Train-RvcSession.py'), '--request', $resolvedRequest,
    '--request-sha256', $RequestSha256, '--output-root', $resolvedOutput)) { $start.ArgumentList.Add($item) }
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
$total = [Diagnostics.Stopwatch]::StartNew()
$idle = [Diagnostics.Stopwatch]::StartNew()
$mode = 'launch'
$batchCount = 0
$epochSize = 0
$step = 0
$saveCount = 0
$lastSaved = 0
$control = $null
try {
    if (-not $child.Start()) { throw 'training.session.start_failed' }
    $stderr = $child.StandardError.ReadToEndAsync()
    $lineTask = $child.StandardOutput.ReadLineAsync()
    while ($true) {
        if ($total.ElapsedMilliseconds -ge 3600000 -or $idle.ElapsedMilliseconds -ge 120000) {
            throw 'training.session.watchdog_timeout: candidates unaccepted, no automatic retry'
        }
        if (-not $lineTask.Wait(200)) { continue }
        $line = $lineTask.GetAwaiter().GetResult()
        if ($null -eq $line) { break }
        if ($line.Length -gt 16384 -or -not $line.Trim()) { throw 'training.session.progress_invalid' }
        $event = $line | ConvertFrom-Json -AsHashtable
        switch ($event.phase) {
            'session_preparing' {
                if ($mode -ne 'launch') { throw 'training.session.progress_invalid' }
                $mode = 'preparing'
            }
            'session_batch_ready' {
                if ($mode -ne 'preparing' -or $event.batch_count -isnot [long] -and $event.batch_count -isnot [int] -or
                    $event.batch_count -ne $batchCount + 1 -or $event.total_batches -lt 1 -or $event.total_batches -gt 2048 -or
                    $event.batch_count -gt $event.total_batches -or $epochSize -ne 0 -and $epochSize -ne $event.total_batches) {
                    throw 'training.session.progress_invalid'
                }
                $batchCount = $event.batch_count
                $epochSize = $event.total_batches
            }
            'session_state_ready' {
                if ($mode -ne 'preparing' -or $batchCount -ne $epochSize -or $epochSize -lt 1 -or
                    $event.epoch_size -ne $epochSize -or $event.global_step -lt 1) { throw 'training.session.progress_invalid' }
                $step = $event.global_step
                $mode = 'ready'
            }
            'session_update_started' {
                if ($mode -ne 'ready' -or $event.global_step -ne $step + 1) { throw 'training.session.progress_invalid' }
                $mode = 'updating'
            }
            'session_update_complete' {
                if ($mode -ne 'updating' -or $event.global_step -ne $step + 1 -or
                    $event.completed_epoch * $epochSize + $event.next_ordinal -ne $event.global_step) {
                    throw 'training.session.progress_invalid'
                }
                $step = $event.global_step
                $mode = 'ready'
            }
            'session_save_started' {
                if ($mode -ne 'ready' -or $event.global_step -ne $step -or $lastSaved -eq $step -or
                    $event.point_count -ne $saveCount + 1 -or $event.point_count -gt 4) { throw 'training.session.progress_invalid' }
                $mode = 'saving'
            }
            'session_save_complete' {
                if ($mode -ne 'saving' -or $event.global_step -ne $step -or $event.point_count -ne $saveCount + 1) {
                    throw 'training.session.progress_invalid'
                }
                $saveCount = $event.point_count
                $lastSaved = $step
                $mode = 'ready'
            }
            'personal_session_control' {
                if ($mode -ne 'ready' -or $null -ne $control -or $event.global_step -ne $step -or
                    $lastSaved -ne $step -or $event.point_count -ne $saveCount) { throw 'training.session.control_invalid' }
                $control = $line
                $mode = 'ended'
            }
            'failed' { Write-Output $line; throw 'training.session.native_failed' }
            default { throw 'training.session.progress_invalid' }
        }
        $idle.Restart() # Only a finite valid state transition renews the watchdog.
        if ($event.phase -ne 'personal_session_control') { Write-Output $line }
        $lineTask = $child.StandardOutput.ReadLineAsync()
    }
    if (-not $child.WaitForExit(5000) -or $total.ElapsedMilliseconds -ge 3600000) { throw 'training.session.exit_timeout' }
    $null = $stderr.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0 -or $null -eq $control -or $mode -ne 'ended') { throw 'training.session.unaccepted' }
} finally {
    if ($child.Id -and -not $child.HasExited) {
        $child.Kill($true)
        if (-not $child.WaitForExit(5000)) { throw 'training.session.cleanup_unconfirmed' }
    }
    $child.Dispose()
}
# The native worker is now confirmed exited. No Torch import in this phase.
$control | & $start.FileName -I (Join-Path $PSScriptRoot 'Accept-RvcSession.py') --request $resolvedRequest `
    --request-sha256 $RequestSha256 --output-root $resolvedOutput --worker-exit-code 0 --worker-deadline-ms 3600000
if ($LASTEXITCODE -ne 0) { throw 'training.session.acceptance_failed' }

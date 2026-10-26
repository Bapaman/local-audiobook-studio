[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$AssetRoot,
    [ValidateSet('Factory', 'All')][string]$Mode = 'All'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resolvedPython = (Resolve-Path -LiteralPath $PythonPath).Path
$resolvedSource = (Resolve-Path -LiteralPath $SourceRoot).Path
$resolvedAssets = (Resolve-Path -LiteralPath $AssetRoot).Path
$testPath = Join-Path $projectRoot 'workers/rvc/acceptance/test_rvc_kernel.py'
$ownedCache = Join-Path $projectRoot '.artifacts/rvc-kernel-tests/numba-cache'
New-Item -ItemType Directory -Path $ownedCache -Force | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $resolvedPython
# All paths are explicit, resolved and quoted; no shell executes these arguments.
$start.Arguments = '-I "' + $testPath + '"'
if ($Mode -eq 'Factory') {
    $start.Arguments += ' RvcKernelAcceptance.test_creates_real_cpu_pretrained_state RvcKernelAcceptance.test_tampered_asset_rejected_before_loading'
}
$start.WorkingDirectory = $projectRoot
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.EnvironmentVariables['LAS_RVC_SOURCE'] = $resolvedSource
$start.EnvironmentVariables['LAS_RVC_ASSETS'] = $resolvedAssets
$start.EnvironmentVariables['CUDA_VISIBLE_DEVICES'] = ''
$start.EnvironmentVariables['OMP_NUM_THREADS'] = '2'
$start.EnvironmentVariables['MKL_NUM_THREADS'] = '2'
$start.EnvironmentVariables['NUMBA_NUM_THREADS'] = '2'
$start.EnvironmentVariables['NUMBA_CACHE_DIR'] = $ownedCache
$start.EnvironmentVariables.Remove('LIBROSA_CACHE_DIR')
$child = [Diagnostics.Process]::new()
$child.StartInfo = $start
try {
    if (-not $child.Start()) { throw 'training.kernel.start_failed' }
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(60000)) {
        # Only the child created here and its descendants; never discover other PIDs.
        $child.Kill()
        $child.WaitForExit(5000) | Out-Null
        throw 'training.kernel.timeout: acceptance unaccepted; do not increase timeout automatically'
    }
    Write-Output $stdout.GetAwaiter().GetResult()
    Write-Output $stderr.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw 'training.kernel.acceptance_failed' }
} finally {
    $child.Dispose()
}

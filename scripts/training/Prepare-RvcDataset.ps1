#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$DecoderRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [switch]$Consent
)
$ErrorActionPreference = 'Stop'
if (-not $Consent) { throw 'recording.consent.required' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resolvedPython = (Resolve-Path -LiteralPath $PythonPath).Path
$resolvedSource = (Resolve-Path -LiteralPath $SourcePath).Path
$resolvedDecoder = (Resolve-Path -LiteralPath $DecoderRoot).Path
$resolvedOutput = (Resolve-Path -LiteralPath $OutputRoot).Path
$sourceDigest = (Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash.ToLowerInvariant()
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $resolvedPython
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $projectRoot
foreach ($item in @('-I', (Join-Path $PSScriptRoot 'Prepare-RvcDataset.py'),
    '--source', $resolvedSource, '--source-sha256', $sourceDigest,
    '--decoder-root', $resolvedDecoder, '--output-root', $resolvedOutput, '--consent')) {
    $start.ArgumentList.Add($item)
}
$child = [Diagnostics.Process]::new()
$child.StartInfo = $start
try {
    if (-not $child.Start()) { throw 'dataset.prepare.start_failed' }
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(60000)) {
        # Own child + its FFmpeg descendants only; never locate unrelated PIDs.
        $child.Kill($true)
        if (-not $child.WaitForExit(5000)) { throw 'dataset.prepare.cleanup_unconfirmed' }
        throw 'dataset.prepare.timeout'
    }
    Write-Output $stdout.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw 'dataset.prepare.failed' }
    $after = (Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceDigest -ne $after) { throw 'recording.source.changed' }
    Write-Output 'recording.source.unchanged'
} finally {
    $child.Dispose()
}

[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$TranscriptPath,
    [string]$DestinationPath,
    [string]$ManifestPath,
    [string]$PythonPath,
    [string]$FfmpegPath,
    [switch]$ConfirmRights
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$localData = Join-Path $repoRoot 'local-test-data'
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $localData 'reference-master.wav'
}
if ([string]::IsNullOrWhiteSpace($TranscriptPath)) {
    $TranscriptPath = Join-Path $localData 'reference-transcript.txt'
}
if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    $DestinationPath = Join-Path $localData 'reference.wav'
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $localData 'reference.manifest.json'
}
if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $PythonPath = Join-Path $repoRoot '.venv-spike\Scripts\python.exe'
}

if (-not $ConfirmRights) {
    throw 'RIGHTS_CONFIRMATION_REQUIRED: pass -ConfirmRights only for your own voice or a voice you may use.'
}
if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "REFERENCE_MASTER_MISSING: $SourcePath"
}
if (-not (Test-Path -LiteralPath $TranscriptPath -PathType Leaf)) {
    throw "REFERENCE_TRANSCRIPT_MISSING: $TranscriptPath"
}
if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
    throw "PYTHON_MISSING: $PythonPath"
}

$resolvedSource = (Resolve-Path -LiteralPath $SourcePath).Path
$resolvedTranscript = (Resolve-Path -LiteralPath $TranscriptPath).Path
$resolvedDestination = [IO.Path]::GetFullPath($DestinationPath)
$resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
$pathRoles = [ordered]@{
    source = $resolvedSource
    transcript = $resolvedTranscript
    destination = $resolvedDestination
    manifest = $resolvedManifest
}
$roleNames = @($pathRoles.Keys)
for ($leftIndex = 0; $leftIndex -lt $roleNames.Count; $leftIndex++) {
    for ($rightIndex = $leftIndex + 1; $rightIndex -lt $roleNames.Count; $rightIndex++) {
        $leftRole = $roleNames[$leftIndex]
        $rightRole = $roleNames[$rightIndex]
        if ([string]::Equals(
                [string]$pathRoles[$leftRole],
                [string]$pathRoles[$rightRole],
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "PATH_COLLISION: $leftRole and $rightRole must use different files."
        }
    }
}
$transcriptBytes = [IO.File]::ReadAllBytes($resolvedTranscript)
$transcriptHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($transcriptBytes)
).ToLowerInvariant()
$transcript = [Text.UTF8Encoding]::new($false, $true).GetString($transcriptBytes).Trim()
if ([string]::IsNullOrWhiteSpace($transcript)) {
    throw 'REFERENCE_TRANSCRIPT_EMPTY: literal transcript is required.'
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$Arguments = @(),

        [hashtable]$Environment = @{}
    )

    $processStartInfo = [Diagnostics.ProcessStartInfo]::new()
    $processStartInfo.FileName = $FilePath
    $processStartInfo.UseShellExecute = $false
    $processStartInfo.RedirectStandardOutput = $true
    $processStartInfo.RedirectStandardError = $true
    $processStartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $processStartInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $processStartInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void]$processStartInfo.ArgumentList.Add([string]$argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $processStartInfo.Environment[[string]$entry.Key] = [string]$entry.Value
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $processStartInfo
    try {
        if (-not $process.Start()) {
            throw "PROCESS_START_FAILED: $FilePath"
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdoutTask.GetAwaiter().GetResult()
            StdErr = $stderrTask.GetAwaiter().GetResult()
        }
    } finally {
        $process.Dispose()
    }
}

function Resolve-FfmpegExecutable {
    if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) {
        if (-not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) {
            throw "FFMPEG_MISSING: $FfmpegPath"
        }
        return (Resolve-Path -LiteralPath $FfmpegPath).Path
    }

    $command = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $wingetPackages = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path -LiteralPath $wingetPackages -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $wingetPackages -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'Gyan.FFmpeg.Essentials*' } |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object {
                Get-ChildItem -LiteralPath $_.FullName -Filter ffmpeg.exe -File -Recurse -ErrorAction SilentlyContinue
            } |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    throw 'FFMPEG_MISSING: install the pinned FFmpeg decoder or pass -FfmpegPath.'
}

$sourceHashBefore = (Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash.ToLowerInvariant()
$packageSource = Join-Path $repoRoot 'benchmarks\evaluator\src'
$existingPythonPath = [Environment]::GetEnvironmentVariable('PYTHONPATH', 'Process')
$pythonPackagePath = if ([string]::IsNullOrWhiteSpace($existingPythonPath)) {
    $packageSource
} else {
    "$packageSource$([IO.Path]::PathSeparator)$existingPythonPath"
}
$preparationSource = $resolvedSource
$temporaryDecoded = $null
$decoder = $null
$sourceExtension = [IO.Path]::GetExtension($resolvedSource).ToLowerInvariant()
if ($sourceExtension -notin @('.wav', '.m4a')) {
    throw "UNSUPPORTED_REFERENCE_FORMAT: expected .wav or .m4a, got '$sourceExtension'."
}

try {
    if ($sourceExtension -eq '.m4a') {
        $resolvedFfmpeg = Resolve-FfmpegExecutable
        $versionResult = Invoke-CapturedProcess -FilePath $resolvedFfmpeg -Arguments @('-version')
        $ffmpegVersion = ($versionResult.StdOut -split "`r?`n" | Select-Object -First 1).Trim()
        if ($versionResult.ExitCode -ne 0 -or $ffmpegVersion -notmatch '^ffmpeg version \S+') {
            throw "FFMPEG_INVALID: $($versionResult.StdErr.Trim())"
        }

        $destinationDirectory = Split-Path $resolvedDestination -Parent
        [void][IO.Directory]::CreateDirectory($destinationDirectory)
        $temporaryDecoded = Join-Path $destinationDirectory (
            "$([IO.Path]::GetFileName($resolvedDestination)).$([Guid]::NewGuid().ToString('N')).decode.wav"
        )
        $decodeResult = Invoke-CapturedProcess `
            -FilePath $resolvedFfmpeg `
            -Arguments @(
                '-nostdin', '-hide_banner', '-loglevel', 'error', '-y',
                '-i', $resolvedSource,
                '-map_metadata', '-1', '-vn',
                '-ac', '1', '-ar', '48000', '-c:a', 'pcm_s16le',
                $temporaryDecoded
            )
        if ($decodeResult.ExitCode -ne 0 -or
            -not (Test-Path -LiteralPath $temporaryDecoded -PathType Leaf) -or
            [long](Get-Item -LiteralPath $temporaryDecoded).Length -le 44) {
            throw "REFERENCE_DECODE_FAILED: $($decodeResult.StdErr.Trim())"
        }
        $preparationSource = $temporaryDecoded
        $decoder = [ordered]@{
            name = 'ffmpeg'
            version = $ffmpegVersion
            sha256 = (Get-FileHash -LiteralPath $resolvedFfmpeg -Algorithm SHA256).Hash.ToLowerInvariant()
            profile = 'mono-48000Hz-pcm_s16le'
        }
    }

    $pythonResult = Invoke-CapturedProcess `
        -FilePath $PythonPath `
        -Arguments @(
            '-m', 'spike_eval.audio', 'prepare',
            '--source', $preparationSource,
            '--destination', $resolvedDestination
        ) `
        -Environment @{
            PYTHONPATH = $pythonPackagePath
            PYTHONUTF8 = '1'
            PYTHONIOENCODING = 'utf-8'
        }
    if ($pythonResult.ExitCode -ne 0) {
        throw "REFERENCE_PREPARATION_FAILED: $($pythonResult.StdErr.Trim())"
    }
    $standardOutput = $pythonResult.StdOut

    $sourceHashAfter = (Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHashAfter -ne $sourceHashBefore) {
        throw 'MASTER_HASH_CHANGED: reference master was modified.'
    }
    $transcriptHashAfter = (Get-FileHash -LiteralPath $resolvedTranscript -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($transcriptHashAfter -ne $transcriptHash) {
        throw 'TRANSCRIPT_HASH_CHANGED: reference transcript was modified.'
    }
} finally {
    if ($null -ne $temporaryDecoded -and (Test-Path -LiteralPath $temporaryDecoded)) {
        Remove-Item -LiteralPath $temporaryDecoded -Force
    }
}

try {
    $reference = $standardOutput | ConvertFrom-Json -Depth 16
} catch {
    throw "REFERENCE_METADATA_INVALID: $($_.Exception.Message)"
}

$manifest = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    consent = [ordered]@{
        rightsConfirmed = $true
        localOnly = $true
    }
    source = [ordered]@{
        fileName = [IO.Path]::GetFileName($resolvedSource)
        sha256 = $sourceHashBefore
    }
    decoder = $decoder
    reference = $reference
    transcript = [ordered]@{
        fileName = [IO.Path]::GetFileName($resolvedTranscript)
        sha256 = $transcriptHash
        text = $transcript
    }
}

$manifestDirectory = Split-Path $resolvedManifest -Parent
[void][IO.Directory]::CreateDirectory($manifestDirectory)
$temporaryManifest = "$resolvedManifest.$([Guid]::NewGuid().ToString('N')).part"
try {
    $json = $manifest | ConvertTo-Json -Depth 16
    [IO.File]::WriteAllText($temporaryManifest, "$json`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryManifest, $resolvedManifest, $true)
} finally {
    if (Test-Path -LiteralPath $temporaryManifest) {
        Remove-Item -LiteralPath $temporaryManifest -Force
    }
}

[ordered]@{
    schemaVersion = 1
    manifestFileName = [IO.Path]::GetFileName($resolvedManifest)
    sourceSha256 = $sourceHashBefore
    referenceSha256 = [string]$reference.sha256
    transcriptSha256 = $transcriptHash
} | ConvertTo-Json -Depth 4

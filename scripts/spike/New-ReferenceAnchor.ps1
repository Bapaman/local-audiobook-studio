[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$TranscriptPath,
    [string]$DestinationPath,
    [string]$PromptTextPath,
    [string]$ManifestPath,
    [string]$PythonPath,
    [double]$StartSeconds = 6.95,
    [double]$DurationSeconds = 12.35,
    [int]$FirstSentence = 2,
    [int]$SentenceCount = 2,
    [switch]$ConfirmRights
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$localData = Join-Path $repoRoot 'local-test-data'
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $localData 'reference.wav'
}
if ([string]::IsNullOrWhiteSpace($TranscriptPath)) {
    $TranscriptPath = Join-Path $localData 'reference-transcript.txt'
}
if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    $DestinationPath = Join-Path $localData 'neutral-anchor.wav'
}
if ([string]::IsNullOrWhiteSpace($PromptTextPath)) {
    $PromptTextPath = Join-Path $localData 'neutral-anchor.txt'
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $localData 'neutral-anchor.manifest.json'
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
if ($FirstSentence -lt 1 -or $SentenceCount -lt 1) {
    throw 'SENTENCE_RANGE_INVALID: sentence indices are one-based and counts must be positive.'
}

$resolvedSource = (Resolve-Path -LiteralPath $SourcePath).Path
$resolvedTranscript = (Resolve-Path -LiteralPath $TranscriptPath).Path
$resolvedDestination = [IO.Path]::GetFullPath($DestinationPath)
$resolvedPrompt = [IO.Path]::GetFullPath($PromptTextPath)
$resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
$pathRoles = [ordered]@{
    source = $resolvedSource
    transcript = $resolvedTranscript
    destination = $resolvedDestination
    prompt = $resolvedPrompt
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

$sourceHash = (Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash.ToLowerInvariant()
$transcriptBytes = [IO.File]::ReadAllBytes($resolvedTranscript)
$transcriptHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($transcriptBytes)
).ToLowerInvariant()
$transcript = [Text.UTF8Encoding]::new($false, $true).GetString($transcriptBytes).Trim()
$sentences = @(
    [regex]::Split($transcript, '(?<=[.!?…])\s+') |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
$lastSentence = $FirstSentence + $SentenceCount - 1
if ($lastSentence -gt $sentences.Count) {
    throw "SENTENCE_RANGE_INVALID: requested sentences $FirstSentence-$lastSentence, transcript has $($sentences.Count)."
}
$promptText = (($sentences[($FirstSentence - 1)..($lastSentence - 1)] -join ' ').Trim() + ' ')

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [string[]]$Arguments,
        [hashtable]$Environment
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
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

foreach ($path in @($resolvedDestination, $resolvedPrompt, $resolvedManifest)) {
    [void][IO.Directory]::CreateDirectory((Split-Path $path -Parent))
}
$nonce = [Guid]::NewGuid().ToString('N')
$anchorStaging = "$resolvedDestination.$nonce.part"
$promptStaging = "$resolvedPrompt.$nonce.part"
$manifestStaging = "$resolvedManifest.$nonce.part"

try {
    $packageSource = Join-Path $repoRoot 'benchmarks\evaluator\src'
    $existingPythonPath = [Environment]::GetEnvironmentVariable('PYTHONPATH', 'Process')
    $pythonPackagePath = if ([string]::IsNullOrWhiteSpace($existingPythonPath)) {
        $packageSource
    } else {
        "$packageSource$([IO.Path]::PathSeparator)$existingPythonPath"
    }
    $pythonResult = Invoke-CapturedProcess `
        -FilePath $PythonPath `
        -Arguments @(
            '-m', 'spike_eval.audio', 'anchor',
            '--source', $resolvedSource,
            '--destination', $anchorStaging,
            '--start-seconds', $StartSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture),
            '--duration-seconds', $DurationSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        ) `
        -Environment @{
            PYTHONPATH = $pythonPackagePath
            PYTHONUTF8 = '1'
            PYTHONIOENCODING = 'utf-8'
        }
    if ($pythonResult.ExitCode -ne 0) {
        throw "ANCHOR_EXTRACTION_FAILED: $($pythonResult.StdErr.Trim())"
    }
    try {
        $reference = $pythonResult.StdOut | ConvertFrom-Json -Depth 16
    } catch {
        throw "ANCHOR_METADATA_INVALID: $($_.Exception.Message)"
    }

    if ((Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) {
        throw 'MASTER_HASH_CHANGED: normalized master was modified.'
    }
    if ((Get-FileHash -LiteralPath $resolvedTranscript -Algorithm SHA256).Hash.ToLowerInvariant() -ne $transcriptHash) {
        throw 'TRANSCRIPT_HASH_CHANGED: reference transcript was modified.'
    }

    [IO.File]::WriteAllText($promptStaging, $promptText, [Text.UTF8Encoding]::new($false))
    $promptHash = (Get-FileHash -LiteralPath $promptStaging -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        schemaVersion = 1
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        consent = [ordered]@{
            rightsConfirmed = $true
            localOnly = $true
        }
        source = [ordered]@{
            fileName = [IO.Path]::GetFileName($resolvedSource)
            sha256 = $sourceHash
        }
        transcript = [ordered]@{
            fileName = [IO.Path]::GetFileName($resolvedTranscript)
            sha256 = $transcriptHash
        }
        selection = [ordered]@{
            startSeconds = $StartSeconds
            durationSeconds = $DurationSeconds
            firstSentence = $FirstSentence
            sentenceCount = $SentenceCount
        }
        prompt = [ordered]@{
            fileName = [IO.Path]::GetFileName($resolvedPrompt)
            sha256 = $promptHash
            text = $promptText
        }
        reference = $reference
    }
    [IO.File]::WriteAllText(
        $manifestStaging,
        "$(($manifest | ConvertTo-Json -Depth 16))`n",
        [Text.UTF8Encoding]::new($false)
    )

    [IO.File]::Move($anchorStaging, $resolvedDestination, $true)
    [IO.File]::Move($promptStaging, $resolvedPrompt, $true)
    [IO.File]::Move($manifestStaging, $resolvedManifest, $true)
} finally {
    foreach ($path in @($anchorStaging, $promptStaging, $manifestStaging)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

[ordered]@{
    schemaVersion = 1
    referenceFileName = [IO.Path]::GetFileName($resolvedDestination)
    referenceSha256 = [string]$reference.sha256
    promptFileName = [IO.Path]::GetFileName($resolvedPrompt)
    promptSha256 = $promptHash
    manifestFileName = [IO.Path]::GetFileName($resolvedManifest)
} | ConvertTo-Json -Depth 4

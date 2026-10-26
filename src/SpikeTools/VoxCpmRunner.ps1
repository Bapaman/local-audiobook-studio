function Read-VoxCpmWavInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "INVALID_OUTPUT_WAV: file is missing: $Path"
    }
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $stream = [IO.File]::Open($resolved, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::ASCII, $false)
    try {
        if ($stream.Length -lt 44) {
            throw 'INVALID_OUTPUT_WAV: file is shorter than a canonical WAV header.'
        }
        $riff = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
        $null = $reader.ReadUInt32()
        $wave = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
        if ($riff -cne 'RIFF' -or $wave -cne 'WAVE') {
            throw 'INVALID_OUTPUT_WAV: RIFF/WAVE signature is missing.'
        }

        $audioFormat = 0
        $channels = 0
        $sampleRate = 0
        $blockAlign = 0
        $bitsPerSample = 0
        $dataBytes = 0L
        while ($stream.Position + 8 -le $stream.Length) {
            $chunkId = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
            $chunkSize = [long]$reader.ReadUInt32()
            $chunkStart = $stream.Position
            $chunkEnd = $chunkStart + $chunkSize
            if ($chunkEnd -gt $stream.Length) {
                throw "INVALID_OUTPUT_WAV: truncated '$chunkId' chunk."
            }
            if ($chunkId -ceq 'fmt ') {
                if ($chunkSize -lt 16) {
                    throw 'INVALID_OUTPUT_WAV: fmt chunk is too short.'
                }
                $audioFormat = [int]$reader.ReadUInt16()
                $channels = [int]$reader.ReadUInt16()
                $sampleRate = [int]$reader.ReadUInt32()
                $null = $reader.ReadUInt32()
                $blockAlign = [int]$reader.ReadUInt16()
                $bitsPerSample = [int]$reader.ReadUInt16()
            } elseif ($chunkId -ceq 'data') {
                $dataBytes = $chunkSize
            }
            $stream.Position = $chunkEnd + ($chunkSize % 2)
        }
        if ($audioFormat -ne 1 -or $channels -ne 1 -or $sampleRate -ne 48000 -or
            $bitsPerSample -ne 16 -or $blockAlign -ne 2 -or $dataBytes -le 0 -or
            $dataBytes % $blockAlign -ne 0) {
            throw "INVALID_OUTPUT_WAV: expected mono 48 kHz RIFF PCM16, got format=$audioFormat channels=$channels rate=$sampleRate bits=$bitsPerSample blockAlign=$blockAlign data=$dataBytes."
        }
    } finally {
        $reader.Dispose()
    }

    [pscustomobject]@{
        Path = $resolved
        AudioFormat = $audioFormat
        Channels = $channels
        SampleRate = $sampleRate
        BitsPerSample = $bitsPerSample
        DataBytes = $dataBytes
        DurationSeconds = [double]$dataBytes / ($sampleRate * $blockAlign)
        Sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-NvidiaTelemetrySample {
    $command = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }
    $result = Invoke-CheckedProcess `
        -FilePath $command.Source `
        -ArgumentList @(
            '--query-gpu=memory.used,memory.free,utilization.gpu',
            '--format=csv,noheader,nounits'
        )
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.StdOut)) {
        return $null
    }
    $fields = @((($result.StdOut -split "`r?`n")[0] -split ',') | ForEach-Object { $_.Trim() })
    if ($fields.Count -ne 3 -or @($fields | Where-Object { $_ -notmatch '^\d+$' }).Count -gt 0) {
        return $null
    }
    [pscustomobject]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        memoryUsedMiB = [int]$fields[0]
        memoryFreeMiB = [int]$fields[1]
        utilizationPercent = [int]$fields[2]
    }
}

function Protect-VoxCpmLogText {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [string]$Text = '',

        [Parameter(Mandatory)]
        [object]$Case,

        [string[]]$AdditionalSecrets = @()
    )

    $protected = [regex]::Replace(
        $Text,
        '(?im)^\s*(?:Text|Prompt text):.*$',
        '[REDACTED]'
    )
    $secrets = @(
        [string]$Case.Text
        [string]$Case.PromptText
        [string]$Case.BaseLmPath
        [string]$Case.AcousticPath
        [string]$Case.PromptWavPath
        [string]$Case.ReferencePath
        [string]$Case.OutputPath
        [string]$Case.AllowedOutputRoot
        @($Case.AllowedInputRoots | ForEach-Object { [string]$_ })
        @($AdditionalSecrets)
    ) |
        ForEach-Object {
            if (-not [string]::IsNullOrWhiteSpace([string]$_)) {
                [string]$_
                if ([string]$_ -match '\\') {
                    ([string]$_).Replace('\', '/')
                }
            }
        } |
        Sort-Object Length -Descending -Unique
    foreach ($secret in $secrets) {
        $protected = $protected.Replace($secret, '[REDACTED]')
    }
    $protected
}

function Get-VoxCpmCaseFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Case
    )

    if ($null -eq $Case.InputMetadata) {
        throw 'CASE_INPUT_METADATA_INVALID: immutable input metadata is required.'
    }
    $metadata = $Case.InputMetadata
    $revisionNames = @('workerRevision', 'modelRevision', 'converterRevision')
    $hashNames = @(
        'workerPatchSha256',
        'workerSha256',
        'modelManifestSha256',
        'baseLmSha256',
        'acousticSha256',
        'promptWavSha256',
        'promptTextSha256',
        'corpusSha256'
    )
    foreach ($name in $revisionNames) {
        if ([string]$metadata.$name -cnotmatch '^[0-9a-f]{40}$') {
            throw "CASE_INPUT_METADATA_INVALID: $name must be a lowercase full revision."
        }
    }
    foreach ($name in $hashNames) {
        if ([string]$metadata.$name -cnotmatch '^[0-9a-f]{64}$') {
            throw "CASE_INPUT_METADATA_INVALID: $name must be a lowercase SHA-256."
        }
    }

    $textSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]$Case.Text))
    ).ToLowerInvariant()
    $inputs = [ordered]@{
        workerRevision = [string]$metadata.workerRevision
        workerPatchSha256 = [string]$metadata.workerPatchSha256
        workerSha256 = [string]$metadata.workerSha256
        modelRevision = [string]$metadata.modelRevision
        converterRevision = [string]$metadata.converterRevision
        modelManifestSha256 = [string]$metadata.modelManifestSha256
        baseLmSha256 = [string]$metadata.baseLmSha256
        acousticSha256 = [string]$metadata.acousticSha256
        promptWavSha256 = [string]$metadata.promptWavSha256
        promptTextSha256 = [string]$metadata.promptTextSha256
        corpusSha256 = [string]$metadata.corpusSha256
        textSha256 = $textSha256
    }
    $parameters = [ordered]@{
        profileId = [string]$Case.ProfileId
        steps = [int]$Case.Steps
        timesteps = [int]$Case.Timesteps
        cfg = [double]$Case.Cfg
        temperature = [double]$Case.Temperature
        seed = [int]$Case.Seed
        gpuLayers = [int]$Case.GpuLayers
        useCpu = [bool]$Case.UseCpu
    }
    $canonical = [ordered]@{
        schemaVersion = 1
        inputs = $inputs
        parameters = $parameters
    }
    $canonicalJson = ConvertTo-Json -InputObject $canonical -Depth 6 -Compress
    $sha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonicalJson))
    ).ToLowerInvariant()
    [pscustomobject]@{
        Sha256 = $sha256
        Inputs = $inputs
        Parameters = $parameters
        TextSha256 = $textSha256
    }
}

function Invoke-VoxCpmCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Case,

        [Parameter(Mandatory)]
        [string]$WorkerPath,

        [string[]]$WorkerArgumentPrefix = @(),

        [ValidateRange(1, 86400)]
        [int]$TimeoutSeconds = 7200,

        [switch]$DisableTelemetry
    )

    if (-not (Test-Path -LiteralPath $WorkerPath -PathType Leaf)) {
        throw "WORKER_MISSING: $WorkerPath"
    }
    $workerFull = (Resolve-Path -LiteralPath $WorkerPath).Path
    $fingerprint = Get-VoxCpmCaseFingerprint -Case $Case
    $arguments = New-VoxCpmArgumentList -Case $Case
    $partPath = [IO.Path]::GetFullPath([string]$Case.OutputPath)
    $finalPath = $partPath.Substring(0, $partPath.Length - '.part'.Length)
    $outputRoot = [IO.Path]::GetFullPath([string]$Case.AllowedOutputRoot)
    [void][IO.Directory]::CreateDirectory($outputRoot)
    if (Test-Path -LiteralPath $finalPath) {
        throw "FINAL_OUTPUT_EXISTS: $finalPath"
    }
    if (Test-Path -LiteralPath $partPath) {
        Remove-Item -LiteralPath $partPath -Force
    }

    $caseId = if ([string]::IsNullOrWhiteSpace([string]$Case.Id)) { 'case' } else { [string]$Case.Id }
    $safeCaseId = [regex]::Replace($caseId, '[^A-Za-z0-9._-]', '_')
    $logRoot = Join-Path $outputRoot '_logs'
    [void][IO.Directory]::CreateDirectory($logRoot)
    $stdoutPath = Join-Path $logRoot "$safeCaseId.stdout.log"
    $stderrPath = Join-Path $logRoot "$safeCaseId.stderr.log"
    $telemetryPath = Join-Path $logRoot "$safeCaseId.telemetry.json"
    $resultPath = Join-Path $logRoot "$safeCaseId.result.json"

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $workerFull
    $startInfo.WorkingDirectory = Split-Path $workerFull -Parent
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @($WorkerArgumentPrefix) + @($arguments)) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $telemetry = [Collections.Generic.List[object]]::new()
    $timedOut = $false
    $exitCode = -1
    $stdout = ''
    $stderr = ''
    $started = $false
    try {
        if (-not $process.Start()) {
            throw 'WORKER_START_FAILED: native worker did not start.'
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(500)) {
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $timedOut = $true
                $process.Kill($true)
                $process.WaitForExit()
                break
            }
            if (-not $DisableTelemetry -and -not [bool]$Case.UseCpu) {
                $sample = Get-NvidiaTelemetrySample
                if ($null -ne $sample) {
                    $telemetry.Add($sample)
                }
            }
        }
        $exitCode = $process.ExitCode
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
    } finally {
        if ($started) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                    $process.WaitForExit()
                }
            } catch {
                # Best effort during cancellation or an exceptional read path.
            }
        }
        $stopwatch.Stop()
        $process.Dispose()
    }

    $logSecrets = @($partPath, $finalPath, $workerFull, (Split-Path $workerFull -Parent))
    $redactedStdout = Protect-VoxCpmLogText -Text $stdout -Case $Case -AdditionalSecrets $logSecrets
    $redactedStderr = Protect-VoxCpmLogText -Text $stderr -Case $Case -AdditionalSecrets $logSecrets
    [IO.File]::WriteAllText($stdoutPath, $redactedStdout, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($stderrPath, $redactedStderr, [Text.UTF8Encoding]::new($false))
    $telemetryJson = ConvertTo-Json -InputObject $telemetry -Depth 6
    [IO.File]::WriteAllText(
        $telemetryPath,
        "$telemetryJson`n",
        [Text.UTF8Encoding]::new($false)
    )

    $status = if ($timedOut) {
        'TIMEOUT'
    } else {
        Get-VoxCpmFailureCode -ExitCode $exitCode -StdErr $stderr
    }
    $wavInfo = $null
    if ($status -eq 'NONE') {
        try {
            $wavInfo = Read-VoxCpmWavInfo -Path $partPath
            [IO.File]::Move($partPath, $finalPath, $false)
            $wavInfo.Path = $finalPath
            $status = 'SUCCESS'
        } catch {
            $status = 'INVALID_OUTPUT'
            $stderr = "$stderr`n$($_.Exception.Message)"
        }
    }
    if ($status -ne 'SUCCESS' -and (Test-Path -LiteralPath $partPath)) {
        Remove-Item -LiteralPath $partPath -Force
    }

    $memoryFreeValues = @($telemetry | ForEach-Object { [int]$_.memoryFreeMiB })
    $memoryUsedValues = @($telemetry | ForEach-Object { [int]$_.memoryUsedMiB })
    $wavRecord = if ($status -eq 'SUCCESS') {
        [ordered]@{
            audioFormat = [int]$wavInfo.AudioFormat
            channels = [int]$wavInfo.Channels
            sampleRate = [int]$wavInfo.SampleRate
            bitsPerSample = [int]$wavInfo.BitsPerSample
            dataBytes = [long]$wavInfo.DataBytes
            durationSeconds = [double]$wavInfo.DurationSeconds
            sha256 = [string]$wavInfo.Sha256
        }
    } else {
        $null
    }
    $resultRecord = [ordered]@{
        schemaVersion = 1
        caseId = $caseId
        profileId = [string]$Case.ProfileId
        status = $status
        exitCode = $exitCode
        wallSeconds = $stopwatch.Elapsed.TotalSeconds
        outputFileName = if ($status -eq 'SUCCESS') { [IO.Path]::GetFileName($finalPath) } else { $null }
        outputSha256 = if ($status -eq 'SUCCESS') { [string]$wavInfo.Sha256 } else { $null }
        inputFingerprint = [string]$fingerprint.Sha256
        inputs = $fingerprint.Inputs
        parameters = $fingerprint.Parameters
        textSha256 = [string]$fingerprint.TextSha256
        seed = [int]$Case.Seed
        gpuLayers = [int]$Case.GpuLayers
        useCpu = [bool]$Case.UseCpu
        minFreeVramMiB = if ($memoryFreeValues.Count) { ($memoryFreeValues | Measure-Object -Minimum).Minimum } else { $null }
        peakUsedVramMiB = if ($memoryUsedValues.Count) { ($memoryUsedValues | Measure-Object -Maximum).Maximum } else { $null }
        wav = $wavRecord
    }
    [IO.File]::WriteAllText(
        $resultPath,
        "$(($resultRecord | ConvertTo-Json -Depth 8))`n",
        [Text.UTF8Encoding]::new($false)
    )
    [pscustomobject]@{
        CaseId = $caseId
        ProfileId = [string]$Case.ProfileId
        Status = $status
        ExitCode = $exitCode
        WallSeconds = $stopwatch.Elapsed.TotalSeconds
        OutputPath = if ($status -eq 'SUCCESS') { $finalPath } else { $null }
        ResultPath = $resultPath
        StdOutPath = $stdoutPath
        StdErrPath = $stderrPath
        TelemetryPath = $telemetryPath
        Error = if ($status -eq 'SUCCESS') { $null } else { $stderr.Trim() }
    }
}

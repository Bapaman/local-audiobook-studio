BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force

    function Write-TestPcm16Wav {
        param(
            [string]$Path,
            [int]$SampleRate = 48000,
            [int]$Channels = 1,
            [int]$BitsPerSample = 16
        )
        $bytesPerSample = [int]($BitsPerSample / 8)
        $frameCount = 480
        $dataSize = $frameCount * $Channels * $bytesPerSample
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::ASCII, $false)
        try {
            $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
            $writer.Write([int](36 + $dataSize))
            $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
            $writer.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
            $writer.Write([int]16)
            $writer.Write([int16]1)
            $writer.Write([int16]$Channels)
            $writer.Write([int]$SampleRate)
            $writer.Write([int]($SampleRate * $Channels * $bytesPerSample))
            $writer.Write([int16]($Channels * $bytesPerSample))
            $writer.Write([int16]$BitsPerSample)
            $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
            $writer.Write([int]$dataSize)
            $writer.Write([byte[]]::new($dataSize))
        } finally {
            $writer.Dispose()
        }
    }

    function New-RunnerCase {
        param([string]$OutputRoot)
        $inputRoot = Join-Path $TestDrive 'inputs'
        New-Item -ItemType Directory -Force -Path $inputRoot, $OutputRoot | Out-Null
        $base = Join-Path $inputRoot 'base.gguf'
        $acoustic = Join-Path $inputRoot 'acoustic.gguf'
        $prompt = Join-Path $inputRoot 'prompt.wav'
        foreach ($path in @($base, $acoustic, $prompt)) {
            'fixture' | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        }
        [pscustomobject]@{
            Id = 'fixture-case'
            ProfileId = 'fixture-profile'
            Text = 'Проверка безопасного запуска.'
            OutputPath = Join-Path $OutputRoot 'fixture.wav.part'
            AllowedOutputRoot = $OutputRoot
            AllowedInputRoots = @($inputRoot)
            BaseLmPath = $base
            AcousticPath = $acoustic
            PromptWavPath = $prompt
            PromptText = 'Точный эталон. '
            ReferencePath = $null
            Steps = 20
            Timesteps = 10
            Cfg = 2.0
            Temperature = 1.0
            Seed = 42
            GpuLayers = -1
            UseCpu = $false
            InputMetadata = [ordered]@{
                workerRevision = ('a' * 40)
                workerPatchSha256 = ('2' * 64)
                workerSha256 = ('b' * 64)
                modelRevision = ('c' * 40)
                converterRevision = ('3' * 40)
                modelManifestSha256 = ('4' * 64)
                baseLmSha256 = ('d' * 64)
                acousticSha256 = ('e' * 64)
                promptWavSha256 = ('f' * 64)
                promptTextSha256 = ('0' * 64)
                corpusSha256 = ('1' * 64)
            }
        }
    }
}

Describe 'Read-VoxCpmWavInfo' {
    It 'accepts only nonempty mono 48 kHz RIFF PCM16 output' {
        $valid = Join-Path $TestDrive 'valid.wav'
        Write-TestPcm16Wav -Path $valid

        $info = Read-VoxCpmWavInfo -Path $valid

        $info.SampleRate | Should -Be 48000
        $info.Channels | Should -Be 1
        $info.BitsPerSample | Should -Be 16
        $info.DataBytes | Should -BeGreaterThan 0

        $invalid = Join-Path $TestDrive 'invalid.wav'
        Write-TestPcm16Wav -Path $invalid -BitsPerSample 24
        { Read-VoxCpmWavInfo -Path $invalid } | Should -Throw '*INVALID_OUTPUT_WAV*'
    }
}

Describe 'Invoke-VoxCpmCase' {
    It 'publishes a validated WAV atomically and leaves no part file' {
        $outputRoot = Join-Path $TestDrive 'success'
        $case = New-RunnerCase -OutputRoot $outputRoot
        $worker = Join-Path $TestDrive 'fake-success.ps1'
        @'
$CliArgs = $args
[Console]::Out.WriteLine('Text: Проверка безопасного запуска.')
[Console]::Error.WriteLine('Arguments: ' + ($CliArgs -join '|'))
$outputIndex = [Array]::IndexOf($CliArgs, '-o')
if ($outputIndex -lt 0) { exit 9 }
$path = $CliArgs[$outputIndex + 1]
$sampleRate = 48000; $channels = 1; $bits = 16; $frames = 480
$dataSize = $frames * 2
$stream = [IO.File]::Open($path, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::ASCII, $false)
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36 + $dataSize))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVE')); $writer.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
    $writer.Write([int]16); $writer.Write([int16]1); $writer.Write([int16]1); $writer.Write([int]$sampleRate)
    $writer.Write([int]($sampleRate * 2)); $writer.Write([int16]2); $writer.Write([int16]16)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]$dataSize)
    $writer.Write([byte[]]::new($dataSize))
} finally { $writer.Dispose() }
'@ | Set-Content -LiteralPath $worker -Encoding utf8NoBOM

        $result = Invoke-VoxCpmCase `
            -Case $case `
            -WorkerPath (Get-Command pwsh).Source `
            -WorkerArgumentPrefix @('-NoProfile', '-File', $worker) `
            -DisableTelemetry

        $result.Status | Should -BeExactly 'SUCCESS'
        Test-Path -LiteralPath $case.OutputPath | Should -BeFalse
        Test-Path -LiteralPath ($case.OutputPath.Substring(0, $case.OutputPath.Length - 5)) | Should -BeTrue

        $telemetryJson = Get-Content -LiteralPath $result.TelemetryPath -Raw
        { $telemetryJson | ConvertFrom-Json -ErrorAction Stop } | Should -Not -Throw
        $telemetryJson.Trim() | Should -BeExactly '[]'

        $resultJson = Get-Content -LiteralPath $result.ResultPath -Raw
        $resultRecord = $resultJson | ConvertFrom-Json -ErrorAction Stop
        @($resultRecord.wav.PSObject.Properties.Name) | Should -Not -Contain 'Path'
        $resultRecord.inputs.workerSha256 | Should -BeExactly ('b' * 64)
        $resultRecord.parameters.timesteps | Should -Be 10
        $resultRecord.parameters.cfg | Should -Be 2.0
        $resultRecord.parameters.temperature | Should -Be 1.0
        $resultRecord.parameters.steps | Should -Be 20
        foreach ($privateFile in @($result.StdOutPath, $result.StdErrPath, $result.ResultPath)) {
            $privateText = Get-Content -LiteralPath $privateFile -Raw
            $privateText | Should -Not -Match ([regex]::Escape($TestDrive))
            $privateText | Should -Not -Match 'Проверка безопасного запуска'
            $privateText | Should -Not -Match 'Точный эталон'
        }
    }

    It 'classifies fake CUDA OOM, publishes no WAV and returns control' {
        $outputRoot = Join-Path $TestDrive 'oom'
        $case = New-RunnerCase -OutputRoot $outputRoot
        $worker = Join-Path $TestDrive 'fake-oom.ps1'
        "[Console]::Error.WriteLine('ggml_cuda: CUDA error: out of memory'); exit 17" |
            Set-Content -LiteralPath $worker -Encoding utf8NoBOM

        $result = Invoke-VoxCpmCase `
            -Case $case `
            -WorkerPath (Get-Command pwsh).Source `
            -WorkerArgumentPrefix @('-NoProfile', '-File', $worker) `
            -DisableTelemetry

        $result.Status | Should -BeExactly 'CUDA_OOM'
        Test-Path -LiteralPath $case.OutputPath | Should -BeFalse
        Test-Path -LiteralPath ($case.OutputPath.Substring(0, $case.OutputPath.Length - 5)) | Should -BeFalse
    }

    It 'kills the complete worker tree when a case times out' {
        $outputRoot = Join-Path $TestDrive 'timeout'
        $case = New-RunnerCase -OutputRoot $outputRoot
        $worker = Join-Path $TestDrive 'fake-timeout.ps1'
        $pidFile = Join-Path $TestDrive 'worker.pid'
        $escapedPidFile = $pidFile.Replace("'", "''")
        @"
[IO.File]::WriteAllText('$escapedPidFile', [string]`$PID)
Start-Sleep -Seconds 60
"@ | Set-Content -LiteralPath $worker -Encoding utf8NoBOM

        $result = Invoke-VoxCpmCase `
            -Case $case `
            -WorkerPath (Get-Command pwsh).Source `
            -WorkerArgumentPrefix @('-NoProfile', '-File', $worker) `
            -TimeoutSeconds 1 `
            -DisableTelemetry

        $result.Status | Should -BeExactly 'TIMEOUT'
        $workerPid = [int](Get-Content -LiteralPath $pidFile -Raw)
        Get-Process -Id $workerPid -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
    }
}

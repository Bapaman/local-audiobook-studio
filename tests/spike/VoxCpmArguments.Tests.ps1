BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force

    function New-TestCase {
        param([switch]$Cpu)

        $modelRoot = Join-Path $TestDrive 'Модели с пробелом'
        $privateRoot = Join-Path $TestDrive 'Личный голос'
        $outputRoot = Join-Path $TestDrive 'Результаты'
        foreach ($root in @($modelRoot, $privateRoot, $outputRoot)) {
            New-Item -ItemType Directory -Force -Path $root | Out-Null
        }
        $base = Join-Path $modelRoot 'VoxCPM2-BaseLM-Q8_0.gguf'
        $acoustic = Join-Path $modelRoot 'VoxCPM2-Acoustic-F16.gguf'
        $prompt = Join-Path $privateRoot 'эталон голоса.wav'
        foreach ($path in @($base, $acoustic, $prompt)) {
            if (-not (Test-Path -LiteralPath $path)) {
                'fixture' | Set-Content -LiteralPath $path -Encoding utf8NoBOM
            }
        }
        [pscustomobject]@{
            Text = '«Ёжик», — сказал рассказчик, — идёт домой.'
            OutputPath = Join-Path $outputRoot 'случай 01.wav.part'
            AllowedOutputRoot = $outputRoot
            AllowedInputRoots = @($modelRoot, $privateRoot)
            BaseLmPath = $base
            AcousticPath = $acoustic
            PromptWavPath = $prompt
            PromptText = 'Точный текст эталона. '
            ReferencePath = $null
            Steps = 70
            Timesteps = 10
            Cfg = 2.0
            Temperature = 1.0
            Seed = 42
            GpuLayers = if ($Cpu) { 0 } else { -1 }
            UseCpu = [bool]$Cpu
        }
    }
}

Describe 'New-VoxCpmArgumentList' {
    It 'preserves Unicode and quoting as literal argument-array elements for ultimate cloning' {
        $case = New-TestCase

        $arguments = New-VoxCpmArgumentList -Case $case

        $arguments | Should -Be @(
            '-t', $case.Text,
            '--prompt-wav', $case.PromptWavPath,
            '--prompt-text', $case.PromptText,
            '-o', $case.OutputPath,
            '--steps', '70',
            '--timesteps', '10',
            '--cfg', '2.0',
            '--temperature', '1.0',
            '--seed', '42',
            '--n-gpu-layers', '-1',
            $case.BaseLmPath,
            $case.AcousticPath
        )
    }

    It 'requires a complete exclusive clone mode and a single trailing prompt separator' {
        $case = New-TestCase
        $case.PromptText = $null
        { New-VoxCpmArgumentList -Case $case } | Should -Throw '*PROMPT_PAIR_REQUIRED*'

        $case = New-TestCase
        $case.ReferencePath = $case.PromptWavPath
        { New-VoxCpmArgumentList -Case $case } | Should -Throw '*CLONE_MODE_CONFLICT*'

        $case = New-TestCase
        $case.PromptText = 'Нет завершающего пробела.'
        { New-VoxCpmArgumentList -Case $case } | Should -Throw '*PROMPT_SEPARATOR_INVALID*'
    }

    It 'uses both CPU switches and supports basic reference cloning' {
        $case = New-TestCase -Cpu
        $case.ReferencePath = $case.PromptWavPath
        $case.PromptWavPath = $null
        $case.PromptText = $null

        $arguments = New-VoxCpmArgumentList -Case $case

        $arguments | Should -Contain '-r'
        $arguments | Should -Contain '--cpu'
        $gpuIndex = [Array]::IndexOf($arguments, '--n-gpu-layers')
        $arguments[$gpuIndex + 1] | Should -BeExactly '0'
    }

    It 'rejects output and inputs outside their declared roots' {
        $case = New-TestCase
        $case.OutputPath = Join-Path $TestDrive 'outside.wav.part'
        { New-VoxCpmArgumentList -Case $case } | Should -Throw '*OUTPUT_PATH_NOT_ALLOWED*'

        $case = New-TestCase
        $outside = Join-Path $TestDrive 'outside.gguf'
        'fixture' | Set-Content -LiteralPath $outside -Encoding utf8NoBOM
        $case.BaseLmPath = $outside
        { New-VoxCpmArgumentList -Case $case } | Should -Throw '*INPUT_PATH_NOT_ALLOWED*'
    }

    It 'requires the CLI to write only a staging WAV path' {
        $case = New-TestCase
        $case.OutputPath = Join-Path $case.AllowedOutputRoot 'final.wav'

        { New-VoxCpmArgumentList -Case $case } | Should -Throw '*OUTPUT_STAGING_REQUIRED*'
    }
}

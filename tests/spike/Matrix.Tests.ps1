BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force
}

Describe 'Get-VoxCpmMatrix' {
    It 'returns the six safe profiles in fallback-first execution order' {
        $modelRoot = Join-Path $TestDrive 'generated'
        New-Item -ItemType Directory -Path $modelRoot | Out-Null

        $profiles = @(Get-VoxCpmMatrix -ModelRoot $modelRoot)

        @($profiles.Id) | Should -Be @(
            'q8-f16-cuda-full',
            'q8-f16-hybrid-24',
            'q8-f16-hybrid-16',
            'f16-f16-cuda-full',
            'q8-f32-cuda-full',
            'q8-f16-cpu'
        )
        @($profiles.GpuLayers) | Should -Be @(-1, 24, 16, -1, -1, 0)
        @($profiles.UseCpu) | Should -Be @($false, $false, $false, $false, $false, $true)
        Split-Path $profiles[0].BaseLmPath -Leaf | Should -BeExactly 'VoxCPM2-BaseLM-Q8_0.gguf'
        Split-Path $profiles[3].BaseLmPath -Leaf | Should -BeExactly 'VoxCPM2-BaseLM-F16.gguf'
        Split-Path $profiles[4].AcousticPath -Leaf | Should -BeExactly 'VoxCPM2-Acoustic-F32.gguf'
    }
}

Describe 'Get-VoxCpmFailureCode' {
    It 'classifies CUDA OOM independently of localized surrounding output' {
        Get-VoxCpmFailureCode -ExitCode 1 -StdErr 'ggml_cuda: CUDA error: out of memory' |
            Should -BeExactly 'CUDA_OOM'
        Get-VoxCpmFailureCode -ExitCode 7 -StdErr 'ordinary failure' |
            Should -BeExactly 'PROCESS_FAILED'
        Get-VoxCpmFailureCode -ExitCode 0 -StdErr '' |
            Should -BeExactly 'NONE'
    }
}

Describe 'Invoke-VoxCpmProfileSequence' {
    It 'records an OOM and continues with the next fresh profile' {
        $profiles = @(
            [pscustomobject]@{ Id = 'full' },
            [pscustomobject]@{ Id = 'hybrid' }
        )
        $calls = [Collections.Generic.List[string]]::new()
        $invoker = {
            param($profile)
            $calls.Add($profile.Id)
            if ($profile.Id -eq 'full') {
                return [pscustomobject]@{ ProfileId = $profile.Id; Status = 'CUDA_OOM' }
            }
            return [pscustomobject]@{ ProfileId = $profile.Id; Status = 'SUCCESS' }
        }

        $results = @(Invoke-VoxCpmProfileSequence -Profiles $profiles -Invoker $invoker)

        @($calls) | Should -Be @('full', 'hybrid')
        @($results.Status) | Should -Be @('CUDA_OOM', 'SUCCESS')
    }
}

Describe 'New-VoxCpmRunPlan' {
    BeforeEach {
        $profiles = @(
            [pscustomobject]@{ Id = 'full' },
            [pscustomobject]@{ Id = 'hybrid' }
        )
        $corpusItems = 1..16 | ForEach-Object {
            [pscustomobject]@{ id = ('ru-v1-{0:d2}' -f $_); text = "item $_"; max_steps = 70 }
        }
    }

    It 'plans one fixed-seed preview item across every profile' {
        $plan = @(New-VoxCpmRunPlan -Mode preview -Profiles $profiles -CorpusItems $corpusItems)

        $plan.Count | Should -Be 2
        @($plan.CorpusId) | Should -Be @('ru-v1-01', 'ru-v1-01')
        @($plan.ProfileId) | Should -Be @('full', 'hybrid')
        @($plan.Seed) | Should -Be @(42, 42)
        @($plan.Id) | Should -Be @(
            'ru-v1-01.full.seed-42',
            'ru-v1-01.hybrid.seed-42'
        )
    }

    It 'plans all 48 stability cases for exactly one selected profile' {
        $plan = @(
            New-VoxCpmRunPlan `
                -Mode Stability `
                -Profiles $profiles `
                -CorpusItems $corpusItems `
                -SelectedProfileId 'hybrid'
        )

        $plan.Count | Should -Be 48
        @($plan.ProfileId | Sort-Object -Unique) | Should -Be @('hybrid')
        @($plan.Seed | Sort-Object -Unique) | Should -Be @(42, 43, 44)
        @($plan.CorpusId | Sort-Object -Unique).Count | Should -Be 16
        @($plan.Id | Sort-Object -Unique).Count | Should -Be 48
    }

    It 'rejects stability mode without one known selected profile' {
        {
            New-VoxCpmRunPlan -Mode Stability -Profiles $profiles -CorpusItems $corpusItems
        } | Should -Throw '*STABILITY_PROFILE_REQUIRED*'
        {
            New-VoxCpmRunPlan `
                -Mode Stability `
                -Profiles $profiles `
                -CorpusItems $corpusItems `
                -SelectedProfileId 'missing'
        } | Should -Throw '*PROFILE_UNKNOWN*'
    }

    It 'requires the complete 16-item corpus for a stability run' {
        {
            New-VoxCpmRunPlan `
                -Mode Stability `
                -Profiles $profiles `
                -CorpusItems @($corpusItems | Select-Object -First 15) `
                -SelectedProfileId 'hybrid'
        } | Should -Throw '*STABILITY_CORPUS_INVALID*'
    }
}

Describe 'Invoke-VoxCpmMatrix.ps1 planning' {
    It 'emits a safe six-case preview plan without requiring model files' {
        $modelRoot = Join-Path $TestDrive 'preview-models'
        New-Item -ItemType Directory -Path $modelRoot | Out-Null

        $json = & (Join-Path $repoRoot 'scripts\spike\Invoke-VoxCpmMatrix.ps1') `
            -Mode Preview `
            -ModelRoot $modelRoot `
            -PlanOnly |
            Out-String
        $plan = $json | ConvertFrom-Json

        $plan.mode | Should -BeExactly 'Preview'
        $plan.caseCount | Should -Be 6
        @($plan.cases.profileId) | Should -Be @(
            'q8-f16-cuda-full',
            'q8-f16-hybrid-24',
            'q8-f16-hybrid-16',
            'f16-f16-cuda-full',
            'q8-f32-cuda-full',
            'q8-f16-cpu'
        )
        $json | Should -Not -Match 'text|prompt'
    }

    It 'emits the complete stability plan for the selected profile' {
        $modelRoot = Join-Path $TestDrive 'stability-models'
        New-Item -ItemType Directory -Path $modelRoot | Out-Null

        $plan = (& (Join-Path $repoRoot 'scripts\spike\Invoke-VoxCpmMatrix.ps1') `
            -Mode Stability `
            -SelectedProfileId 'q8-f16-hybrid-24' `
            -ModelRoot $modelRoot `
            -PlanOnly |
            Out-String) | ConvertFrom-Json

        $plan.caseCount | Should -Be 48
        @($plan.cases.profileId | Sort-Object -Unique) | Should -Be @('q8-f16-hybrid-24')
    }
}

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $modulePath = Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1'
    if (Test-Path -LiteralPath $modulePath) {
        Import-Module $modulePath -Force
    }

    function New-ValidEnvironment {
        [pscustomobject]@{
            PowerShellVersion = '7.6.5'
            Python311        = $true
            CMake            = $true
            Ninja            = $true
            Msvc             = $true
            Nvcc             = $true
            DriverVersion    = '610.47'
            GpuName          = 'NVIDIA GeForce RTX 2070 SUPER'
            VramMiB          = 8192
        }
    }
}

Describe 'Test-SpikeEnvironment' {
    It 'reports each missing required build tool without rejecting a current NVIDIA driver' {
        $environment = [pscustomobject]@{
            PowerShellVersion = '7.6.5'
            Python311        = $false
            CMake            = $false
            Ninja            = $false
            Msvc             = $false
            Nvcc             = $false
            DriverVersion    = '610.47'
            GpuName          = 'NVIDIA GeForce RTX 2070 SUPER'
            VramMiB          = 8192
        }

        $issues = Test-SpikeEnvironment $environment

        $issues.Code | Should -Contain 'PYTHON_311_MISSING'
        $issues.Code | Should -Contain 'CMAKE_MISSING'
        $issues.Code | Should -Contain 'NINJA_MISSING'
        $issues.Code | Should -Contain 'MSVC_MISSING'
        $issues.Code | Should -Contain 'CUDA_TOOLKIT_MISSING'
        $issues.Code | Should -Not -Contain 'NVIDIA_DRIVER_TOO_OLD'
    }

    It 'rejects an NVIDIA driver older than 572.61' {
        $environment = New-ValidEnvironment
        $environment.DriverVersion = '571.00'

        $issues = Test-SpikeEnvironment $environment

        $issues.Code | Should -Contain 'NVIDIA_DRIVER_TOO_OLD'
    }

    It 'rejects a GPU with less than 8192 MiB VRAM' {
        $environment = New-ValidEnvironment
        $environment.VramMiB = 6144

        $issues = Test-SpikeEnvironment $environment

        $issues.Code | Should -Contain 'GPU_VRAM_TOO_LOW'
    }

    It 'rejects PowerShell older than 7.6' {
        $environment = New-ValidEnvironment
        $environment.PowerShellVersion = '7.5.4'

        $issues = Test-SpikeEnvironment $environment

        $issues.Code | Should -Contain 'POWERSHELL_TOO_OLD'
    }

    It 'reports a missing NVIDIA GPU and driver without a duplicate VRAM error' {
        $environment = New-ValidEnvironment
        $environment.GpuName = $null
        $environment.DriverVersion = $null
        $environment.VramMiB = 0

        $issues = Test-SpikeEnvironment $environment

        $issues.Code | Should -Contain 'NVIDIA_GPU_MISSING'
        $issues.Code | Should -Contain 'NVIDIA_DRIVER_MISSING'
        $issues.Code | Should -Not -Contain 'GPU_VRAM_TOO_LOW'
    }

    It 'returns no issues for a fully valid environment' {
        $issues = Test-SpikeEnvironment (New-ValidEnvironment)

        $issues | Should -BeNullOrEmpty
    }
}

Describe 'Get-SpikeEnvironment' {
    It 'returns the canonical probe properties even when optional tools are missing' {
        $environment = Get-SpikeEnvironment

        $environment.PowerShellVersion | Should -Not -BeNullOrEmpty
        $environment.Python311 | Should -BeOfType ([bool])
        $environment.CMake | Should -BeOfType ([bool])
        $environment.Ninja | Should -BeOfType ([bool])
        $environment.Msvc | Should -BeOfType ([bool])
        $environment.Nvcc | Should -BeOfType ([bool])
        $environment.PSObject.Properties.Name | Should -Contain 'DriverVersion'
        $environment.PSObject.Properties.Name | Should -Contain 'GpuName'
        $environment.PSObject.Properties.Name | Should -Contain 'VramMiB'
    }

    It 'detects NVIDIA details when nvidia-smi is available' -Skip:(-not (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) {
        $environment = Get-SpikeEnvironment

        $environment.GpuName | Should -Not -BeNullOrEmpty
        $environment.VramMiB | Should -BeGreaterOrEqual 8192
        $environment.DriverVersion | Should -Not -BeNullOrEmpty
    }

    It 'detects Python 3.11 when the Python launcher can run it' -Skip:(-not (Get-Command py -ErrorAction SilentlyContinue)) {
        $launcherOutput = & py -3.11 --version 2>&1
        if ($launcherOutput -notmatch '^Python 3\.11(?:\.\d+)?$') {
            Set-ItResult -Skipped -Because 'Python 3.11 is not registered with the Python launcher.'
            return
        }

        & cmd.exe /c exit 17
        $environment = Get-SpikeEnvironment

        $environment.Python311 | Should -BeTrue
        $environment.PythonVersion | Should -Match '^3\.11(?:\.\d+)?$'
    }
}

Describe 'Get-SpikePreflight.ps1' {
    It 'writes a successful JSON report and exits zero for a valid injected environment' {
        $environmentPath = Join-Path $TestDrive 'valid-environment.json'
        $outputPath = Join-Path $TestDrive 'valid-preflight.json'
        New-ValidEnvironment | ConvertTo-Json | Set-Content -LiteralPath $environmentPath -Encoding utf8NoBOM
        $scriptPath = Join-Path $repoRoot 'scripts\spike\Get-SpikePreflight.ps1'

        & pwsh -NoProfile -File $scriptPath -EnvironmentPath $environmentPath -OutputPath $outputPath
        $exitCode = $LASTEXITCODE

        $exitCode | Should -Be 0
        $report = Get-Content -Raw -LiteralPath $outputPath | ConvertFrom-Json
        $report.Environment.GpuName | Should -Be 'NVIDIA GeForce RTX 2070 SUPER'
        @($report.Issues).Count | Should -Be 0
    }

    It 'writes issue codes and exits one for an invalid injected environment' {
        $environment = New-ValidEnvironment
        $environment.Python311 = $false
        $environmentPath = Join-Path $TestDrive 'invalid-environment.json'
        $outputPath = Join-Path $TestDrive 'invalid-preflight.json'
        $environment | ConvertTo-Json | Set-Content -LiteralPath $environmentPath -Encoding utf8NoBOM
        $scriptPath = Join-Path $repoRoot 'scripts\spike\Get-SpikePreflight.ps1'

        & pwsh -NoProfile -File $scriptPath -EnvironmentPath $environmentPath -OutputPath $outputPath
        $exitCode = $LASTEXITCODE

        $exitCode | Should -Be 1
        $report = Get-Content -Raw -LiteralPath $outputPath | ConvertFrom-Json
        @($report.Issues).Code | Should -Contain 'PYTHON_311_MISSING'
    }
}

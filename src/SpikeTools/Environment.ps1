function Get-SpikeEnvironment {
    [CmdletBinding()]
    param()

    $python311 = $false
    $pythonVersion = $null
    $pythonCandidates = @(
        @{ File = 'py'; Arguments = @('-3.11', '--version') },
        @{ File = 'python3.11'; Arguments = @('--version') },
        @{ File = 'python'; Arguments = @('--version') }
    )

    foreach ($candidate in $pythonCandidates) {
        $command = Get-Command $candidate.File -ErrorAction SilentlyContinue
        if (-not $command) {
            continue
        }

        $output = & $command.Source @($candidate.Arguments) 2>&1 | Select-Object -First 1
        if ($output -match 'Python\s+(3\.11(?:\.\d+)?)') {
            $python311 = $true
            $pythonVersion = $Matches[1]
            break
        }
    }

    $cmakeCommand = Get-Command 'cmake' -ErrorAction SilentlyContinue
    $cmakeVersion = $null
    if ($cmakeCommand) {
        $cmakeLine = & $cmakeCommand.Source --version 2>$null | Select-Object -First 1
        if ($cmakeLine -match '(\d+\.\d+(?:\.\d+)?)') {
            $cmakeVersion = $Matches[1]
        }
    }

    $ninjaCommand = Get-Command 'ninja' -ErrorAction SilentlyContinue
    $ninjaVersion = if ($ninjaCommand) {
        (& $ninjaCommand.Source --version 2>$null | Select-Object -First 1).Trim()
    } else {
        $null
    }

    $clCommand = Get-Command 'cl' -ErrorAction SilentlyContinue
    $vsWherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msvcAvailable = [bool]$clCommand
    $msvcVersion = $null
    if ($clCommand) {
        $clLine = & $clCommand.Source 2>&1 | Select-Object -First 1
        if ($clLine -match 'Version\s+([\d\.]+)') {
            $msvcVersion = $Matches[1]
        }
    } elseif (Test-Path -LiteralPath $vsWherePath) {
        $installation = & $vsWherePath `
            -latest `
            -products '*' `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationVersion 2>$null | Select-Object -First 1
        if ($installation) {
            $msvcAvailable = $true
            $msvcVersion = $installation.Trim()
        }
    }

    $nvccCommand = Get-Command 'nvcc' -ErrorAction SilentlyContinue
    $cudaVersion = $null
    if ($nvccCommand) {
        $nvccOutput = & $nvccCommand.Source --version 2>$null
        $releaseLine = $nvccOutput | Select-String 'release\s+([\d\.]+)' | Select-Object -First 1
        if ($releaseLine -and $releaseLine.Matches.Count -gt 0) {
            $cudaVersion = $releaseLine.Matches[0].Groups[1].Value
        }
    }

    $gpuName = $null
    $vramMiB = 0
    $driverVersion = $null
    $nvidiaSmi = Get-Command 'nvidia-smi' -ErrorAction SilentlyContinue
    if ($nvidiaSmi) {
        $gpuLine = & $nvidiaSmi.Source `
            '--query-gpu=name,memory.total,driver_version' `
            '--format=csv,noheader,nounits' 2>$null | Select-Object -First 1
        if ($gpuLine) {
            $parts = $gpuLine -split ',' | ForEach-Object { $_.Trim() }
            if ($parts.Count -ge 3) {
                $gpuName = $parts[0]
                $parsedVram = 0
                if ([int]::TryParse($parts[1], [ref]$parsedVram)) {
                    $vramMiB = $parsedVram
                }
                $driverVersion = $parts[2]
            }
        }
    }

    [pscustomobject]@{
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
        Python311        = [bool]$python311
        PythonVersion    = $pythonVersion
        CMake            = [bool]$cmakeCommand
        CMakeVersion     = $cmakeVersion
        Ninja            = [bool]$ninjaCommand
        NinjaVersion     = $ninjaVersion
        Msvc             = [bool]$msvcAvailable
        MsvcVersion      = $msvcVersion
        Nvcc             = [bool]$nvccCommand
        CudaVersion      = $cudaVersion
        DriverVersion    = $driverVersion
        GpuName          = $gpuName
        VramMiB          = [int]$vramMiB
    }
}

function Test-SpikeEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Environment
    )

    $issues = [System.Collections.Generic.List[object]]::new()

    try {
        if ([version]$Environment.PowerShellVersion -lt [version]'7.6') {
            $issues.Add([pscustomobject]@{
                Code = 'POWERSHELL_TOO_OLD'; Severity = 'Error'
                Message = 'PowerShell 7.6 or newer is required.'
                Remediation = 'Install the current PowerShell 7 release.'
            })
        }
    } catch {
        $issues.Add([pscustomobject]@{
            Code = 'POWERSHELL_VERSION_INVALID'; Severity = 'Error'
            Message = 'The PowerShell version could not be parsed.'
            Remediation = 'Run the spike from PowerShell 7.6 or newer.'
        })
    }

    $toolChecks = @(
        @{ Property = 'Python311'; Code = 'PYTHON_311_MISSING'; Message = 'Python 3.11 is required.'; Remediation = 'Install official Python 3.11 x64.' },
        @{ Property = 'CMake'; Code = 'CMAKE_MISSING'; Message = 'CMake is required.'; Remediation = 'Install CMake from the Visual Studio Build Tools workload or Kitware.' },
        @{ Property = 'Ninja'; Code = 'NINJA_MISSING'; Message = 'Ninja is required.'; Remediation = 'Install Ninja and add it to PATH.' },
        @{ Property = 'Msvc'; Code = 'MSVC_MISSING'; Message = 'MSVC v143 Build Tools are required.'; Remediation = 'Install Visual Studio 2022 Build Tools with C++ tools.' },
        @{ Property = 'Nvcc'; Code = 'CUDA_TOOLKIT_MISSING'; Message = 'CUDA Toolkit 12.8 Update 1 is required for the worker build.'; Remediation = 'Install CUDA Toolkit 12.8 Update 1, not CUDA 13.' }
    )

    foreach ($check in $toolChecks) {
        if (-not [bool]$Environment.($check.Property)) {
            $issues.Add([pscustomobject]@{
                Code = $check.Code; Severity = 'Error'
                Message = $check.Message; Remediation = $check.Remediation
            })
        }
    }

    $hasGpu = -not [string]::IsNullOrWhiteSpace([string]$Environment.GpuName)
    if (-not $hasGpu) {
        $issues.Add([pscustomobject]@{
            Code = 'NVIDIA_GPU_MISSING'; Severity = 'Error'
            Message = 'A supported NVIDIA GPU was not detected.'
            Remediation = 'Use a Windows computer with an NVIDIA GPU and at least 8 GiB VRAM.'
        })
    } elseif ([int]$Environment.VramMiB -lt 8192) {
        $issues.Add([pscustomobject]@{
            Code = 'GPU_VRAM_TOO_LOW'; Severity = 'Error'
            Message = 'At least 8192 MiB of NVIDIA VRAM is required for the spike.'
            Remediation = 'Use an 8 GiB or larger NVIDIA GPU.'
        })
    }

    if ([string]::IsNullOrWhiteSpace([string]$Environment.DriverVersion)) {
        $issues.Add([pscustomobject]@{
            Code = 'NVIDIA_DRIVER_MISSING'; Severity = 'Error'
            Message = 'The NVIDIA driver version could not be detected.'
            Remediation = 'Install a current NVIDIA driver and verify nvidia-smi works.'
        })
    } else {
        try {
            if ([version]$Environment.DriverVersion -lt [version]'572.61') {
                $issues.Add([pscustomobject]@{
                    Code = 'NVIDIA_DRIVER_TOO_OLD'; Severity = 'Error'
                    Message = 'NVIDIA driver 572.61 or newer is required.'
                    Remediation = 'Update the NVIDIA driver before building the CUDA worker.'
                })
            }
        } catch {
            $issues.Add([pscustomobject]@{
                Code = 'NVIDIA_DRIVER_VERSION_INVALID'; Severity = 'Error'
                Message = 'The NVIDIA driver version could not be parsed.'
                Remediation = 'Update the driver and verify nvidia-smi output.'
            })
        }
    }

    return $issues.ToArray()
}

function Get-VoxCpmMatrix {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ModelRoot
    )

    $root = [IO.Path]::GetFullPath($ModelRoot)
    $definitions = @(
        @('q8-f16-cuda-full', 'VoxCPM2-BaseLM-Q8_0.gguf', 'VoxCPM2-Acoustic-F16.gguf', -1, $false),
        @('q8-f16-hybrid-24', 'VoxCPM2-BaseLM-Q8_0.gguf', 'VoxCPM2-Acoustic-F16.gguf', 24, $false),
        @('q8-f16-hybrid-16', 'VoxCPM2-BaseLM-Q8_0.gguf', 'VoxCPM2-Acoustic-F16.gguf', 16, $false),
        @('f16-f16-cuda-full', 'VoxCPM2-BaseLM-F16.gguf', 'VoxCPM2-Acoustic-F16.gguf', -1, $false),
        @('q8-f32-cuda-full', 'VoxCPM2-BaseLM-Q8_0.gguf', 'VoxCPM2-Acoustic-F32.gguf', -1, $false),
        @('q8-f16-cpu', 'VoxCPM2-BaseLM-Q8_0.gguf', 'VoxCPM2-Acoustic-F16.gguf', 0, $true)
    )
    $priority = 0
    foreach ($definition in $definitions) {
        $priority++
        [pscustomobject]@{
            Id = [string]$definition[0]
            Priority = $priority
            BaseLmPath = Join-Path $root ([string]$definition[1])
            AcousticPath = Join-Path $root ([string]$definition[2])
            GpuLayers = [int]$definition[3]
            UseCpu = [bool]$definition[4]
        }
    }
}

function Get-VoxCpmFailureCode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$ExitCode,

        [AllowEmptyString()]
        [string]$StdErr = ''
    )

    if ($ExitCode -eq 0) {
        return 'NONE'
    }
    if ($StdErr -match '(?i)(?:CUDA[^\r\n]*(?:out of memory|error\s*2)|out of memory[^\r\n]*CUDA|cudaMalloc[^\r\n]*fail)') {
        return 'CUDA_OOM'
    }
    return 'PROCESS_FAILED'
}

function Invoke-VoxCpmProfileSequence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Profiles,

        [Parameter(Mandatory)]
        [scriptblock]$Invoker
    )

    foreach ($profile in $Profiles) {
        try {
            $result = & $Invoker $profile
            if ($null -eq $result) {
                [pscustomobject]@{ ProfileId = [string]$profile.Id; Status = 'EMPTY_RESULT' }
            } else {
                $result
            }
        } catch {
            [pscustomobject]@{
                ProfileId = [string]$profile.Id
                Status = 'UNHANDLED_FAILURE'
                Error = $_.Exception.Message
            }
        }
    }
}

function New-VoxCpmRunPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Preview', 'Stability')]
        [string]$Mode,

        [Parameter(Mandatory)]
        [object[]]$Profiles,

        [Parameter(Mandatory)]
        [object[]]$CorpusItems,

        [string]$SelectedProfileId
    )

    if ($Profiles.Count -eq 0) {
        throw 'PROFILE_MATRIX_EMPTY: at least one profile is required.'
    }
    if ($CorpusItems.Count -eq 0) {
        throw 'CORPUS_EMPTY: at least one corpus item is required.'
    }

    $profileIds = @($Profiles | ForEach-Object { [string]$_.Id })
    if (@($profileIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
        @($profileIds | Sort-Object -Unique).Count -ne $profileIds.Count) {
        throw 'PROFILE_MATRIX_INVALID: profile ids must be nonempty and unique.'
    }
    $corpusIds = @($CorpusItems | ForEach-Object { [string]$_.id })
    if (@($corpusIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
        @($corpusIds | Sort-Object -Unique).Count -ne $corpusIds.Count) {
        throw 'CORPUS_INVALID: corpus ids must be nonempty and unique.'
    }

    if ($Mode -ieq 'Preview') {
        $corpusId = $corpusIds[0]
        foreach ($profile in $Profiles) {
            $profileId = [string]$profile.Id
            [pscustomobject]@{
                Id = "$corpusId.$profileId.seed-42"
                CorpusId = $corpusId
                ProfileId = $profileId
                Seed = 42
            }
        }
        return
    }

    if ([string]::IsNullOrWhiteSpace($SelectedProfileId)) {
        throw 'STABILITY_PROFILE_REQUIRED: select exactly one profile for stability mode.'
    }
    if ($CorpusItems.Count -ne 16) {
        throw "STABILITY_CORPUS_INVALID: expected 16 corpus items, found $($CorpusItems.Count)."
    }
    $selected = @($Profiles | Where-Object { [string]$_.Id -ceq $SelectedProfileId })
    if ($selected.Count -ne 1) {
        throw "PROFILE_UNKNOWN: $SelectedProfileId"
    }
    foreach ($corpusId in $corpusIds) {
        foreach ($seed in @(42, 43, 44)) {
            [pscustomobject]@{
                Id = "$corpusId.$SelectedProfileId.seed-$seed"
                CorpusId = $corpusId
                ProfileId = $SelectedProfileId
                Seed = $seed
            }
        }
    }
}

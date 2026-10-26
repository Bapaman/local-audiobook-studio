function Test-PathInsideRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Root
    )

    $pathFull = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    return (
        [string]::Equals($pathFull, $rootFull, [StringComparison]::OrdinalIgnoreCase) -or
        $pathFull.StartsWith(
            $rootFull + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase
        )
    )
}

function New-VoxCpmArgumentList {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Case
    )

    if ([string]::IsNullOrWhiteSpace([string]$Case.Text)) {
        throw 'TEXT_REQUIRED: synthesis text cannot be empty.'
    }
    $outputPath = [IO.Path]::GetFullPath([string]$Case.OutputPath)
    $outputRoot = [IO.Path]::GetFullPath([string]$Case.AllowedOutputRoot)
    if (-not (Test-PathInsideRoot -Path $outputPath -Root $outputRoot)) {
        throw "OUTPUT_PATH_NOT_ALLOWED: $outputPath"
    }
    if (-not $outputPath.EndsWith('.wav.part', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OUTPUT_STAGING_REQUIRED: the native worker may write only to a .wav.part path.'
    }

    $allowedInputRoots = @($Case.AllowedInputRoots)
    if ($allowedInputRoots.Count -eq 0) {
        throw 'INPUT_ROOT_REQUIRED: at least one allowed input root is required.'
    }
    $inputPaths = @([string]$Case.BaseLmPath, [string]$Case.AcousticPath)
    $hasPromptWav = -not [string]::IsNullOrWhiteSpace([string]$Case.PromptWavPath)
    $hasPromptText = -not [string]::IsNullOrWhiteSpace([string]$Case.PromptText)
    $hasReference = -not [string]::IsNullOrWhiteSpace([string]$Case.ReferencePath)
    if ($hasPromptWav -xor $hasPromptText) {
        throw 'PROMPT_PAIR_REQUIRED: ultimate cloning requires both prompt WAV and exact prompt text.'
    }
    if ($hasPromptWav -and $hasReference) {
        throw 'CLONE_MODE_CONFLICT: prompt cloning and basic reference cloning are mutually exclusive.'
    }
    if (-not $hasPromptWav -and -not $hasReference) {
        throw 'CLONE_MODE_REQUIRED: a prompt pair or reference WAV is required.'
    }
    if ($hasPromptWav) {
        $promptText = [string]$Case.PromptText
        if (-not $promptText.EndsWith(' ', [StringComparison]::Ordinal) -or
            $promptText.EndsWith('  ', [StringComparison]::Ordinal) -or
            $promptText -match "[`r`n]\s*$") {
            throw 'PROMPT_SEPARATOR_INVALID: exact prompt text must end with exactly one ordinary space.'
        }
        $inputPaths += [string]$Case.PromptWavPath
    } else {
        $inputPaths += [string]$Case.ReferencePath
    }

    foreach ($inputPathValue in $inputPaths) {
        $inputPath = [IO.Path]::GetFullPath($inputPathValue)
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
            throw "INPUT_FILE_MISSING: $inputPath"
        }
        $allowed = @($allowedInputRoots | Where-Object {
            Test-PathInsideRoot -Path $inputPath -Root ([string]$_)
        }).Count -gt 0
        if (-not $allowed) {
            throw "INPUT_PATH_NOT_ALLOWED: $inputPath"
        }
    }

    $steps = [int]$Case.Steps
    $timesteps = [int]$Case.Timesteps
    $seed = [int]$Case.Seed
    $gpuLayers = [int]$Case.GpuLayers
    $cfg = [double]$Case.Cfg
    $temperature = [double]$Case.Temperature
    if ($steps -lt 1 -or $steps -gt 200) {
        throw 'STEPS_INVALID: steps must be between 1 and 200.'
    }
    if ($timesteps -lt 1 -or $timesteps -gt 100) {
        throw 'TIMESTEPS_INVALID: timesteps must be between 1 and 100.'
    }
    if (-not [double]::IsFinite($cfg) -or $cfg -le 0 -or
        -not [double]::IsFinite($temperature) -or $temperature -le 0) {
        throw 'SAMPLING_INVALID: cfg and temperature must be positive finite values.'
    }
    if ([bool]$Case.UseCpu -and $gpuLayers -ne 0) {
        throw 'CPU_GPU_LAYERS_INVALID: CPU mode requires --n-gpu-layers 0.'
    }
    if (-not [bool]$Case.UseCpu -and $gpuLayers -lt -1) {
        throw 'GPU_LAYERS_INVALID: GPU layers must be -1 or non-negative.'
    }

    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add('-t')
    $arguments.Add([string]$Case.Text)
    if ($hasPromptWav) {
        $arguments.Add('--prompt-wav')
        $arguments.Add([IO.Path]::GetFullPath([string]$Case.PromptWavPath))
        $arguments.Add('--prompt-text')
        $arguments.Add([string]$Case.PromptText)
    } else {
        $arguments.Add('-r')
        $arguments.Add([IO.Path]::GetFullPath([string]$Case.ReferencePath))
    }
    $arguments.Add('-o')
    $arguments.Add($outputPath)
    $arguments.Add('--steps')
    $arguments.Add($steps.ToString($invariant))
    $arguments.Add('--timesteps')
    $arguments.Add($timesteps.ToString($invariant))
    $arguments.Add('--cfg')
    $arguments.Add($cfg.ToString('0.0###############', $invariant))
    $arguments.Add('--temperature')
    $arguments.Add($temperature.ToString('0.0###############', $invariant))
    $arguments.Add('--seed')
    $arguments.Add($seed.ToString($invariant))
    if ([bool]$Case.UseCpu) {
        $arguments.Add('--cpu')
    }
    $arguments.Add('--n-gpu-layers')
    $arguments.Add($gpuLayers.ToString($invariant))
    $arguments.Add([IO.Path]::GetFullPath([string]$Case.BaseLmPath))
    $arguments.Add([IO.Path]::GetFullPath([string]$Case.AcousticPath))
    return [string[]]$arguments.ToArray()
}

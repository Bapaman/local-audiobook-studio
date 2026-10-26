[CmdletBinding()]
param(
    [string]$ModelRoot,
    [string]$OutputRoot,
    [string]$VenvPath,
    [string]$UpstreamRoot,
    [string]$BuildRoot,
    [string]$LockPath,
    [switch]$UpdateLock,
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force

$modelRevision = '32279effe8c19989596f05d353d1447f51d9e915'
$converterCommit = '873056743b74e1a4ce5dcf7290e2298428e214db'
$converterTag = 'b267'

if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
    $ModelRoot = Join-Path $env:LOCALAPPDATA "VoxBookStudio\spike\models\VoxCPM2-$($modelRevision.Substring(0, 12))"
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $env:LOCALAPPDATA "VoxBookStudio\spike\generated\VoxCPM2-$($modelRevision.Substring(0, 12))-$converterTag"
}
if ([string]::IsNullOrWhiteSpace($VenvPath)) {
    $VenvPath = Join-Path $repoRoot '.venv-spike'
}
if ([string]::IsNullOrWhiteSpace($UpstreamRoot)) {
    $UpstreamRoot = Join-Path $repoRoot ".tools\upstream\llama.cpp-omni-$converterTag"
}
if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $repoRoot 'eng\locks\voxcpm2.lock.json'
}

$modelFull = [System.IO.Path]::GetFullPath($ModelRoot)
$outputFull = [System.IO.Path]::GetFullPath($OutputRoot)
$stagingFull = "$outputFull.part"
$venvFull = [System.IO.Path]::GetFullPath($VenvPath)
$upstreamFull = [System.IO.Path]::GetFullPath($UpstreamRoot)
$lockFull = [System.IO.Path]::GetFullPath($LockPath)

if ($outputFull.EndsWith('.part', [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be a published path and cannot end in .part: $outputFull"
}

if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $buildPlan = & (Join-Path $PSScriptRoot 'Build-VoxCpmWorker.ps1') `
        -SourceRoot $upstreamFull `
        -PlanOnly
    $BuildRoot = $buildPlan.BaselineBuildRoot
}
$buildFull = [System.IO.Path]::GetFullPath($BuildRoot)
$converterScript = Join-Path $upstreamFull 'tools\omni\voxcpm2\convert_voxcpm2_to_gguf.py'
$quantizerPath = Join-Path $buildFull 'bin\llama-quantize.exe'
$buildManifestPath = Join-Path $buildFull 'voxcpm2-worker-manifest.json'
$pythonPath = Join-Path $venvFull 'Scripts\python.exe'
$metadataDumpScript = Join-Path $upstreamFull 'gguf-py\gguf\scripts\gguf_dump.py'

$expectedArtifacts = [string[]]@(
    'VoxCPM2-BaseLM-F16.gguf',
    'VoxCPM2-Acoustic-F16.gguf',
    'VoxCPM2-BaseLM-F32.gguf',
    'VoxCPM2-Acoustic-F32.gguf',
    'VoxCPM2-BaseLM-Q8_0.gguf'
)
$requiredBuildArtifacts = [string[]]@(
    'cublas64_12.dll',
    'cublasLt64_12.dll',
    'cudart64_12.dll',
    'ggml-base.dll',
    'ggml-cpu.dll',
    'ggml-cuda.dll',
    'ggml.dll',
    'llama-common.dll',
    'llama-quantize-impl.dll',
    'llama-quantize.exe',
    'llama.dll',
    'voxcpm2-cli.exe'
)
$verifiedSourceScopes = [string[]]@(
    'CMakeLists.txt',
    ':(glob)**/CMakeLists.txt',
    ':(glob)**/*.cmake',
    'cmake',
    'common',
    'ggml',
    'gguf-py',
    'include',
    'src',
    'tools/CMakeLists.txt',
    'tools/omni/CMakeLists.txt',
    'tools/omni/voxcpm2',
    'tools/quantize',
    'vendor'
)

$plan = [pscustomobject]@{
    ModelRoot          = $modelFull
    OutputRoot         = $outputFull
    StagingRoot        = $stagingFull
    VenvPath           = $venvFull
    UpstreamRoot       = $upstreamFull
    BuildRoot          = $buildFull
    ConverterScript    = $converterScript
    ConverterCommit    = $converterCommit
    ConversionDtypes   = [string[]]@('f16', 'f32')
    QuantizerPath      = $quantizerPath
    BuildManifestPath  = $buildManifestPath
    Quantization       = [pscustomobject]@{
        Type   = 'Q8_0'
        Source = 'VoxCPM2-BaseLM-F32.gguf'
        Output = 'VoxCPM2-BaseLM-Q8_0.gguf'
    }
    ExpectedArtifacts  = $expectedArtifacts
    LockPath           = $lockFull
    CanonicalLockIsAuthority = $true
    UpdateLock         = [bool]$UpdateLock
    ValidatesPinnedPythonEnvironment = $true
    ValidatesExistingGgufSemantics = $true
    ReadyMarkerCreatedBeforeAtomicPublish = $true
    ChecksDiskBeforeRemovingStaging = $true
    PreservesValidatedStagingArtifacts = $true
    FailureLeavesStagingForResume = $true
    RejectsUnexpectedStagingEntries = $true
    VerifiedSourceScopes = $verifiedSourceScopes
    RequiredBuildArtifacts = $requiredBuildArtifacts
    ForcesUtf8ChildProcesses = $true
    RequiresCleanFirstAuthoring = $true
    RevalidatesResumedBytesAgainstCanonicalLock = $true
}
if ($PlanOnly) {
    return $plan
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Child
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    $childFull = [System.IO.Path]::GetFullPath($Child)
    $rootPrefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $childFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside conversion output root: $childFull"
    }
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    $partialPath = "$Path.part"
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $partialPath -Encoding utf8NoBOM
    Move-Item -Force -LiteralPath $partialPath -Destination $Path
}

function Invoke-ConversionProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$Arguments = @(),

        [string]$WorkingDirectory = $repoRoot
    )

    $logRoot = Join-Path $repoRoot '.artifacts\model-conversion\logs'
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    $previousPythonUtf8 = [Environment]::GetEnvironmentVariable('PYTHONUTF8', 'Process')
    $previousPythonIoEncoding = [Environment]::GetEnvironmentVariable('PYTHONIOENCODING', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('PYTHONUTF8', '1', 'Process')
        [Environment]::SetEnvironmentVariable('PYTHONIOENCODING', 'utf-8', 'Process')
        $result = Invoke-CheckedProcess `
            -FilePath $FilePath `
            -ArgumentList $Arguments `
            -WorkingDirectory $WorkingDirectory
    } finally {
        [Environment]::SetEnvironmentVariable('PYTHONUTF8', $previousPythonUtf8, 'Process')
        [Environment]::SetEnvironmentVariable('PYTHONIOENCODING', $previousPythonIoEncoding, 'Process')
    }
    $result.StdOut | Set-Content -LiteralPath (Join-Path $logRoot "$Name.stdout.log") -Encoding utf8NoBOM
    $result.StdErr | Set-Content -LiteralPath (Join-Path $logRoot "$Name.stderr.log") -Encoding utf8NoBOM
    if ($result.ExitCode -ne 0) {
        throw "$Name failed with exit code $($result.ExitCode). See '$logRoot'.`n$($result.StdErr)"
    }
    return $result
}

function Get-GeneratedFileRecord {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $path = Join-Path $Root $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected generated artifact is missing: $path"
    }
    $item = Get-Item -LiteralPath $path
    if ([long]$item.Length -le 0) {
        throw "Generated artifact is empty: $path"
    }

    $role = if ($Name -like '*BaseLM*') { 'BaseLM' } else { 'Acoustic' }
    $format = if ($Name -like '*Q8_0*') { 'Q8_0' } elseif ($Name -like '*F32*') { 'F32' } else { 'F16' }
    [ordered]@{
        name   = $Name
        role   = $role
        format = $format
        length = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Assert-GeneratedManifest {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [object]$Manifest,

        [object]$ValidatedState
    )

    if ([int]$Manifest.schemaVersion -ne 1 -or
        [string]$Manifest.artifact -cne 'VoxCPM2-GGUF' -or
        [string]$Manifest.source.repository -cne 'openbmb/VoxCPM2' -or
        [string]$Manifest.source.revision -cne $modelRevision -or
        [string]$Manifest.converter.repository -cne 'https://github.com/tc-mb/llama.cpp-omni.git' -or
        [string]$Manifest.converter.tag -cne $converterTag -or
        [string]$Manifest.converter.commit -cne $converterCommit -or
        [string]$Manifest.converter.script -cne 'tools/omni/voxcpm2/convert_voxcpm2_to_gguf.py' -or
        [string]$Manifest.converter.sha256 -cne (Get-FileHash -LiteralPath $converterScript -Algorithm SHA256).Hash.ToLowerInvariant() -or
        [string]$Manifest.quantizer.name -cne 'llama-quantize.exe' -or
        [string]$Manifest.quantizer.sourceCommit -cne $converterCommit -or
        [string]$Manifest.quantizer.type -cne 'Q8_0' -or
        [string]$Manifest.quantizer.sha256 -cne (Get-FileHash -LiteralPath $quantizerPath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Generated manifest does not match the locked model and converter revisions.'
    }
    if (@($Manifest.files).Count -ne $expectedArtifacts.Count) {
        throw "Generated manifest must contain exactly $($expectedArtifacts.Count) files."
    }
    foreach ($name in $expectedArtifacts) {
        $record = @($Manifest.files | Where-Object { [string]$_.name -ceq $name })
        if ($record.Count -ne 1) {
            throw "Generated manifest must contain exactly one record for '$name'."
        }
        $verified = Assert-ArtifactHash `
            -Path (Join-Path $Root $name) `
            -Sha256 ([string]$record[0].sha256)
        if ([long]$verified.Length -ne [long]$record[0].length) {
            throw "Generated artifact length mismatch for '$name'."
        }
        $expectedRole = if ($name -like '*BaseLM*') { 'BaseLM' } else { 'Acoustic' }
        $expectedFormat = if ($name -like '*Q8_0*') { 'Q8_0' } elseif ($name -like '*F32*') { 'F32' } else { 'F16' }
        if ([string]$record[0].role -cne $expectedRole -or
            [string]$record[0].format -cne $expectedFormat) {
            throw "Generated manifest has the wrong role or format for '$name'."
        }

        if ($null -ne $ValidatedState) {
            $validatedRecord = @($ValidatedState.Files | Where-Object { [string]$_.name -ceq $name })
            if ($validatedRecord.Count -ne 1 -or
                [long]$validatedRecord[0].length -ne [long]$record[0].length -or
                [string]$validatedRecord[0].sha256 -cne [string]$record[0].sha256) {
                throw "Generated manifest does not match validated GGUF bytes for '$name'."
            }
            $manifestMetadata = if ($Manifest.metadata -is [System.Collections.IDictionary]) {
                $Manifest.metadata[$name]
            } else {
                $Manifest.metadata.PSObject.Properties[$name].Value
            }
            $validatedMetadata = $ValidatedState.Metadata[$name]
            if ($null -eq $manifestMetadata -or $null -eq $validatedMetadata) {
                throw "Generated manifest is missing validated metadata for '$name'."
            }
            if ([string]$manifestMetadata.architecture -cne [string]$validatedMetadata.architecture -or
                [int]$manifestMetadata.keyCount -ne [int]$validatedMetadata.keyCount -or
                [int]$manifestMetadata.tensorCount -ne [int]$validatedMetadata.tensorCount -or
                [int]$manifestMetadata.obsoleteVoxCpm2Keys -ne 0 -or
                (Compare-Object `
                    -ReferenceObject @($validatedMetadata.tensorTypes) `
                    -DifferenceObject @($manifestMetadata.tensorTypes) `
                    -CaseSensitive).Count -gt 0) {
                throw "Generated manifest metadata differs from the GGUF inspection for '$name'."
            }
        }
    }
}

function Get-ValidatedGgufState {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [string[]]$Names = $expectedArtifacts
    )

    if ($Names.Count -eq 0) {
        throw 'At least one GGUF artifact name is required for validation.'
    }
    $uniqueNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Names) {
        if ($name -cnotin $expectedArtifacts -or -not $uniqueNames.Add($name)) {
            throw "Unexpected or duplicate GGUF artifact requested for validation: '$name'."
        }
    }

    $metadataSummaries = [ordered]@{}
    foreach ($name in $Names) {
        $ggufPath = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $ggufPath -PathType Leaf)) {
            throw "Expected GGUF artifact is missing: $ggufPath"
        }
        $metadataResult = Invoke-ConversionProcess `
            -Name "metadata-$($name.Replace('.gguf', '').ToLowerInvariant())" `
            -FilePath $pythonPath `
            -Arguments @($metadataDumpScript, $ggufPath, '--json') `
            -WorkingDirectory $upstreamFull
        $metadata = $metadataResult.StdOut | ConvertFrom-Json -ErrorAction Stop
        $metadataKeys = [string[]]@($metadata.metadata.PSObject.Properties.Name)
        $obsoleteKeys = @($metadataKeys | Where-Object { $_ -match '^voxcpm2\.' })
        if ($obsoleteKeys.Count -gt 0) {
            throw "GGUF '$name' contains obsolete voxcpm2.* metadata: $($obsoleteKeys -join ', ')"
        }
        if ('general.architecture' -notin $metadataKeys) {
            throw "GGUF '$name' is missing general.architecture metadata."
        }
        $architecture = [string]$metadata.metadata.'general.architecture'.value
        $expectedArchitecture = if ($name -like '*BaseLM*') { 'minicpm4' } else { 'voxcpm-acoustic' }
        if ($architecture -cne $expectedArchitecture) {
            throw "GGUF '$name' has architecture '$architecture', expected '$expectedArchitecture'."
        }
        $tensorProperties = @($metadata.tensors.PSObject.Properties)
        if ($tensorProperties.Count -eq 0) {
            throw "GGUF '$name' contains no tensors."
        }
        $tensorTypes = [string[]]@($tensorProperties.Value.type | Sort-Object -Unique)
        if ($name -like '*Q8_0*' -and 'Q8_0' -notin $tensorTypes) {
            throw "Quantized GGUF '$name' contains no Q8_0 tensors."
        }
        if ($name -like '*F16*' -and 'F16' -notin $tensorTypes) {
            throw "F16 GGUF '$name' contains no F16 tensors."
        }
        if ($name -like '*F32*' -and @($tensorTypes | Where-Object { $_ -cne 'F32' }).Count -gt 0) {
            throw "F32 GGUF '$name' contains unexpected tensor types: $($tensorTypes -join ', ')."
        }
        $metadataSummaries[$name] = [ordered]@{
            architecture = $architecture
            keyCount = $metadataKeys.Count
            tensorCount = $tensorProperties.Count
            tensorTypes = $tensorTypes
            obsoleteVoxCpm2Keys = 0
        }
    }

    $fileRecords = @(
        foreach ($name in $Names) {
            Get-GeneratedFileRecord -Root $Root -Name $name
        }
    )
    if ('VoxCPM2-BaseLM-F16.gguf' -cin $Names -and
        'VoxCPM2-BaseLM-Q8_0.gguf' -cin $Names) {
        $f16Base = @($fileRecords | Where-Object { $_.name -ceq 'VoxCPM2-BaseLM-F16.gguf' })[0]
        $q8Base = @($fileRecords | Where-Object { $_.name -ceq 'VoxCPM2-BaseLM-Q8_0.gguf' })[0]
        if ([long]$q8Base.length -ge [long]$f16Base.length) {
            throw 'Q8_0 BaseLM must be smaller than F16 BaseLM.'
        }
    }

    return [pscustomobject]@{
        Metadata = $metadataSummaries
        Files    = [object[]]$fileRecords
    }
}

function Assert-LockSourceSet {
    param(
        [Parameter(Mandatory)]
        [object[]]$Expected,

        [Parameter(Mandatory)]
        [object[]]$Actual
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "Local model file count differs from the canonical lock: expected $($Expected.Count), got $($Actual.Count)."
    }
    foreach ($expectedFile in $Expected) {
        $matches = @($Actual | Where-Object { [string]$_.path -ceq [string]$expectedFile.path })
        if ($matches.Count -ne 1 -or
            [long]$matches[0].length -ne [long]$expectedFile.length -or
            [string]$matches[0].sha256 -cne [string]$expectedFile.sha256) {
            throw "Local model lock differs from the canonical record for '$($expectedFile.path)'."
        }
    }
}

function Assert-CanonicalGeneratedFiles {
    param(
        [Parameter(Mandatory)]
        [object]$CanonicalLock,

        [Parameter(Mandatory)]
        [object]$Manifest
    )

    $expectedGenerated = @($CanonicalLock.generated.files)
    if ($expectedGenerated.Count -eq 0) {
        return
    }
    if ($expectedGenerated.Count -ne $expectedArtifacts.Count) {
        throw "Canonical lock must contain exactly $($expectedArtifacts.Count) generated artifacts."
    }
    foreach ($expectedFile in $expectedGenerated) {
        $matches = @($Manifest.files | Where-Object { [string]$_.name -ceq [string]$expectedFile.name })
        if ($matches.Count -ne 1 -or
            [long]$matches[0].length -ne [long]$expectedFile.length -or
            [string]$matches[0].sha256 -cne [string]$expectedFile.sha256) {
            throw "Generated artifact '$($expectedFile.name)' differs from the canonical lock."
        }
    }
}

function Publish-GeneratedLock {
    param(
        [Parameter(Mandatory)]
        [object]$CanonicalLock,

        [Parameter(Mandatory)]
        [object]$Manifest
    )

    if (@($CanonicalLock.generated.files).Count -gt 0) {
        Assert-CanonicalGeneratedFiles -CanonicalLock $CanonicalLock -Manifest $Manifest
        return
    }
    if (-not $UpdateLock) {
        throw 'Canonical lock has no generated hashes. Only an explicit authoring run with -UpdateLock may populate them.'
    }

    $CanonicalLock.generated = [ordered]@{
        manifest = 'generated-manifest.json'
        files    = @($Manifest.files)
    }
    Write-JsonAtomically -Value $CanonicalLock -Path $lockFull
}

function Assert-PinnedConversionEnvironment {
    $probeCode = @'
import importlib.metadata as metadata
import json
import sys

print(json.dumps({
    "python": ".".join(str(v) for v in sys.version_info[:3]),
    "torch": metadata.version("torch"),
    "numpy": metadata.version("numpy"),
}, sort_keys=True))
'@
    $probe = Invoke-ConversionProcess `
        -Name 'verify-conversion-python' `
        -FilePath $pythonPath `
        -Arguments @('-c', $probeCode)
    $state = $probe.StdOut | ConvertFrom-Json -ErrorAction Stop
    if ([string]$state.python -cnotmatch '^3\.11\.' -or
        [string]$state.torch -cne '2.8.0+cpu' -or
        [string]$state.numpy -cne '2.2.6') {
        throw "Conversion environment is not pinned: python=$($state.python), torch=$($state.torch), numpy=$($state.numpy)."
    }
}

if (-not (Test-Path -LiteralPath $modelFull -PathType Container)) {
    throw "Published official VoxCPM2 model is missing: $modelFull"
}
$canonicalLock = Read-ArtifactLock `
    -Path $lockFull `
    -ExpectedRepository 'openbmb/VoxCPM2' `
    -ExpectedRevision $modelRevision `
    -ExpectedLicense 'Apache-2.0'
$isFirstAuthoring = @($canonicalLock.generated.files).Count -eq 0 -and [bool]$UpdateLock
if ($isFirstAuthoring) {
    Assert-CleanAuthoringTargets -OutputRoot $outputFull -StagingRoot $stagingFull
}
$sourceLockPath = Join-Path $modelFull 'artifact-lock.json'
$localSourceLock = Read-ArtifactLock `
    -Path $sourceLockPath `
    -ExpectedRepository 'openbmb/VoxCPM2' `
    -ExpectedRevision $modelRevision `
    -ExpectedLicense 'Apache-2.0' `
    -ArtifactRoot $modelFull
Assert-LockSourceSet -Expected @($canonicalLock.files) -Actual @($localSourceLock.files)
if ([string]$canonicalLock.converter.commit -cne $converterCommit -or
    [string]$localSourceLock.converter.commit -cne $converterCommit) {
    throw "Model locks must both reference converter commit '$converterCommit'."
}
foreach ($sourceFile in @($canonicalLock.files)) {
    $verifiedSource = Assert-ArtifactHash `
        -Path (Join-Path $modelFull ([string]$sourceFile.path)) `
        -Sha256 ([string]$sourceFile.sha256)
    if ([long]$verifiedSource.Length -ne [long]$sourceFile.length) {
        throw "Official source file length mismatch for '$($sourceFile.path)'."
    }
}

$upstreamLock = Read-ArtifactLock `
    -Path (Join-Path $upstreamFull 'artifact-lock.json') `
    -ExpectedRepository 'https://github.com/tc-mb/llama.cpp-omni.git' `
    -ExpectedTag $converterTag `
    -ExpectedLicense 'MIT' `
    -ArtifactRoot $upstreamFull
if ([string]$upstreamLock.commit -cne $converterCommit) {
    throw "llama.cpp-omni source has unexpected commit '$($upstreamLock.commit)'."
}

foreach ($requiredPath in @($pythonPath, $converterScript, $metadataDumpScript, $quantizerPath, $buildManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required conversion dependency is missing: $requiredPath"
    }
}
Assert-PinnedConversionEnvironment

$buildManifest = Get-Content -Raw -LiteralPath $buildManifestPath | ConvertFrom-Json -ErrorAction Stop
$expectedRuntimeDllNames = @('cudart64_12.dll', 'cublas64_12.dll', 'cublasLt64_12.dll')
$expectedBuildTargets = @('voxcpm2-cli', 'llama-quantize')
if ([int]$buildManifest.schemaVersion -ne 1 -or
    [string]$buildManifest.source.repository -cne 'https://github.com/tc-mb/llama.cpp-omni.git' -or
    [string]$buildManifest.source.commit -cne $converterCommit -or
    [string]$buildManifest.source.tag -cne $converterTag -or
    [string]$buildManifest.toolchain.msvcToolset -cne '14.38' -or
    [string]$buildManifest.toolchain.cl -cnotmatch '19\.38\.' -or
    [string]$buildManifest.toolchain.nvcc -cnotmatch 'release 12\.8' -or
    [string]$buildManifest.toolchain.generator -cne 'Ninja' -or
    [string]$buildManifest.toolchain.cudaArchitectures -cne '75' -or
    @($buildManifest.buildTargets).Count -ne $expectedBuildTargets.Count -or
    (Compare-Object -ReferenceObject $expectedBuildTargets -DifferenceObject @($buildManifest.buildTargets) -CaseSensitive).Count -gt 0 -or
    @($buildManifest.runtimeDllNames).Count -ne $expectedRuntimeDllNames.Count -or
    (Compare-Object -ReferenceObject $expectedRuntimeDllNames -DifferenceObject @($buildManifest.runtimeDllNames) -CaseSensitive).Count -gt 0) {
    throw 'Worker build manifest does not match the locked source, toolchain, targets and CUDA runtime contract.'
}
$builtFileRecords = @($buildManifest.files)
if ($builtFileRecords.Count -ne $requiredBuildArtifacts.Count -or
    (Compare-Object `
        -ReferenceObject $requiredBuildArtifacts `
        -DifferenceObject @($builtFileRecords.name) `
        -CaseSensitive).Count -gt 0) {
    throw 'Worker build manifest must contain the exact required executable and DLL closure.'
}
$builtNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($builtFile in @($buildManifest.files)) {
    $builtName = [string]$builtFile.name
    if ([string]::IsNullOrWhiteSpace($builtName) -or
        [System.IO.Path]::GetFileName($builtName) -cne $builtName -or
        -not $builtNames.Add($builtName) -or
        [long]$builtFile.length -le 0 -or
        [string]$builtFile.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Worker build manifest contains an unsafe, duplicate or unhashed file record: '$builtName'."
    }
    $builtPath = Join-Path (Join-Path $buildFull 'bin') ([string]$builtFile.name)
    $verifiedBuiltFile = Assert-ArtifactHash -Path $builtPath -Sha256 ([string]$builtFile.sha256)
    if ([long]$verifiedBuiltFile.Length -ne [long]$builtFile.length) {
        throw "Worker build artifact length mismatch for '$($builtFile.name)'."
    }
}

$gitPath = (Get-Command git -ErrorAction Stop).Source
$headResult = Invoke-CheckedProcess `
    -FilePath $gitPath `
    -ArgumentList @('-C', $upstreamFull, 'rev-parse', 'HEAD') `
    -WorkingDirectory $upstreamFull
if ($headResult.ExitCode -ne 0 -or $headResult.StdOut.Trim() -cne $converterCommit) {
    throw "Converter checkout HEAD is not the locked commit '$converterCommit'."
}
$statusResult = Invoke-CheckedProcess `
    -FilePath $gitPath `
    -ArgumentList (@(
        '-C', $upstreamFull,
        'status', '--porcelain', '--untracked-files=all', '--'
    ) + $verifiedSourceScopes) `
    -WorkingDirectory $upstreamFull
if ($statusResult.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($statusResult.StdOut)) {
    throw "Converter, quantizer or shared build dependency tree is dirty at the locked commit.`n$($statusResult.StdOut)$($statusResult.StdErr)"
}

if (Test-Path -LiteralPath $outputFull) {
    $readyPath = Join-Path $outputFull 'READY'
    $manifestPath = Join-Path $outputFull 'generated-manifest.json'
    if (-not (Test-Path -LiteralPath $readyPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Existing conversion output is incomplete: $outputFull"
    }
    Assert-ExactArtifactInventory `
        -Root $outputFull `
        -AllowedRelativePaths ($expectedArtifacts + @('generated-manifest.json', 'READY')) | Out-Null
    $existingManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -ErrorAction Stop
    $existingState = Get-ValidatedGgufState -Root $outputFull
    Assert-GeneratedManifest `
        -Root $outputFull `
        -Manifest $existingManifest `
        -ValidatedState $existingState
    Publish-GeneratedLock -CanonicalLock $canonicalLock -Manifest $existingManifest
    return [pscustomobject]@{
        OutputRoot   = $outputFull
        ManifestPath = $manifestPath
        Files        = @($existingManifest.files)
        Reused       = $true
    }
}

if (@($canonicalLock.generated.files).Count -eq 0 -and -not $UpdateLock) {
    throw 'Canonical lock has no generated hashes. Run this authoring conversion explicitly with -UpdateLock.'
}

$outputParent = Split-Path -Parent $outputFull
New-Item -ItemType Directory -Force -Path $outputParent | Out-Null
Assert-ChildPath -Root $outputParent -Child $outputFull
Assert-ChildPath -Root $outputParent -Child $stagingFull

if (Test-Path -LiteralPath $stagingFull -PathType Container) {
    foreach ($staleMetadataName in @('generated-manifest.json', 'generated-manifest.json.part', 'READY')) {
        $staleMetadataPath = Join-Path $stagingFull $staleMetadataName
        if (Test-Path -LiteralPath $staleMetadataPath) {
            Assert-ChildPath -Root $stagingFull -Child $staleMetadataPath
            Remove-Item -LiteralPath $staleMetadataPath -Force
        }
    }
    Assert-ExactArtifactInventory `
        -Root $stagingFull `
        -AllowedRelativePaths @(
            $expectedArtifacts | Where-Object {
                Test-Path -LiteralPath (Join-Path $stagingFull $_) -PathType Leaf
            }
        ) | Out-Null
}

$sourcePayloadBytes = [long](Get-Item -LiteralPath (Join-Path $modelFull 'model.safetensors')).Length +
    [long](Get-Item -LiteralPath (Join-Path $modelFull 'audiovae.pth')).Length
$requiredFreeBytes = [long]($sourcePayloadBytes * 4)
$driveInfo = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($outputFull))
$reclaimableStagingBytes = 0L
if (Test-Path -LiteralPath $stagingFull -PathType Container) {
    $reclaimableStagingBytes = [long]((
        Get-ChildItem -LiteralPath $stagingFull -File -Recurse -Force -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum
    ).Sum)
}
$availableAfterStagingCleanup = [long]$driveInfo.AvailableFreeSpace + $reclaimableStagingBytes
if ($availableAfterStagingCleanup -lt $requiredFreeBytes) {
    throw "Insufficient disk space for F16, F32 and Q8_0 conversion: need at least $requiredFreeBytes bytes after staging cleanup, have $availableAfterStagingCleanup."
}

try {
    New-Item -ItemType Directory -Force -Path $stagingFull | Out-Null

    $regeneratedDtypes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($dtype in @('f16', 'f32')) {
        $formatName = $dtype.ToUpperInvariant()
        $dtypeArtifacts = [string[]]@(
            "VoxCPM2-BaseLM-$formatName.gguf",
            "VoxCPM2-Acoustic-$formatName.gguf"
        )
        $presentDtypeArtifacts = @(
            $dtypeArtifacts | Where-Object {
                Test-Path -LiteralPath (Join-Path $stagingFull $_) -PathType Leaf
            }
        )
        $reuseDtype = $false
        if ($presentDtypeArtifacts.Count -eq $dtypeArtifacts.Count) {
            $canonicalBytesMatch = (
                @($canonicalLock.generated.files).Count -eq 0 -or
                (Test-LockedArtifactSet `
                    -Root $stagingFull `
                    -Records @($canonicalLock.generated.files) `
                    -Names $dtypeArtifacts)
            )
            if ($canonicalBytesMatch) {
                try {
                    $null = Get-ValidatedGgufState -Root $stagingFull -Names $dtypeArtifacts
                    $reuseDtype = $true
                } catch {
                    Write-Warning "Staged $formatName artifacts failed validation and will be regenerated: $($_.Exception.Message)"
                }
            } else {
                Write-Warning "Staged $formatName artifacts differ from the canonical hashes and will be regenerated."
            }
        }
        if (-not $reuseDtype) {
            foreach ($name in $dtypeArtifacts) {
                $path = Join-Path $stagingFull $name
                if (Test-Path -LiteralPath $path) {
                    Assert-ChildPath -Root $stagingFull -Child $path
                    Remove-Item -LiteralPath $path -Force
                }
            }
            $null = Invoke-ConversionProcess `
                -Name "convert-$dtype" `
                -FilePath $pythonPath `
                -Arguments @(
                    $converterScript,
                    '--model', (Join-Path $modelFull 'model.safetensors'),
                    '--vae', (Join-Path $modelFull 'audiovae.pth'),
                    '--config', (Join-Path $modelFull 'config.json'),
                    '--tokenizer-dir', $modelFull,
                    '--output', $stagingFull,
                    '--dtype', $dtype
                ) `
                -WorkingDirectory $upstreamFull
            $null = Get-ValidatedGgufState -Root $stagingFull -Names $dtypeArtifacts
            $null = $regeneratedDtypes.Add($dtype)
        }
    }

    $q8Name = 'VoxCPM2-BaseLM-Q8_0.gguf'
    $q8Path = Join-Path $stagingFull $q8Name
    $reuseQ8 = $false
    if (-not $regeneratedDtypes.Contains('f32') -and
        (Test-Path -LiteralPath $q8Path -PathType Leaf)) {
        $canonicalQ8Matches = (
            @($canonicalLock.generated.files).Count -eq 0 -or
            (Test-LockedArtifactSet `
                -Root $stagingFull `
                -Records @($canonicalLock.generated.files) `
                -Names @($q8Name))
        )
        if ($canonicalQ8Matches) {
            try {
                $null = Get-ValidatedGgufState -Root $stagingFull -Names @($q8Name)
                $reuseQ8 = $true
            } catch {
                Write-Warning "Staged Q8_0 artifact failed validation and will be regenerated: $($_.Exception.Message)"
            }
        } else {
            Write-Warning 'Staged Q8_0 artifact differs from the canonical hash and will be regenerated.'
        }
    }
    if (-not $reuseQ8) {
        if (Test-Path -LiteralPath $q8Path) {
            Assert-ChildPath -Root $stagingFull -Child $q8Path
            Remove-Item -LiteralPath $q8Path -Force
        }
        $null = Invoke-ConversionProcess `
            -Name 'quantize-baselm-q8_0' `
            -FilePath $quantizerPath `
            -Arguments @(
                (Join-Path $stagingFull 'VoxCPM2-BaseLM-F32.gguf'),
                $q8Path,
                'Q8_0'
            ) `
            -WorkingDirectory (Split-Path -Parent $quantizerPath)
        $null = Get-ValidatedGgufState -Root $stagingFull -Names @($q8Name)
    }

    $validatedState = Get-ValidatedGgufState -Root $stagingFull
    $metadataSummaries = $validatedState.Metadata
    $fileRecords = @($validatedState.Files)

    $manifest = [ordered]@{
        schemaVersion = 1
        artifact      = 'VoxCPM2-GGUF'
        source        = [ordered]@{
            repository = 'openbmb/VoxCPM2'
            revision   = $modelRevision
        }
        converter     = [ordered]@{
            repository = 'https://github.com/tc-mb/llama.cpp-omni.git'
            tag        = $converterTag
            commit     = $converterCommit
            script     = 'tools/omni/voxcpm2/convert_voxcpm2_to_gguf.py'
            sha256     = (Get-FileHash -LiteralPath $converterScript -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        quantizer     = [ordered]@{
            name       = 'llama-quantize.exe'
            sourceCommit = $converterCommit
            sha256     = (Get-FileHash -LiteralPath $quantizerPath -Algorithm SHA256).Hash.ToLowerInvariant()
            type       = 'Q8_0'
        }
        metadata      = $metadataSummaries
        files         = $fileRecords
    }
    Assert-GeneratedManifest `
        -Root $stagingFull `
        -Manifest ([pscustomobject]$manifest) `
        -ValidatedState $validatedState
    if (@($canonicalLock.generated.files).Count -gt 0) {
        Assert-CanonicalGeneratedFiles -CanonicalLock $canonicalLock -Manifest $manifest
    }
    Write-JsonAtomically -Value $manifest -Path (Join-Path $stagingFull 'generated-manifest.json')
    New-Item -ItemType File -Force -Path (Join-Path $stagingFull 'READY') | Out-Null
    Assert-ExactArtifactInventory `
        -Root $stagingFull `
        -AllowedRelativePaths ($expectedArtifacts + @('generated-manifest.json', 'READY')) | Out-Null
    Move-Item -LiteralPath $stagingFull -Destination $outputFull
    Publish-GeneratedLock -CanonicalLock $canonicalLock -Manifest $manifest

    [pscustomobject]@{
        OutputRoot   = $outputFull
        ManifestPath = Join-Path $outputFull 'generated-manifest.json'
        Files        = $fileRecords
        Reused       = $false
    }
} catch {
    # Keep validated or partially completed GGUF files in the revision-specific
    # .part directory. The next invocation validates each complete phase before
    # reusing it and regenerates only an incomplete or invalid phase.
    throw
}

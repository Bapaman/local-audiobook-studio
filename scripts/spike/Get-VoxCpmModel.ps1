[CmdletBinding()]
param(
    [string]$Repository = 'openbmb/VoxCPM2',
    [string]$Revision = '32279effe8c19989596f05d353d1447f51d9e915',
    [ValidateSet('HuggingFace', 'ModelScope')]
    [string]$Transport = 'HuggingFace',
    [string]$DestinationRoot,
    [string]$VenvPath,
    [string]$LockPath,
    [int]$MaxWorkers = 4,
    [switch]$UpdateLock,
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force

$Transport = if ($Transport -ieq 'ModelScope') { 'ModelScope' } else { 'HuggingFace' }
$pinnedRevision = '32279effe8c19989596f05d353d1447f51d9e915'
if ($Repository -cne 'openbmb/VoxCPM2') {
    throw "Only the official model repository openbmb/VoxCPM2 is accepted, got '$Repository'."
}
if ($Revision -cnotmatch '^[0-9a-f]{40}$') {
    throw "Revision must contain exactly 40 lowercase hexadecimal characters, got '$Revision'."
}
if ($Revision -cne $pinnedRevision) {
    throw "This spike accepts only the pinned VoxCPM2 revision $pinnedRevision, got '$Revision'."
}
if ($MaxWorkers -lt 1 -or $MaxWorkers -gt 8) {
    throw 'MaxWorkers must be between 1 and 8.'
}

if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path $env:LOCALAPPDATA 'VoxBookStudio\spike\models'
}
if ([string]::IsNullOrWhiteSpace($VenvPath)) {
    $VenvPath = Join-Path $repoRoot '.venv-spike'
}
if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $repoRoot 'eng\locks\voxcpm2.lock.json'
}

$huggingFaceHubVersion = '0.36.0'
$modelScopeRepository = 'OpenBMB/VoxCPM2'
$modelScopeRevision = '519da200db2bc31a0395635fc7042653b9c4c862'
$modelScopeHubVersion = '0.4.5'
$modelScopeExcludedFiles = @('.gitattributes')
$modelScopeTransferFiles = @('audiovae.pth', 'model.safetensors')
$modelScopeRangeWorkers = 4
$downloadHeadroomBytes = 1GB
$pythonPackages = @(
    'torch==2.8.0',
    "huggingface_hub==$huggingFaceHubVersion",
    'hf-xet==1.6.0',
    'numpy==2.2.6',
    'tqdm==4.67.1',
    'PyYAML==6.0.2',
    'requests==2.32.5'
)
$modelScopePackages = @(
    "modelscope-hub==$modelScopeHubVersion"
)
$destinationRootFull = [System.IO.Path]::GetFullPath($DestinationRoot)
$venvFull = [System.IO.Path]::GetFullPath($VenvPath)
$lockFull = [System.IO.Path]::GetFullPath($LockPath)
$publishedRoot = Join-Path $destinationRootFull "VoxCPM2-$($Revision.Substring(0, 12))"
$stagingRoot = "$publishedRoot.part"

$plan = [pscustomobject]@{
    Repository                = $Repository
    Revision                  = $Revision
    License                   = 'Apache-2.0'
    Transport                 = $Transport
    AvailableTransports       = @('HuggingFace', 'ModelScope')
    HuggingFaceHubVersion     = $huggingFaceHubVersion
    PythonPackages            = [string[]]$pythonPackages
    ModelScopeRepository      = $modelScopeRepository
    ModelScopeRevision        = $modelScopeRevision
    ModelScopeHubVersion      = $modelScopeHubVersion
    ModelScopePackages        = [string[]]$modelScopePackages
    ModelScopeExcludedFiles   = [string[]]$modelScopeExcludedFiles
    ModelScopeTransferFiles   = [string[]]$modelScopeTransferFiles
    ModelScopePartSizeMB      = 160
    ModelScopeRangeWorkers    = $modelScopeRangeWorkers
    ModelScopeTimeoutSeconds  = 600
    ModelScopeMaxRetries      = 20
    ModelScopePerFileHashVerification = $true
    CanonicalHashAuthority    = 'HuggingFaceRevision'
    CanonicalHashesRequired   = $true
    ReadyMarkerCreatedBeforeAtomicPublish = $true
    VenvPath                  = $venvFull
    PublishedRoot             = $publishedRoot
    StagingRoot               = $stagingRoot
    LockPath                  = $lockFull
    MaxWorkers                = $MaxWorkers
    PreservePartialDownloads = $true
    CanonicalLockIsAuthority = $true
    UpdateLock                = [bool]$UpdateLock
    ValidatesPinnedEnvironmentBeforeNetwork = $true
    ValidatesHuggingFaceGitBlobIds = $true
    DownloadHeadroomBytes     = $downloadHeadroomBytes
    CreditsOnlyVerifiedStagingFiles = $true
    AccountsForModelScopeMergeBuffer = $true
    UsesXetDownloads          = $true
    CleansKnownTransportPartFiles = $true
    RejectsUnexpectedPublishedEntries = $true
    ReusesVerifiedArtifactBeforePythonSetup = $true
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
        throw "Refusing to modify path outside model destination root: $childFull"
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
    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $partialPath -Encoding utf8NoBOM
    Move-Item -Force -LiteralPath $partialPath -Destination $Path
}

function Invoke-ModelProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$Arguments = @(),

        [string]$WorkingDirectory = $repoRoot
    )

    $logRoot = Join-Path $repoRoot '.artifacts\model-acquisition\logs'
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    $result = Invoke-CheckedProcess `
        -FilePath $FilePath `
        -ArgumentList $Arguments `
        -WorkingDirectory $WorkingDirectory
    $result.StdOut | Set-Content -LiteralPath (Join-Path $logRoot "$Name.stdout.log") -Encoding utf8NoBOM
    $result.StdErr | Set-Content -LiteralPath (Join-Path $logRoot "$Name.stderr.log") -Encoding utf8NoBOM
    if ($result.ExitCode -ne 0) {
        throw "$Name failed with exit code $($result.ExitCode). See '$logRoot'.`n$($result.StdErr)"
    }
    return $result
}

function Assert-LockedSourceFiles {
    param(
        [Parameter(Mandatory)]
        [string]$ModelRoot,

        [Parameter(Mandatory)]
        [object[]]$Files
    )

    foreach ($file in $Files) {
        $filePath = Join-Path $ModelRoot ([string]$file.path)
        $verified = Assert-ArtifactHash -Path $filePath -Sha256 ([string]$file.sha256)
        if ([long]$verified.Length -ne [long]$file.length) {
            throw "Model file length mismatch for '$filePath': expected $($file.length), got $($verified.Length)."
        }
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
        throw "Official model file count differs from the canonical lock: expected $($Expected.Count), got $($Actual.Count)."
    }
    foreach ($expectedFile in $Expected) {
        $matches = @($Actual | Where-Object { [string]$_.path -ceq [string]$expectedFile.path })
        if ($matches.Count -ne 1 -or
            [long]$matches[0].length -ne [long]$expectedFile.length -or
            [string]$matches[0].sha256 -cne [string]$expectedFile.sha256) {
            throw "Official model file '$($expectedFile.path)' differs from the canonical lock."
        }
    }
}

function Get-GitBlobId {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path
    $prefix = [System.Text.Encoding]::ASCII.GetBytes("blob $($item.Length)`0")
    $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA1
    )
    $stream = $null
    try {
        $hasher.AppendData($prefix)
        $stream = [System.IO.File]::OpenRead($item.FullName)
        $buffer = [byte[]]::new(1MB)
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $hasher.AppendData($buffer, 0, $read)
        }
        return [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
    } finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        $hasher.Dispose()
    }
}

function Remove-KnownModelTransportArtifacts {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    foreach ($cacheName in @('.cache', '._____temp', '.parallel_tmp')) {
        $cachePath = Join-Path $Root $cacheName
        if (Test-Path -LiteralPath $cachePath) {
            Assert-ChildPath -Root $Root -Child $cachePath
            Remove-Item -LiteralPath $cachePath -Recurse -Force
        }
    }

    foreach ($transferFile in $modelScopeTransferFiles) {
        $partPattern = '^' + [regex]::Escape($transferFile) + '_\d+_\d+$'
        foreach ($partFile in @(
            Get-ChildItem -LiteralPath $Root -File -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match $partPattern }
        )) {
            Assert-ChildPath -Root $Root -Child $partFile.FullName
            Remove-Item -LiteralPath $partFile.FullName -Force
        }
    }
}

$canonicalLock = $null
if (Test-Path -LiteralPath $lockFull -PathType Leaf) {
    $canonicalLock = Read-ArtifactLock `
        -Path $lockFull `
        -ExpectedRepository $Repository `
        -ExpectedRevision $Revision `
        -ExpectedLicense 'Apache-2.0'
} elseif (-not $UpdateLock) {
    throw "Canonical model lock is missing: $lockFull. Only an explicit authoring run with -UpdateLock may create it."
}

New-Item -ItemType Directory -Force -Path $destinationRootFull | Out-Null
Assert-ChildPath -Root $destinationRootFull -Child $publishedRoot
Assert-ChildPath -Root $destinationRootFull -Child $stagingRoot

$publishedLockPath = Join-Path $publishedRoot 'artifact-lock.json'
$readyPath = Join-Path $publishedRoot 'READY'
$reuseResult = $null
if (Test-Path -LiteralPath $publishedRoot) {
    if (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
        throw "Existing model artifact is incomplete because READY is missing: $publishedRoot"
    }
    if ($null -eq $canonicalLock) {
        throw "Cannot author the canonical lock from an existing local artifact: $publishedRoot"
    }
    $localLock = Read-ArtifactLock `
        -Path $publishedLockPath `
        -ExpectedRepository $Repository `
        -ExpectedRevision $Revision `
        -ExpectedLicense 'Apache-2.0' `
        -ArtifactRoot $publishedRoot
    Assert-LockSourceSet -Expected @($canonicalLock.files) -Actual @($localLock.files)
    Assert-LockedSourceFiles -ModelRoot $publishedRoot -Files @($canonicalLock.files)
    Assert-ExactArtifactInventory `
        -Root $publishedRoot `
        -AllowedRelativePaths (@($canonicalLock.files.path) + @('artifact-lock.json', 'READY')) | Out-Null
    $reuseResult = [pscustomobject]@{
        ModelRoot = $publishedRoot
        LockPath  = $lockFull
        Revision  = $Revision
        Reused    = $true
    }
}

if ($null -ne $reuseResult) {
    return $reuseResult
}

$pyLauncher = Get-Command py -ErrorAction Stop
$venvPython = Join-Path $venvFull 'Scripts\python.exe'
$requirementsMarker = Join-Path $venvFull '.voxcpm2-conversion-requirements-v1.ready'
$freezePath = Join-Path $venvFull 'requirements.freeze.txt'

function Get-PinnedEnvironmentState {
    if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
        return $null
    }
    $probeCode = @'
import importlib.metadata as metadata
import json
import sys

packages = {
    "pip": "pip",
    "torch": "torch",
    "huggingfaceHub": "huggingface_hub",
    "hfXet": "hf-xet",
    "numpy": "numpy",
    "tqdm": "tqdm",
    "pyyaml": "PyYAML",
    "requests": "requests",
    "modelscopeHub": "modelscope-hub",
}
state = {"python": ".".join(str(v) for v in sys.version_info[:3])}
for key, distribution in packages.items():
    try:
        state[key] = metadata.version(distribution)
    except metadata.PackageNotFoundError:
        state[key] = None
print(json.dumps(state, sort_keys=True))
'@
    $result = Invoke-CheckedProcess `
        -FilePath $venvPython `
        -ArgumentList @('-c', $probeCode) `
        -WorkingDirectory $repoRoot
    if ($result.ExitCode -ne 0) {
        return $null
    }
    try {
        return $result.StdOut | ConvertFrom-Json -ErrorAction Stop
    } catch {
        return $null
    }
}

function Test-PinnedEnvironmentState {
    param([object]$State)

    if ($null -eq $State) {
        return $false
    }
    $commonEnvironmentMatches = (
        [string]$State.python -match '^3\.11\.' -and
        [string]$State.pip -ceq '25.2' -and
        [string]$State.torch -ceq '2.8.0+cpu' -and
        [string]$State.huggingFaceHub -ceq $huggingFaceHubVersion -and
        [string]$State.hfXet -ceq '1.6.0' -and
        [string]$State.numpy -ceq '2.2.6' -and
        [string]$State.tqdm -ceq '4.67.1' -and
        [string]$State.pyyaml -ceq '6.0.2' -and
        [string]$State.requests -ceq '2.32.5'
    )
    if (-not $commonEnvironmentMatches) {
        return $false
    }
    if ($Transport -ceq 'ModelScope') {
        return [string]$State.modelscopeHub -ceq $modelScopeHubVersion
    }
    return $true
}

if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    $venvParent = Split-Path -Parent $venvFull
    New-Item -ItemType Directory -Force -Path $venvParent | Out-Null
    $null = Invoke-ModelProcess `
        -Name 'create-venv' `
        -FilePath $pyLauncher.Source `
        -Arguments @('-3.11', '-m', 'venv', $venvFull)
}

$pythonVersionResult = Invoke-CheckedProcess `
    -FilePath $venvPython `
    -ArgumentList @('-c', 'import sys; print(".".join(str(v) for v in sys.version_info[:3]))') `
    -WorkingDirectory $repoRoot
if ($pythonVersionResult.ExitCode -ne 0 -or $pythonVersionResult.StdOut.Trim() -notmatch '^3\.11\.') {
    throw "Spike virtual environment must use Python 3.11, got '$($pythonVersionResult.StdOut.Trim())': $venvFull"
}

$environmentState = Get-PinnedEnvironmentState
$environmentReady = (
    (Test-Path -LiteralPath $requirementsMarker -PathType Leaf) -and
    (Test-Path -LiteralPath $freezePath -PathType Leaf) -and
    (Test-PinnedEnvironmentState -State $environmentState)
)
if (-not $environmentReady) {
    $null = Invoke-ModelProcess `
        -Name 'upgrade-pip' `
        -FilePath $venvPython `
        -Arguments @('-m', 'pip', 'install', '--disable-pip-version-check', 'pip==25.2')
    $null = Invoke-ModelProcess `
        -Name 'install-torch-cpu' `
        -FilePath $venvPython `
        -Arguments @(
            '-m', 'pip', 'install', '--disable-pip-version-check',
            '--index-url', 'https://download.pytorch.org/whl/cpu',
            'torch==2.8.0'
        )
    $null = Invoke-ModelProcess `
        -Name 'install-model-tools' `
        -FilePath $venvPython `
        -Arguments @(
            '-m', 'pip', 'install', '--disable-pip-version-check',
            "huggingface_hub==$huggingFaceHubVersion",
            'hf-xet==1.6.0',
            'numpy==2.2.6',
            'tqdm==4.67.1',
            'PyYAML==6.0.2',
            'requests==2.32.5'
        )
    if ($Transport -ceq 'ModelScope') {
        $null = Invoke-ModelProcess `
            -Name 'install-modelscope-transport' `
            -FilePath $venvPython `
            -Arguments @(
                '-m', 'pip', 'install', '--disable-pip-version-check',
                "modelscope-hub==$modelScopeHubVersion",
                'requests==2.32.5',
                'tqdm==4.67.1'
            )
    }
    $freezeResult = Invoke-ModelProcess `
        -Name 'pip-freeze' `
        -FilePath $venvPython `
        -Arguments @('-m', 'pip', 'freeze', '--all')
    $freezeResult.StdOut | Set-Content -LiteralPath $freezePath -Encoding utf8NoBOM
    New-Item -ItemType File -Force -Path $requirementsMarker | Out-Null
}

$validatedEnvironment = Get-PinnedEnvironmentState
if (-not (Test-PinnedEnvironmentState -State $validatedEnvironment) -or
    -not (Test-Path -LiteralPath $freezePath -PathType Leaf)) {
    throw "Spike virtual environment does not match the pinned conversion package set: $venvFull"
}
$verifiedFreezeResult = Invoke-ModelProcess `
    -Name 'verify-pip-freeze' `
    -FilePath $venvPython `
    -Arguments @('-m', 'pip', 'freeze', '--all')
$verifiedFreezeResult.StdOut | Set-Content -LiteralPath $freezePath -Encoding utf8NoBOM

$metadataUri = "https://huggingface.co/api/models/$Repository/revision/$Revision`?blobs=true"
$metadata = Invoke-RestMethod -Uri $metadataUri -Method Get
if ([string]$metadata.id -cne $Repository -or [string]$metadata.sha -cne $Revision) {
    throw "Hugging Face metadata did not resolve to the locked repository and revision: $metadataUri"
}
if ([string]$metadata.cardData.license -cne 'apache-2.0') {
    throw "Expected Hugging Face license apache-2.0, got '$($metadata.cardData.license)'."
}
if (@($metadata.siblings).Count -eq 0) {
    throw 'Hugging Face metadata did not include any model files.'
}

$expectedMetadataPaths = @(
    '.gitattributes',
    'README.md',
    'audiovae.pth',
    'config.json',
    'model.safetensors',
    'special_tokens_map.json',
    'tokenization_voxcpm2.py',
    'tokenizer.json',
    'tokenizer_config.json'
)
$metadataPaths = @($metadata.siblings.rfilename)
if ($metadataPaths.Count -ne $expectedMetadataPaths.Count -or
    (Compare-Object `
        -ReferenceObject $expectedMetadataPaths `
        -DifferenceObject $metadataPaths `
        -CaseSensitive).Count -gt 0) {
    throw 'Hugging Face metadata does not contain the exact canonical nine-file VoxCPM2 snapshot.'
}
if ($null -ne $canonicalLock) {
    foreach ($lockedFile in @($canonicalLock.files)) {
        $remote = @($metadata.siblings | Where-Object { [string]$_.rfilename -ceq [string]$lockedFile.path })
        if ($remote.Count -ne 1 -or [long]$remote[0].size -ne [long]$lockedFile.length) {
            throw "Hugging Face metadata differs from the canonical lock for '$($lockedFile.path)'."
        }
        if ($null -ne $remote[0].lfs -and
            -not [string]::IsNullOrWhiteSpace([string]$remote[0].lfs.sha256) -and
            [string]$remote[0].lfs.sha256 -cne [string]$lockedFile.sha256) {
            throw "Hugging Face LFS hash differs from the canonical lock for '$($lockedFile.path)'."
        }
    }
}

$modelScopeMetadata = $null
if ($Transport -ceq 'ModelScope') {
    $modelScopeInfoUri = "https://modelscope.cn/api/v1/models/$modelScopeRepository"
    $modelScopeInfo = Invoke-RestMethod -Uri $modelScopeInfoUri -Method Get
    if ([int]$modelScopeInfo.Code -ne 200 -or
        [string]$modelScopeInfo.Data.Path -cne 'OpenBMB' -or
        [string]$modelScopeInfo.Data.Name -cne 'VoxCPM2' -or
        [string]$modelScopeInfo.Data.License -cne 'apache-2.0') {
        throw "ModelScope metadata did not resolve to the official Apache-2.0 OpenBMB/VoxCPM2 model: $modelScopeInfoUri"
    }

    $modelScopeFilesUri = "https://modelscope.cn/api/v1/models/$modelScopeRepository/repo/files?Revision=$modelScopeRevision&Root="
    $modelScopeResponse = Invoke-RestMethod -Uri $modelScopeFilesUri -Method Get
    if ([int]$modelScopeResponse.Code -ne 200 -or @($modelScopeResponse.Data.Files).Count -eq 0) {
        throw "ModelScope did not resolve the pinned transport revision: $modelScopeRevision"
    }
    $modelScopeMetadata = @($modelScopeResponse.Data.Files)
    $modelScopePaths = @($modelScopeMetadata.Path)
    if ($modelScopePaths.Count -ne $expectedMetadataPaths.Count -or
        (Compare-Object `
            -ReferenceObject $expectedMetadataPaths `
            -DifferenceObject $modelScopePaths `
            -CaseSensitive).Count -gt 0) {
        throw 'The pinned ModelScope transport snapshot does not contain the expected nine-file VoxCPM2 source set.'
    }

    foreach ($transferPath in $modelScopeTransferFiles) {
        if ($transferPath -in $modelScopeExcludedFiles) {
            throw "ModelScope transport configuration includes excluded canonical file '$transferPath'."
        }
        $hfFile = @($metadata.siblings | Where-Object { [string]$_.rfilename -ceq $transferPath })
        $msFile = @($modelScopeMetadata | Where-Object { [string]$_.Path -ceq $transferPath })
        if ($hfFile.Count -ne 1 -or $msFile.Count -ne 1 -or
            $null -eq $hfFile[0].lfs -or
            [string]::IsNullOrWhiteSpace([string]$hfFile[0].lfs.sha256) -or
            [long]$msFile[0].Size -ne [long]$hfFile[0].size -or
            [string]$msFile[0].Sha256 -cne [string]$hfFile[0].lfs.sha256) {
            throw "ModelScope transport bytes for '$transferPath' do not match the pinned Hugging Face revision."
        }
    }

    if ($null -ne $canonicalLock) {
        foreach ($lockedFile in @($canonicalLock.files | Where-Object { [string]$_.path -notin $modelScopeExcludedFiles })) {
            $msFile = @($modelScopeMetadata | Where-Object { [string]$_.Path -ceq [string]$lockedFile.path })
            if ($msFile.Count -ne 1 -or
                [long]$msFile[0].Size -ne [long]$lockedFile.length -or
                [string]$msFile[0].Sha256 -cne [string]$lockedFile.sha256) {
                throw "Pinned ModelScope snapshot differs from the canonical lock for '$($lockedFile.path)'."
            }
        }
    }
}

$snapshotBytes = [long](($metadata.siblings | Measure-Object -Property size -Sum).Sum)
$existingStagingBytes = 0L
if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
    foreach ($sibling in @($metadata.siblings)) {
        $relativePath = [string]$sibling.rfilename
        $candidatePath = Join-Path $stagingRoot $relativePath
        if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
            continue
        }
        $candidate = Get-Item -LiteralPath $candidatePath
        if ([long]$candidate.Length -ne [long]$sibling.size) {
            continue
        }

        $expectedHash = $null
        if ($null -ne $canonicalLock) {
            $lockedCandidate = @($canonicalLock.files | Where-Object { [string]$_.path -ceq $relativePath })
            if ($lockedCandidate.Count -eq 1) {
                $expectedHash = [string]$lockedCandidate[0].sha256
            }
        } elseif ($null -ne $sibling.lfs -and
            -not [string]::IsNullOrWhiteSpace([string]$sibling.lfs.sha256)) {
            $expectedHash = [string]$sibling.lfs.sha256
        }

        if ($expectedHash -cmatch '^[0-9a-f]{64}$') {
            $actualHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ceq $expectedHash) {
                $existingStagingBytes += [long]$candidate.Length
            }
        }
    }
}
$destinationDrive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($destinationRootFull))
$availableWithResume = [long]$destinationDrive.AvailableFreeSpace + $existingStagingBytes
$modelScopeMergeBufferBytes = 0L
if ($Transport -ceq 'ModelScope') {
    $largestModelScopeTransfer = (
        $modelScopeMetadata |
            Where-Object { [string]$_.Path -in $modelScopeTransferFiles } |
            Measure-Object -Property Size -Maximum
    )
    $modelScopeMergeBufferBytes = [long]$largestModelScopeTransfer.Maximum
}
$requiredDownloadCapacity = $snapshotBytes + $modelScopeMergeBufferBytes + $downloadHeadroomBytes
if ($availableWithResume -lt $requiredDownloadCapacity) {
    throw "Insufficient disk space for the official VoxCPM2 snapshot and transport merge buffer: need $requiredDownloadCapacity bytes including headroom, have $availableWithResume."
}

try {
    $env:HF_HUB_DISABLE_XET = '0'
    $env:HF_HUB_DISABLE_SYMLINKS_WARNING = '1'
    $env:HF_HUB_DOWNLOAD_TIMEOUT = '600'
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

    $hfPath = Join-Path $venvFull 'Scripts\hf.exe'
    if (-not (Test-Path -LiteralPath $hfPath -PathType Leaf)) {
        $hfPath = Join-Path $venvFull 'Scripts\huggingface-cli.exe'
    }
    if (-not (Test-Path -LiteralPath $hfPath -PathType Leaf)) {
        throw "Hugging Face CLI was not installed in the spike virtual environment: $venvFull"
    }

    if ($Transport -ceq 'HuggingFace') {
        $null = Invoke-ModelProcess `
            -Name 'download-voxcpm2-huggingface' `
            -FilePath $hfPath `
            -Arguments @(
                'download', $Repository,
                '--revision', $Revision,
                '--local-dir', $stagingRoot,
                '--max-workers', [string]$MaxWorkers
            )
    } else {
        # ModelScope is used only for the two multi-gigabyte LFS payloads. The
        # remaining files come from the exact Hugging Face revision; notably,
        # ModelScope's .gitattributes intentionally differs from that revision.
        $huggingFaceBootstrapFiles = @(
            $expectedMetadataPaths | Where-Object { $_ -notin $modelScopeTransferFiles }
        )
        $huggingFaceArguments = @('download', $Repository) +
            $huggingFaceBootstrapFiles +
            @(
                '--revision', $Revision,
                '--local-dir', $stagingRoot,
                '--max-workers', [string]$MaxWorkers
            )
        $null = Invoke-ModelProcess `
            -Name 'download-voxcpm2-huggingface-metadata' `
            -FilePath $hfPath `
            -Arguments $huggingFaceArguments

        $modelScopeDownloadCode = @'
import pathlib
import os
import sys
import hashlib

os.environ["MODELSCOPE_DOWNLOAD_PARALLEL_THRESHOLD_MB"] = "1"
os.environ["MODELSCOPE_DOWNLOAD_PARALLEL_WORKERS"] = sys.argv[4]
os.environ["MODELSCOPE_DOWNLOAD_TIMEOUT"] = "600"
os.environ["MODELSCOPE_DOWNLOAD_MAX_RETRIES"] = "20"
os.environ["MODELSCOPE_DOWNLOAD_PART_SIZE_MB"] = "160"

from modelscope_hub import HubApi

model_id, revision, local_dir, max_workers, *transfer_fields = sys.argv[1:]
if len(transfer_fields) % 3:
    raise RuntimeError("ModelScope transfer records must be path/size/SHA-256 triples")
api = HubApi(endpoint="https://modelscope.cn")
for index in range(0, len(transfer_fields), 3):
    relative_path = transfer_fields[index]
    expected_size = int(transfer_fields[index + 1])
    expected_sha256 = transfer_fields[index + 2]
    api.download_repo(
        repo_id=model_id,
        repo_type="model",
        revision=revision,
        local_dir=local_dir,
        allow_patterns=[relative_path],
        max_workers=1,
    )
    target = pathlib.Path(local_dir, relative_path)
    if not target.is_file() or target.stat().st_size != expected_size:
        raise RuntimeError(f"ModelScope did not produce the expected file: {relative_path}")
    digest = hashlib.sha256()
    with target.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    actual_sha256 = digest.hexdigest()
    if actual_sha256 != expected_sha256:
        raise RuntimeError(
            f"ModelScope SHA-256 mismatch for {relative_path}: "
            f"expected {expected_sha256}, got {actual_sha256}"
        )
'@
        $modelScopeTransferArguments = @(
            foreach ($transferPath in $modelScopeTransferFiles) {
                $transportFile = @(
                    $modelScopeMetadata | Where-Object { [string]$_.Path -ceq $transferPath }
                )[0]
                $transferPath
                [string]$transportFile.Size
                [string]$transportFile.Sha256
            }
        )
        $modelScopeArguments = @(
            '-c', $modelScopeDownloadCode,
            $modelScopeRepository,
            $modelScopeRevision,
            $stagingRoot,
            [string]$modelScopeRangeWorkers
        ) + $modelScopeTransferArguments
        $null = Invoke-ModelProcess `
            -Name 'download-voxcpm2-modelscope-weights' `
            -FilePath $venvPython `
            -Arguments $modelScopeArguments
    }

    $sourceFiles = @(
        foreach ($sibling in @($metadata.siblings | Sort-Object rfilename)) {
            $relativePath = [string]$sibling.rfilename
            $filePath = Join-Path $stagingRoot $relativePath
            if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
                throw "Downloaded snapshot is missing '$relativePath'."
            }
            $item = Get-Item -LiteralPath $filePath
            if ([long]$sibling.size -ne [long]$item.Length) {
                throw "Downloaded file size mismatch for '$relativePath': expected $($sibling.size), got $($item.Length)."
            }
            $sha256 = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($null -ne $sibling.lfs) {
                if ([string]$sibling.lfs.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                    throw "Hugging Face metadata is missing the LFS SHA-256 for '$relativePath'."
                }
                if ($sha256 -cne [string]$sibling.lfs.sha256) {
                    throw "Downloaded LFS SHA-256 mismatch for '$relativePath'."
                }
            } else {
                if ([string]$sibling.blobId -cnotmatch '^[0-9a-f]{40}$') {
                    throw "Hugging Face metadata is missing the Git blob id for '$relativePath'."
                }
                $gitBlobId = Get-GitBlobId -Path $filePath
                if ($gitBlobId -cne [string]$sibling.blobId) {
                    throw "Downloaded Git blob id mismatch for '$relativePath': expected $($sibling.blobId), got $gitBlobId."
                }
            }
            [ordered]@{
                path       = $relativePath
                length     = [long]$item.Length
                sha256     = $sha256
                lfsSha256  = if ($null -ne $sibling.lfs) { [string]$sibling.lfs.sha256 } else { $null }
            }
        }
    )

    if ($null -ne $canonicalLock) {
        Assert-LockSourceSet -Expected @($canonicalLock.files) -Actual $sourceFiles
        $lock = $canonicalLock
    } else {
        $lock = [ordered]@{
            schemaVersion = 1
            artifact      = 'VoxCPM2'
            kind          = 'huggingface'
            repository    = $Repository
            revision      = $Revision
            license       = 'Apache-2.0'
            files         = $sourceFiles
            downloader    = [ordered]@{
                python = (& $venvPython --version 2>&1 | Select-Object -First 1).ToString()
                huggingFaceHub = $huggingFaceHubVersion
                transport = $Transport
                modelScopeHub = if ($Transport -ceq 'ModelScope') { $modelScopeHubVersion } else { $null }
                modelScopeRepository = if ($Transport -ceq 'ModelScope') { $modelScopeRepository } else { $null }
                modelScopeRevision = if ($Transport -ceq 'ModelScope') { $modelScopeRevision } else { $null }
                requirementsFreezeSha256 = (Get-FileHash -LiteralPath $freezePath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            converter     = [ordered]@{
                repository = 'https://github.com/tc-mb/llama.cpp-omni.git'
                tag        = 'b267'
                commit     = '873056743b74e1a4ce5dcf7290e2298428e214db'
                script     = 'tools/omni/voxcpm2/convert_voxcpm2_to_gguf.py'
            }
            generated     = [ordered]@{
                manifest = 'generated-manifest.json'
                files    = @()
            }
        }
    }

    Write-JsonAtomically -Value $lock -Path (Join-Path $stagingRoot 'artifact-lock.json')
    Remove-KnownModelTransportArtifacts -Root $stagingRoot
    if ($null -eq $canonicalLock) {
        Write-JsonAtomically -Value $lock -Path $lockFull
    }
    New-Item -ItemType File -Force -Path (Join-Path $stagingRoot 'READY') | Out-Null
    Assert-ExactArtifactInventory `
        -Root $stagingRoot `
        -AllowedRelativePaths (@($sourceFiles.path) + @('artifact-lock.json', 'READY')) | Out-Null
    Move-Item -LiteralPath $stagingRoot -Destination $publishedRoot

    [pscustomobject]@{
        ModelRoot = $publishedRoot
        LockPath  = $lockFull
        Revision  = $Revision
        Reused    = $false
    }
} catch {
    # Keep the revision-specific .part directory so huggingface_hub can resume
    # multi-gigabyte files safely on the next invocation. READY can exist there
    # only immediately before an attempted atomic move; the .part suffix still
    # prevents it from being accepted as a published model.
    throw
}

[CmdletBinding()]
param(
    [string]$CorpusId = 'ru-v1-01',
    [string]$ProfileId = 'q8-f16-cuda-full',
    [int]$Seed = 42,
    [string]$OutputRoot,
    [string]$ModelRoot,
    [string]$WorkerPath,
    [string]$PromptWavPath,
    [string]$PromptTextPath,
    [string]$PromptManifestPath,
    [switch]$ConfirmRights,
    [switch]$DisableTelemetry
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force

if (-not $ConfirmRights) {
    throw 'RIGHTS_CONFIRMATION_REQUIRED: pass -ConfirmRights only for your own voice or a voice you may use.'
}
$revision = '32279effe8c19989596f05d353d1447f51d9e915'
if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
    $ModelRoot = Join-Path $env:LOCALAPPDATA "VoxBookStudio\spike\generated\VoxCPM2-$($revision.Substring(0, 12))-b267"
}
if ([string]::IsNullOrWhiteSpace($WorkerPath)) {
    $buildPlan = & (Join-Path $PSScriptRoot 'Build-VoxCpmWorker.ps1') -PlanOnly
    $WorkerPath = Join-Path $buildPlan.BuildRoot 'bin\voxcpm2-cli.exe'
}
$localData = Join-Path $repoRoot 'local-test-data'
if ([string]::IsNullOrWhiteSpace($PromptWavPath)) {
    $PromptWavPath = Join-Path $localData 'neutral-anchor.wav'
}
if ([string]::IsNullOrWhiteSpace($PromptTextPath)) {
    $PromptTextPath = Join-Path $localData 'neutral-anchor.txt'
}
if ([string]::IsNullOrWhiteSpace($PromptManifestPath)) {
    $PromptManifestPath = Join-Path $localData 'neutral-anchor.manifest.json'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $env:LOCALAPPDATA 'VoxBookStudio\spike\runs\preview'
}

foreach ($requiredPath in @($WorkerPath, $PromptWavPath, $PromptTextPath, $PromptManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "REQUIRED_INPUT_MISSING: $requiredPath"
    }
}
$modelFull = (Resolve-Path -LiteralPath $ModelRoot).Path
$workerFull = (Resolve-Path -LiteralPath $WorkerPath).Path
$promptWavFull = (Resolve-Path -LiteralPath $PromptWavPath).Path
$promptTextFull = (Resolve-Path -LiteralPath $PromptTextPath).Path
$promptManifest = Get-Content -LiteralPath $PromptManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
if (-not [bool]$promptManifest.consent.rightsConfirmed -or
    -not [bool]$promptManifest.consent.localOnly) {
    throw 'PROMPT_CONSENT_INVALID: private prompt manifest lacks the required local consent flags.'
}
$verifiedPromptWav = Assert-ArtifactHash `
    -Path $promptWavFull `
    -Sha256 ([string]$promptManifest.reference.sha256)
$verifiedPromptText = Assert-ArtifactHash `
    -Path $promptTextFull `
    -Sha256 ([string]$promptManifest.prompt.sha256)
$promptText = [IO.File]::ReadAllText($promptTextFull, [Text.UTF8Encoding]::new($false, $true))
if ($promptText -cne [string]$promptManifest.prompt.text) {
    throw 'PROMPT_TEXT_MISMATCH: prompt file and private manifest differ.'
}

$workerManifestPath = Join-Path (Split-Path (Split-Path $workerFull -Parent) -Parent) 'voxcpm2-worker-manifest.json'
$workerManifest = Get-Content -LiteralPath $workerManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
$workerRecord = @($workerManifest.files | Where-Object { [string]$_.name -ceq 'voxcpm2-cli.exe' })
if ($workerRecord.Count -ne 1 -or
    -not [bool]$workerManifest.source.patch.windowsUtf8Arguments -or
    -not [bool]$workerManifest.source.patch.unicodePaths -or
    -not [bool]$workerManifest.source.patch.strictPcm16Input -or
    -not [bool]$workerManifest.source.patch.checkedWavWrites) {
    throw 'WORKER_MANIFEST_INVALID: patched Windows safety contract is missing.'
}
$verifiedWorker = Assert-ArtifactHash -Path $workerFull -Sha256 ([string]$workerRecord[0].sha256)
if ([long]$verifiedWorker.Length -ne [long]$workerRecord[0].length) {
    throw 'WORKER_MANIFEST_INVALID: worker length differs from its manifest.'
}

$profiles = @(Get-VoxCpmMatrix -ModelRoot $modelFull)
$profile = @($profiles | Where-Object { [string]$_.Id -ceq $ProfileId })
if ($profile.Count -ne 1) {
    throw "PROFILE_UNKNOWN: $ProfileId"
}
$generatedManifestPath = Join-Path $modelFull 'generated-manifest.json'
$generatedManifest = Get-Content -LiteralPath $generatedManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
$modelHashes = @{}
foreach ($modelPath in @($profile[0].BaseLmPath, $profile[0].AcousticPath)) {
    $name = Split-Path $modelPath -Leaf
    $record = @($generatedManifest.files | Where-Object { [string]$_.name -ceq $name })
    if ($record.Count -ne 1) {
        throw "MODEL_MANIFEST_INVALID: missing $name"
    }
    $verified = Assert-ArtifactHash -Path $modelPath -Sha256 ([string]$record[0].sha256)
    if ([long]$verified.Length -ne [long]$record[0].length) {
        throw "MODEL_MANIFEST_INVALID: length mismatch for $name"
    }
    $modelHashes[$name] = [string]$verified.Sha256
}

$corpusPath = Join-Path $repoRoot 'benchmarks\corpus\ru-v1.json'
$corpus = Get-Content -LiteralPath $corpusPath -Raw | ConvertFrom-Json -ErrorAction Stop
$item = @($corpus.items | Where-Object { [string]$_.id -ceq $CorpusId })
if ($item.Count -ne 1) {
    throw "CORPUS_ITEM_UNKNOWN: $CorpusId"
}
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
[void][IO.Directory]::CreateDirectory($outputFull)
$caseId = "$CorpusId.$ProfileId.seed-$Seed"
$case = [pscustomobject]@{
    Id = $caseId
    ProfileId = $ProfileId
    Text = [string]$item[0].text
    OutputPath = Join-Path $outputFull "$caseId.wav.part"
    AllowedOutputRoot = $outputFull
    AllowedInputRoots = @($modelFull, $localData)
    BaseLmPath = [string]$profile[0].BaseLmPath
    AcousticPath = [string]$profile[0].AcousticPath
    PromptWavPath = $promptWavFull
    PromptText = $promptText
    ReferencePath = $null
    Steps = [int]$item[0].max_steps
    Timesteps = 10
    Cfg = 2.0
    Temperature = 1.0
    Seed = $Seed
    GpuLayers = [int]$profile[0].GpuLayers
    UseCpu = [bool]$profile[0].UseCpu
    InputMetadata = [ordered]@{
        workerRevision = [string]$workerManifest.source.commit
        workerPatchSha256 = [string]$workerManifest.source.patch.sha256
        workerSha256 = [string]$verifiedWorker.Sha256
        modelRevision = [string]$generatedManifest.source.revision
        converterRevision = [string]$generatedManifest.converter.commit
        modelManifestSha256 = (Get-FileHash -LiteralPath $generatedManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        baseLmSha256 = [string]$modelHashes[(Split-Path $profile[0].BaseLmPath -Leaf)]
        acousticSha256 = [string]$modelHashes[(Split-Path $profile[0].AcousticPath -Leaf)]
        promptWavSha256 = [string]$verifiedPromptWav.Sha256
        promptTextSha256 = [string]$verifiedPromptText.Sha256
        corpusSha256 = (Get-FileHash -LiteralPath $corpusPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$result = SpikeTools\Invoke-VoxCpmCase `
    -Case $case `
    -WorkerPath $workerFull `
    -DisableTelemetry:$DisableTelemetry

[ordered]@{
    caseId = $result.CaseId
    profileId = $result.ProfileId
    status = $result.Status
    exitCode = $result.ExitCode
    wallSeconds = $result.WallSeconds
    outputFileName = if ($result.OutputPath) { Split-Path $result.OutputPath -Leaf } else { $null }
    resultFileName = Split-Path $result.ResultPath -Leaf
} | ConvertTo-Json -Depth 4

if ($result.Status -ne 'SUCCESS') {
    exit 2
}

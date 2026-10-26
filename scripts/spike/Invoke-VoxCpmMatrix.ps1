[CmdletBinding()]
param(
    [ValidateSet('Preview', 'Stability')]
    [string]$Mode = 'Preview',

    [string]$SelectedProfileId = 'q8-f16-cuda-full',
    [string]$OutputRoot,
    [string]$ModelRoot,
    [string]$WorkerPath,
    [string]$PromptWavPath,
    [string]$PromptTextPath,
    [string]$PromptManifestPath,

    [ValidateRange(1, 86400)]
    [int]$TimeoutSeconds = 7200,

    [switch]$ConfirmRights,
    [switch]$DisableTelemetry,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force
$Mode = if ($Mode -ieq 'Preview') { 'Preview' } else { 'Stability' }

function Write-AtomicUtf8Json {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Value
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    [void][IO.Directory]::CreateDirectory((Split-Path $fullPath -Parent))
    $temporaryPath = "$fullPath.$([guid]::NewGuid().ToString('N')).part"
    try {
        $json = ConvertTo-Json -InputObject $Value -Depth 10
        [IO.File]::WriteAllText($temporaryPath, "$json`n", [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryPath, $fullPath, $true)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function New-SafeCaseSummary {
    param(
        [Parameter(Mandatory)]
        [object]$PlanCase,

        [Parameter(Mandatory)]
        [object]$ResultRecord,

        [Parameter(Mandatory)]
        [string]$Status
    )

    [ordered]@{
        caseId = [string]$PlanCase.Id
        corpusId = [string]$PlanCase.CorpusId
        profileId = [string]$PlanCase.ProfileId
        seed = [int]$PlanCase.Seed
        status = $Status
        workerStatus = [string]$ResultRecord.status
        exitCode = [int]$ResultRecord.exitCode
        wallSeconds = [double]$ResultRecord.wallSeconds
        outputFileName = [string]$ResultRecord.outputFileName
        outputSha256 = [string]$ResultRecord.outputSha256
        inputFingerprint = [string]$ResultRecord.inputFingerprint
        minFreeVramMiB = $ResultRecord.minFreeVramMiB
        peakUsedVramMiB = $ResultRecord.peakUsedVramMiB
        durationSeconds = if ($null -ne $ResultRecord.wav) {
            [double]$ResultRecord.wav.durationSeconds
        } else {
            $null
        }
    }
}

$revision = '32279effe8c19989596f05d353d1447f51d9e915'
if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
    $ModelRoot = Join-Path $env:LOCALAPPDATA "VoxBookStudio\spike\generated\VoxCPM2-$($revision.Substring(0, 12))-b267"
}
$profiles = @(Get-VoxCpmMatrix -ModelRoot $ModelRoot)
$corpusPath = Join-Path $repoRoot 'benchmarks\corpus\ru-v1.json'
$corpus = Get-Content -LiteralPath $corpusPath -Raw | ConvertFrom-Json -ErrorAction Stop
$runPlan = @(
    New-VoxCpmRunPlan `
        -Mode $Mode `
        -Profiles $profiles `
        -CorpusItems @($corpus.items) `
        -SelectedProfileId $SelectedProfileId
)

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        mode = $Mode
        selectedProfileId = if ($Mode -ieq 'Stability') { $SelectedProfileId } else { $null }
        caseCount = $runPlan.Count
        cases = @($runPlan | ForEach-Object {
            [ordered]@{
                caseId = [string]$_.Id
                corpusId = [string]$_.CorpusId
                profileId = [string]$_.ProfileId
                seed = [int]$_.Seed
            }
        })
    } | ConvertTo-Json -Depth 6
    return
}

if (-not $ConfirmRights) {
    throw 'RIGHTS_CONFIRMATION_REQUIRED: pass -ConfirmRights only for your own voice or a voice you may use.'
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
    $runKind = $Mode.ToLowerInvariant()
    $OutputRoot = Join-Path $env:LOCALAPPDATA "VoxBookStudio\spike\runs\$runKind"
}

foreach ($requiredPath in @($WorkerPath, $PromptWavPath, $PromptTextPath, $PromptManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "REQUIRED_INPUT_MISSING: $requiredPath"
    }
}
if (-not (Test-Path -LiteralPath $ModelRoot -PathType Container)) {
    throw "MODEL_ROOT_MISSING: $ModelRoot"
}
$modelFull = (Resolve-Path -LiteralPath $ModelRoot).Path
$workerFull = (Resolve-Path -LiteralPath $WorkerPath).Path
$promptWavFull = (Resolve-Path -LiteralPath $PromptWavPath).Path
$promptTextFull = (Resolve-Path -LiteralPath $PromptTextPath).Path
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
[void][IO.Directory]::CreateDirectory($outputFull)

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

$generatedManifestPath = Join-Path $modelFull 'generated-manifest.json'
$generatedManifest = Get-Content -LiteralPath $generatedManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
$plannedProfileIds = @($runPlan.ProfileId | Sort-Object -Unique)
$plannedProfiles = @($profiles | Where-Object { $plannedProfileIds -ccontains [string]$_.Id })
$plannedModelPaths = @(
    $plannedProfiles | ForEach-Object {
        [string]$_.BaseLmPath
        [string]$_.AcousticPath
    } | Sort-Object -Unique
)
$verifiedModelHashes = @{}
foreach ($modelPath in $plannedModelPaths) {
    $name = Split-Path $modelPath -Leaf
    $record = @($generatedManifest.files | Where-Object { [string]$_.name -ceq $name })
    if ($record.Count -ne 1) {
        throw "MODEL_MANIFEST_INVALID: missing $name"
    }
    $verified = Assert-ArtifactHash -Path $modelPath -Sha256 ([string]$record[0].sha256)
    if ([long]$verified.Length -ne [long]$record[0].length) {
        throw "MODEL_MANIFEST_INVALID: length mismatch for $name"
    }
    $verifiedModelHashes[$name] = [string]$verified.Sha256
}

$corpusSha256 = (Get-FileHash -LiteralPath $corpusPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestSha256 = (Get-FileHash -LiteralPath $generatedManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$summaryPath = Join-Path $outputFull 'matrix-summary.json'
$safeResults = [Collections.Generic.List[object]]::new()
$summary = [ordered]@{
    schemaVersion = 1
    mode = $Mode
    selectedProfileId = if ($Mode -ieq 'Stability') { $SelectedProfileId } else { $null }
    plannedCaseCount = $runPlan.Count
    completedCaseCount = 0
    failedCaseCount = 0
    inputs = [ordered]@{
        corpusSha256 = $corpusSha256
        workerRevision = [string]$workerManifest.source.commit
        workerPatchSha256 = [string]$workerManifest.source.patch.sha256
        workerSha256 = [string]$verifiedWorker.Sha256
        modelRevision = [string]$generatedManifest.source.revision
        converterRevision = [string]$generatedManifest.converter.commit
        modelManifestSha256 = $manifestSha256
        promptWavSha256 = [string]$verifiedPromptWav.Sha256
        promptTextSha256 = [string]$verifiedPromptText.Sha256
    }
    cases = $safeResults
}

foreach ($planCase in $runPlan) {
    $profile = @($profiles | Where-Object { [string]$_.Id -ceq [string]$planCase.ProfileId })
    $item = @($corpus.items | Where-Object { [string]$_.id -ceq [string]$planCase.CorpusId })
    if ($profile.Count -ne 1 -or $item.Count -ne 1) {
        throw "RUN_PLAN_INVALID: $($planCase.Id)"
    }

    $finalPath = Join-Path $outputFull "$($planCase.Id).wav"
    $logRoot = Join-Path $outputFull '_logs'
    $resultPath = Join-Path $logRoot "$($planCase.Id).result.json"
    $case = [pscustomobject]@{
        Id = [string]$planCase.Id
        ProfileId = [string]$planCase.ProfileId
        Text = [string]$item[0].text
        OutputPath = "$finalPath.part"
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
        Seed = [int]$planCase.Seed
        GpuLayers = [int]$profile[0].GpuLayers
        UseCpu = [bool]$profile[0].UseCpu
        InputMetadata = [ordered]@{
            workerRevision = [string]$workerManifest.source.commit
            workerPatchSha256 = [string]$workerManifest.source.patch.sha256
            workerSha256 = [string]$verifiedWorker.Sha256
            modelRevision = [string]$generatedManifest.source.revision
            converterRevision = [string]$generatedManifest.converter.commit
            modelManifestSha256 = $manifestSha256
            baseLmSha256 = [string]$verifiedModelHashes[(Split-Path $profile[0].BaseLmPath -Leaf)]
            acousticSha256 = [string]$verifiedModelHashes[(Split-Path $profile[0].AcousticPath -Leaf)]
            promptWavSha256 = [string]$verifiedPromptWav.Sha256
            promptTextSha256 = [string]$verifiedPromptText.Sha256
            corpusSha256 = $corpusSha256
        }
    }
    $expectedFingerprint = Get-VoxCpmCaseFingerprint -Case $case

    $existingIsValid = $false
    if ((Test-Path -LiteralPath $finalPath -PathType Leaf) -and
        (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        try {
            $wavInfo = Read-VoxCpmWavInfo -Path $finalPath
            $existingRecord = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $existingIsValid = (
                [string]$existingRecord.status -ceq 'SUCCESS' -and
                [string]$existingRecord.caseId -ceq [string]$planCase.Id -and
                [string]$existingRecord.profileId -ceq [string]$planCase.ProfileId -and
                [string]$existingRecord.outputSha256 -ceq [string]$wavInfo.Sha256 -and
                [string]$existingRecord.inputFingerprint -ceq [string]$expectedFingerprint.Sha256
            )
        } catch {
            $existingIsValid = $false
        }
    }
    if ($existingIsValid) {
        $safeResults.Add((New-SafeCaseSummary -PlanCase $planCase -ResultRecord $existingRecord -Status 'SKIPPED_VALID'))
        $summary.completedCaseCount = $safeResults.Count
        Write-AtomicUtf8Json -Path $summaryPath -Value $summary
        continue
    }

    $previousArtifacts = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($candidate in @($finalPath, "$finalPath.part")) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $previousArtifacts.Add((Get-Item -LiteralPath $candidate))
        }
    }
    if (Test-Path -LiteralPath $logRoot -PathType Container) {
        foreach ($previousLog in @(Get-ChildItem -LiteralPath $logRoot -File | Where-Object {
            $_.Name.StartsWith("$($planCase.Id).", [StringComparison]::Ordinal)
        })) {
            $previousArtifacts.Add($previousLog)
        }
    }
    if ($previousArtifacts.Count -gt 0) {
        $attemptId = "$([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        $attemptRoot = Join-Path $logRoot ("_attempts\$($planCase.Id)\$attemptId")
        [void][IO.Directory]::CreateDirectory($attemptRoot)
        foreach ($previousArtifact in $previousArtifacts) {
            Move-Item -LiteralPath $previousArtifact.FullName -Destination $attemptRoot
        }
    }

    $runResult = SpikeTools\Invoke-VoxCpmCase `
        -Case $case `
        -WorkerPath $workerFull `
        -TimeoutSeconds $TimeoutSeconds `
        -DisableTelemetry:$DisableTelemetry
    $resultRecord = Get-Content -LiteralPath $runResult.ResultPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $safeResults.Add((New-SafeCaseSummary -PlanCase $planCase -ResultRecord $resultRecord -Status ([string]$runResult.Status)))
    $summary.completedCaseCount = $safeResults.Count
    $summary.failedCaseCount = @($safeResults | Where-Object {
        [string]$_.status -cnotin @('SUCCESS', 'SKIPPED_VALID')
    }).Count
    Write-AtomicUtf8Json -Path $summaryPath -Value $summary
}

$summary | ConvertTo-Json -Depth 10
if ([int]$summary.failedCaseCount -gt 0) {
    exit 2
}

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$SyntheticDataRoot,
    [string]$SyntheticLaunchPipe,
    [string]$DeveloperRuntimeRoot,
    [string]$DeveloperModelRoot,
    [string]$DeveloperLockRoot,
    [string]$TrainingRuntimeConfig,
    [string]$TtsCorpusRuntimeConfig,
    [string]$VoxCpmTrainingRuntimeConfig,
    [string]$VoiceFfmpeg,
    [switch]$SkipBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repositoryRoot 'src\LocalAudiobookStudio.App\LocalAudiobookStudio.App.csproj'
$launchArguments = @()
if ($VoiceFfmpeg) {
    if ($VoiceFfmpeg -notmatch '^[A-Za-z]:[\\/]' -or $VoiceFfmpeg.Contains('"') -or $VoiceFfmpeg -match '[\x00-\x1f]') {
        throw 'VoiceFfmpeg must be an absolute local path.'
    }
    if (-not (Test-Path -LiteralPath $VoiceFfmpeg -PathType Leaf)) { throw 'FFmpeg executable is missing.' }
    $VoiceFfmpeg = (Resolve-Path -LiteralPath $VoiceFfmpeg).Path
}
if ($VoxCpmTrainingRuntimeConfig) {
    if ($VoxCpmTrainingRuntimeConfig -notmatch '^[A-Za-z]:[\\/]' -or $VoxCpmTrainingRuntimeConfig.Contains('"') -or $VoxCpmTrainingRuntimeConfig -match '[\x00-\x1f]') {
        throw 'VoxCpmTrainingRuntimeConfig must be an absolute local path.'
    }
    $VoxCpmTrainingRuntimeConfig = (Resolve-Path -LiteralPath $VoxCpmTrainingRuntimeConfig).Path
    if (-not (Test-Path -LiteralPath $VoxCpmTrainingRuntimeConfig -PathType Leaf)) { throw 'VoxCPM training runtime configuration is missing.' }
}
if ($TtsCorpusRuntimeConfig) {
    if ($TtsCorpusRuntimeConfig -notmatch '^[A-Za-z]:[\\/]' -or $TtsCorpusRuntimeConfig.Contains('"') -or $TtsCorpusRuntimeConfig -match '[\x00-\x1f]') {
        throw 'TtsCorpusRuntimeConfig must be an absolute local path.'
    }
    $TtsCorpusRuntimeConfig = (Resolve-Path -LiteralPath $TtsCorpusRuntimeConfig).Path
    if (-not (Test-Path -LiteralPath $TtsCorpusRuntimeConfig -PathType Leaf)) { throw 'Corpus runtime configuration is missing.' }
}
if ($TrainingRuntimeConfig) {
    if ($TrainingRuntimeConfig -notmatch '^[A-Za-z]:[\\/]' -or $TrainingRuntimeConfig.Contains('"') -or $TrainingRuntimeConfig -match '[\x00-\x1f]') {
        throw 'TrainingRuntimeConfig must be an absolute local path.'
    }
    $TrainingRuntimeConfig = (Resolve-Path -LiteralPath $TrainingRuntimeConfig).Path
    if (-not (Test-Path -LiteralPath $TrainingRuntimeConfig -PathType Leaf)) { throw 'Training runtime configuration is missing.' }
}
if ($SyntheticLaunchPipe -and -not $SyntheticDataRoot) { throw 'SyntheticLaunchPipe requires SyntheticDataRoot.' }
if ($SyntheticDataRoot) {
    # Windows PowerShell 5.1 does not expose Path.IsPathFullyQualified.
    if ($SyntheticDataRoot -notmatch '^[A-Za-z]:[\\/]') { throw 'SyntheticDataRoot must be an absolute local path.' }
    $launchArguments = @('--synthetic-data-root', ('"' + [IO.Path]::GetFullPath($SyntheticDataRoot) + '"'))
    if ($SyntheticLaunchPipe) {
        $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        if ($SyntheticLaunchPipe -notmatch ("^LAS\.SyntheticLaunch\." + $sessionId + '\.[a-f0-9]{32}$')) { throw 'Invalid synthetic launch channel.' }
        $launchArguments += @('--synthetic-launch-pipe', $SyntheticLaunchPipe)
    }
}
$runtimeRoots = @($DeveloperRuntimeRoot, $DeveloperModelRoot, $DeveloperLockRoot)
$providedRoots = @($runtimeRoots | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($providedRoots.Count -ne 0) {
    if ($providedRoots.Count -ne 3) { throw 'Specify all three developer runtime/model/lock roots.' }
    foreach ($runtimeRoot in $runtimeRoots) {
        if ($runtimeRoot -notmatch '^[A-Za-z]:[\\/]' -or $runtimeRoot.Contains('"') -or $runtimeRoot -match '[\x00-\x1f]') {
            throw 'Developer roots must be absolute local paths.'
        }
    }
    # Explicit CPU opt-in; does not install/download models or enable training.
    $launchArguments += @('--developer-cpu-runtime')
    foreach ($runtimeRoot in $runtimeRoots) { $launchArguments += @('"' + [IO.Path]::GetFullPath($runtimeRoot) + '"') }
}
if ($TrainingRuntimeConfig) { $launchArguments += @('--training-runtime-config', ('"' + $TrainingRuntimeConfig + '"')) }
if ($TtsCorpusRuntimeConfig) { $launchArguments += @('--tts-corpus-runtime-config', ('"' + $TtsCorpusRuntimeConfig + '"')) }
if ($VoxCpmTrainingRuntimeConfig) { $launchArguments += @('--voxcpm-training-runtime-config', ('"' + $VoxCpmTrainingRuntimeConfig + '"')) }
if ($VoiceFfmpeg) { $launchArguments += @('--voice-ffmpeg', ('"' + $VoiceFfmpeg + '"')) }
Push-Location -LiteralPath $repositoryRoot
try {
    if (-not $SkipBuild) {
        & dotnet build $project -c $Configuration -p:BuildStudioClient=true
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    $executable = Join-Path $repositoryRoot "src\LocalAudiobookStudio.App\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64\LocalAudiobookStudio.App.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Studio build is missing; run this script without -SkipBuild first.' }
    if ($launchArguments.Count -gt 0) {
        Start-Process -FilePath $executable -ArgumentList $launchArguments -WindowStyle Hidden -PassThru
    } else {
        Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
    }
} finally { Pop-Location }

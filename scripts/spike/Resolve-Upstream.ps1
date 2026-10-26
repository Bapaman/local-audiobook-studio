[CmdletBinding()]
param(
    [string]$Repository = 'https://github.com/tc-mb/llama.cpp-omni.git',
    [string]$Tag = 'b267',
    [string]$ExpectedCommit = '873056743b74e1a4ce5dcf7290e2298428e214db',
    [string]$DestinationRoot,
    [string]$LockPath,
    [string]$ArtifactName = 'llama.cpp-omni',
    [string]$ExpectedLicense = 'MIT'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force

if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path $repoRoot '.tools\upstream'
}

if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $repoRoot 'eng\locks\llama-cpp-omni.lock.json'
}

if ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$') {
    throw 'ExpectedCommit must contain exactly 40 lowercase hexadecimal characters.'
}

if ($Tag -match '^(?:main|master|refs/heads/)') {
    throw "Tag must identify an immutable tag, not branch '$Tag'."
}

function Assert-ProcessSuccess {
    param(
        [Parameter(Mandatory)]
        [object]$Result,

        [Parameter(Mandatory)]
        [string]$Operation
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Operation failed with exit code $($Result.ExitCode).`nSTDOUT:`n$($Result.StdOut)`nSTDERR:`n$($Result.StdErr)"
    }
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
        throw "Refusing to modify path outside destination root: $childFull"
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
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $partialPath -Encoding utf8NoBOM
    Move-Item -Force -LiteralPath $partialPath -Destination $Path
}

New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null
$destinationRootFull = (Resolve-Path -LiteralPath $DestinationRoot).Path
$publishedRoot = Join-Path $destinationRootFull "$ArtifactName-$Tag"
$stagingRoot = "$publishedRoot.part"
Assert-ChildPath -Root $destinationRootFull -Child $publishedRoot
Assert-ChildPath -Root $destinationRootFull -Child $stagingRoot

$publishedLockPath = Join-Path $publishedRoot 'artifact-lock.json'
$readyPath = Join-Path $publishedRoot 'READY'
if (Test-Path -LiteralPath $publishedRoot) {
    if (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
        throw "Existing published artifact is incomplete because READY is missing: $publishedRoot"
    }

    $lock = Read-ArtifactLock `
        -Path $publishedLockPath `
        -ExpectedRepository $Repository `
        -ExpectedTag $Tag `
        -ExpectedLicense $ExpectedLicense `
        -ArtifactRoot $publishedRoot
    if ([string]$lock.commit -cne $ExpectedCommit) {
        throw "Published artifact commit '$($lock.commit)' does not match expected commit '$ExpectedCommit'."
    }

    Write-JsonAtomically -Value $lock -Path $LockPath
    return [pscustomobject]@{
        Path       = $publishedRoot
        LockPath   = $LockPath
        Commit     = $lock.commit
        Reused     = $true
    }
}

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

$gitCommand = Get-Command git -ErrorAction Stop
try {
    $cloneResult = Invoke-CheckedProcess `
        -FilePath $gitCommand.Source `
        -ArgumentList @(
            '-c', 'core.longpaths=true', 'clone', '--quiet', '--filter=blob:none', '--single-branch',
            '--branch', $Tag, '--depth', '1', '--', $Repository, $stagingRoot
        ) `
        -WorkingDirectory $destinationRootFull
    Assert-ProcessSuccess -Result $cloneResult -Operation 'Git clone'

    $headResult = Invoke-CheckedProcess `
        -FilePath $gitCommand.Source `
        -ArgumentList @('-C', $stagingRoot, 'rev-parse', 'HEAD') `
        -WorkingDirectory $destinationRootFull
    Assert-ProcessSuccess -Result $headResult -Operation 'Git commit resolution'
    $resolvedCommit = $headResult.StdOut.Trim()
    if ($resolvedCommit -cne $ExpectedCommit) {
        throw "Resolved commit '$resolvedCommit' does not match expected commit '$ExpectedCommit'."
    }

    $tagResult = Invoke-CheckedProcess `
        -FilePath $gitCommand.Source `
        -ArgumentList @('-C', $stagingRoot, 'describe', '--tags', '--exact-match', 'HEAD') `
        -WorkingDirectory $destinationRootFull
    Assert-ProcessSuccess -Result $tagResult -Operation 'Git tag verification'
    if ($tagResult.StdOut.Trim() -cne $Tag) {
        throw "Resolved tag '$($tagResult.StdOut.Trim())' does not match expected tag '$Tag'."
    }

    $licensePath = Join-Path $stagingRoot 'LICENSE'
    if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
        throw "Upstream license file is missing: $licensePath"
    }

    $licenseText = Get-Content -Raw -LiteralPath $licensePath
    if ($ExpectedLicense -ceq 'MIT' -and $licenseText -notmatch '(?i)MIT License') {
        throw "Upstream LICENSE does not identify the expected MIT license: $licensePath"
    }

    $lock = [ordered]@{
        schemaVersion = 1
        artifact      = $ArtifactName
        kind          = 'git'
        repository    = $Repository
        tag           = $Tag
        commit        = $resolvedCommit
        license       = $ExpectedLicense
        licenseFile   = 'LICENSE'
    }

    Write-JsonAtomically -Value $lock -Path (Join-Path $stagingRoot 'artifact-lock.json')
    Move-Item -LiteralPath $stagingRoot -Destination $publishedRoot
    Write-JsonAtomically -Value $lock -Path $LockPath
    New-Item -ItemType File -Path $readyPath -ErrorAction Stop | Out-Null

    [pscustomobject]@{
        Path       = $publishedRoot
        LockPath   = $LockPath
        Commit     = $resolvedCommit
        Reused     = $false
    }
} catch {
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-ChildPath -Root $destinationRootFull -Child $stagingRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    throw
}

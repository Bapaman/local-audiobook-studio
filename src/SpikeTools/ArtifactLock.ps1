function Read-ArtifactLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [string]$ExpectedRepository,
        [string]$ExpectedTag,
        [string]$ExpectedRevision,
        [string]$ExpectedLicense,
        [string]$ArtifactRoot
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Artifact lock does not exist: $Path"
    }

    try {
        $lock = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Artifact lock is not valid JSON: $Path. $($_.Exception.Message)"
    }

    foreach ($propertyName in @('schemaVersion', 'artifact', 'kind', 'repository', 'license')) {
        if ($null -eq $lock.PSObject.Properties[$propertyName] -or
            [string]::IsNullOrWhiteSpace([string]$lock.$propertyName)) {
            throw "Artifact lock is missing required property '$propertyName': $Path"
        }
    }

    if ([int]$lock.schemaVersion -ne 1) {
        throw "Unsupported artifact lock schema version '$($lock.schemaVersion)': $Path"
    }

    if ([string]$lock.kind -eq 'git') {
        if ($null -eq $lock.PSObject.Properties['commit'] -or
            [string]$lock.commit -cnotmatch '^[0-9a-f]{40}$') {
            throw "Artifact lock commit must contain exactly 40 lowercase hexadecimal characters: $Path"
        }

        if ($null -eq $lock.PSObject.Properties['tag'] -or
            [string]::IsNullOrWhiteSpace([string]$lock.tag)) {
            throw "Git artifact lock is missing required property 'tag': $Path"
        }

        if ([string]$lock.tag -match '^(?:main|master|refs/heads/)' ) {
            throw "Git artifact lock tag must be an immutable tag, not a branch: $($lock.tag)"
        }
    } elseif ([string]$lock.kind -eq 'huggingface') {
        if ([string]$lock.repository -match '^(?i:DennisHuang648)(?:/|$)') {
            throw "Community VoxCPM/GGUF repositories are not accepted as official canonical sources: '$($lock.repository)'."
        }
        $isVoxCpm2 = (
            [string]$lock.artifact -ieq 'VoxCPM2' -or
            [string]$lock.repository -ieq 'openbmb/VoxCPM2'
        )
        if ($isVoxCpm2 -and [string]$lock.artifact -cne 'VoxCPM2') {
            throw "The official model artifact identity must be exactly 'VoxCPM2', not '$($lock.artifact)'."
        }
        if ($isVoxCpm2 -and [string]$lock.repository -cne 'openbmb/VoxCPM2') {
            throw "VoxCPM2 model artifacts must come from the official OpenBMB repository, not '$($lock.repository)'."
        }
        if ($isVoxCpm2 -and [string]$lock.license -cne 'Apache-2.0') {
            throw "The official VoxCPM2 model lock must use license Apache-2.0, not '$($lock.license)'."
        }

        if ($null -eq $lock.PSObject.Properties['revision'] -or
            [string]$lock.revision -cnotmatch '^[0-9a-f]{40}$') {
            throw "Hugging Face revision must contain exactly 40 lowercase hexadecimal characters, not a floating revision: $Path"
        }

        if ($null -eq $lock.PSObject.Properties['files'] -or @($lock.files).Count -eq 0) {
            throw "Hugging Face artifact lock must contain upstream file hashes: $Path"
        }

        $sourcePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in @($lock.files)) {
            $sourcePath = [string]$file.path
            $pathSegments = @($sourcePath -split '[\\/]')
            if ([string]::IsNullOrWhiteSpace($sourcePath) -or
                [System.IO.Path]::IsPathRooted($sourcePath) -or
                $pathSegments.Count -eq 0 -or
                @($pathSegments | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
                $sourcePath.Contains(':') -or
                -not $sourcePaths.Add($sourcePath)) {
                if (-not $sourcePaths.Add($sourcePath)) {
                    throw "Hugging Face artifact lock contains a duplicate source path '$sourcePath': $Path"
                }
                throw "Every Hugging Face upstream file must use a safe relative path: $Path"
            }
            if ([long]$file.length -le 0 -or
                [string]$file.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw "Every Hugging Face upstream file must have a positive length and a 64-character lowercase SHA-256 hash: $Path"
            }
        }

        if ($isVoxCpm2) {
            $expectedSourcePaths = @(
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
            if (@($lock.files).Count -ne $expectedSourcePaths.Count -or
                (Compare-Object `
                    -ReferenceObject $expectedSourcePaths `
                    -DifferenceObject @($lock.files.path) `
                    -CaseSensitive).Count -gt 0) {
                throw "The official VoxCPM2 lock must contain exactly nine canonical source files: $Path"
            }
            if ($null -eq $lock.PSObject.Properties['generated'] -or
                [string]$lock.generated.manifest -cne 'generated-manifest.json' -or
                $null -eq $lock.generated.PSObject.Properties['files']) {
                throw "The official VoxCPM2 lock must contain generated manifest metadata: $Path"
            }
        }

        if ($null -ne $lock.PSObject.Properties['converter']) {
            if ([string]$lock.converter.commit -cnotmatch '^[0-9a-f]{40}$') {
                throw "Converter commit must contain exactly 40 lowercase hexadecimal characters: $Path"
            }
        }
        if ($isVoxCpm2) {
            if ($null -eq $lock.PSObject.Properties['converter']) {
                throw "The official VoxCPM2 lock is missing converter provenance: $Path"
            }
            if ([string]$lock.converter.repository -cne 'https://github.com/tc-mb/llama.cpp-omni.git' -or
                [string]$lock.converter.tag -cne 'b267' -or
                [string]$lock.converter.commit -cne '873056743b74e1a4ce5dcf7290e2298428e214db' -or
                [string]$lock.converter.script -cne 'tools/omni/voxcpm2/convert_voxcpm2_to_gguf.py') {
                throw "The official VoxCPM2 lock has unexpected converter provenance: $Path"
            }
        }

        if ($null -ne $lock.PSObject.Properties['generated'] -and
            @($lock.generated.files).Count -gt 0) {
            if ([string]::IsNullOrWhiteSpace([string]$lock.generated.manifest)) {
                throw "Generated artifact lock must name its manifest: $Path"
            }
            $generatedNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($file in @($lock.generated.files)) {
                if ([string]::IsNullOrWhiteSpace([string]$file.name) -or
                    [System.IO.Path]::GetFileName([string]$file.name) -cne [string]$file.name -or
                    -not $generatedNames.Add([string]$file.name) -or
                    [long]$file.length -le 0 -or
                    [string]$file.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                    throw "Every generated artifact must have a name, positive length and 64-character lowercase SHA-256 hash: $Path"
                }
            }
            if ($isVoxCpm2) {
                $expectedGeneratedNames = @(
                    'VoxCPM2-Acoustic-F16.gguf',
                    'VoxCPM2-Acoustic-F32.gguf',
                    'VoxCPM2-BaseLM-F16.gguf',
                    'VoxCPM2-BaseLM-F32.gguf',
                    'VoxCPM2-BaseLM-Q8_0.gguf'
                )
                if (@($lock.generated.files).Count -ne $expectedGeneratedNames.Count -or
                    (Compare-Object `
                        -ReferenceObject $expectedGeneratedNames `
                        -DifferenceObject @($lock.generated.files.name) `
                        -CaseSensitive).Count -gt 0) {
                    throw "The official VoxCPM2 lock must contain exactly five canonical generated artifacts: $Path"
                }
            }
        }
    } else {
        throw "Unsupported artifact lock kind '$($lock.kind)': $Path"
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedRepository) -and
        [string]$lock.repository -cne $ExpectedRepository) {
        throw "Artifact lock repository '$($lock.repository)' does not match expected repository '$ExpectedRepository'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedTag) -and
        [string]$lock.tag -cne $ExpectedTag) {
        throw "Artifact lock tag '$($lock.tag)' does not match expected tag '$ExpectedTag'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedRevision) -and
        [string]$lock.revision -cne $ExpectedRevision) {
        throw "Artifact lock revision '$($lock.revision)' does not match expected revision '$ExpectedRevision'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedLicense) -and
        [string]$lock.license -cne $ExpectedLicense) {
        throw "Artifact lock license '$($lock.license)' does not match expected license '$ExpectedLicense'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ArtifactRoot)) {
        $trimmedRoot = $ArtifactRoot.TrimEnd([char[]]@('\', '/'))
        $artifactLeaf = Split-Path -Leaf $trimmedRoot
        if ($artifactLeaf.EndsWith('.part', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Artifact staging directory ending in .part is not ready: $ArtifactRoot"
        }

        if (-not (Test-Path -LiteralPath (Join-Path $ArtifactRoot 'READY') -PathType Leaf)) {
            throw "Published artifact is missing READY marker: $ArtifactRoot"
        }
    }

    return $lock
}

function Assert-ArtifactHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Sha256
    )

    if ($Sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Expected SHA-256 must contain exactly 64 lowercase hexadecimal characters.'
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Artifact does not exist: $Path"
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $actualHash = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $Sha256) {
        throw "SHA-256 mismatch for '$resolvedPath': expected $Sha256, got $actualHash."
    }

    $item = Get-Item -LiteralPath $resolvedPath
    [pscustomobject]@{
        Path   = $resolvedPath
        Sha256 = $actualHash
        Length = [long]$item.Length
    }
}

function Test-LockedArtifactSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [object[]]$Records,

        [Parameter(Mandatory)]
        [string[]]$Names
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return $false
    }
    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name) -or
            [IO.Path]::GetFileName($name) -cne $name) {
            return $false
        }
        $record = @($Records | Where-Object { [string]$_.name -ceq $name })
        if ($record.Count -ne 1 -or
            [long]$record[0].length -le 0 -or
            [string]$record[0].sha256 -cnotmatch '^[0-9a-f]{64}$') {
            return $false
        }
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return $false
        }
        $item = Get-Item -LiteralPath $path
        if ([long]$item.Length -ne [long]$record[0].length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$record[0].sha256) {
            return $false
        }
    }
    return $true
}

function Assert-CleanAuthoringTargets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$OutputRoot,

        [Parameter(Mandatory)]
        [string]$StagingRoot
    )

    if (Test-Path -LiteralPath $OutputRoot) {
        throw "A clean authoring run requires the publication path to be absent: $OutputRoot"
    }
    if (Test-Path -LiteralPath $StagingRoot) {
        throw "A clean authoring run requires the staging path to be absent: $StagingRoot"
    }
}

function Assert-ExactArtifactInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$AllowedRelativePaths
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Artifact root does not exist: $Root"
    }

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $allowedFiles = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    $allowedDirectories = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($relativePathValue in $AllowedRelativePaths) {
        $relativePath = [string]$relativePathValue
        $segments = @($relativePath -split '[\\/]')
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains(':') -or
            @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
            throw "Allowed inventory entry must be a safe relative file path: '$relativePath'."
        }
        $normalized = $relativePath.Replace('\\', '/')
        if (-not $allowedFiles.Add($normalized)) {
            throw "Allowed inventory contains a duplicate file path: '$relativePath'."
        }
        for ($index = 1; $index -lt $segments.Count; $index++) {
            $null = $allowedDirectories.Add(($segments[0..($index - 1)] -join '/'))
        }
    }

    foreach ($entry in Get-ChildItem -LiteralPath $rootFull -Force -Recurse) {
        $relative = [System.IO.Path]::GetRelativePath($rootFull, $entry.FullName).Replace('\\', '/')
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Artifact inventory contains an unexpected reparse point: '$relative'."
        }
        if ($entry.PSIsContainer) {
            if (-not $allowedDirectories.Contains($relative)) {
                throw "Artifact inventory contains an unexpected directory: '$relative'."
            }
        } elseif (-not $allowedFiles.Contains($relative)) {
            throw "Artifact inventory contains an unexpected file: '$relative'."
        }
    }

    foreach ($relativePath in $allowedFiles) {
        $candidate = Join-Path $rootFull $relativePath
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Artifact inventory is missing required file: '$relativePath'."
        }
    }

    [pscustomobject]@{
        Root  = $rootFull
        Files = [string[]]@($allowedFiles)
    }
}

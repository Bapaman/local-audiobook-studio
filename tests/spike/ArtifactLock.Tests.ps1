BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $modulePath = Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1'
    Import-Module $modulePath -Force

    $expectedRepository = 'https://github.com/tc-mb/llama.cpp-omni.git'
    $expectedTag = 'b267'
    $expectedCommit = '873056743b74e1a4ce5dcf7290e2298428e214db'
    $expectedVoxRevision = '32279effe8c19989596f05d353d1447f51d9e915'

    function New-ValidLlamaLock {
        [ordered]@{
            schemaVersion = 1
            artifact      = 'llama.cpp-omni'
            kind          = 'git'
            repository    = $expectedRepository
            tag           = $expectedTag
            commit        = $expectedCommit
            license       = 'MIT'
            licenseFile   = 'LICENSE'
        }
    }

    function Write-TestLock {
        param(
            [Parameter(Mandatory)]
            [System.Collections.IDictionary]$Lock,

            [Parameter(Mandatory)]
            [string]$Path
        )

        $Lock | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
    }

    function New-ValidVoxCpmLock {
        [ordered]@{
            schemaVersion = 1
            artifact      = 'VoxCPM2'
            kind          = 'huggingface'
            repository    = 'openbmb/VoxCPM2'
            revision      = $expectedVoxRevision
            license       = 'Apache-2.0'
            files         = @(
                foreach ($name in @(
                    '.gitattributes',
                    'README.md',
                    'audiovae.pth',
                    'config.json',
                    'model.safetensors',
                    'special_tokens_map.json',
                    'tokenization_voxcpm2.py',
                    'tokenizer.json',
                    'tokenizer_config.json'
                )) {
                    [ordered]@{
                        path   = $name
                        length = 123
                        sha256 = 'b' * 64
                    }
                }
            )
            converter     = [ordered]@{
                repository = $expectedRepository
                tag        = $expectedTag
                commit     = $expectedCommit
                script     = 'tools/omni/voxcpm2/convert_voxcpm2_to_gguf.py'
            }
            generated     = [ordered]@{
                manifest = 'generated-manifest.json'
                files    = @()
            }
        }
    }
}

Describe 'Read-ArtifactLock' {
    It 'loads the canonical immutable llama.cpp-omni lock' {
        $lockPath = Join-Path $repoRoot 'eng\locks\llama-cpp-omni.lock.json'

        $lock = Read-ArtifactLock `
            -Path $lockPath `
            -ExpectedRepository $expectedRepository `
            -ExpectedTag $expectedTag `
            -ExpectedLicense 'MIT'

        $lock.Repository | Should -BeExactly $expectedRepository
        $lock.Tag | Should -BeExactly $expectedTag
        $lock.Commit | Should -BeExactly $expectedCommit
        $lock.Commit | Should -Match '^[0-9a-f]{40}$'
        $lock.License | Should -BeExactly 'MIT'
    }

    It 'rejects a branch name in place of the pinned tag' {
        $lockData = New-ValidLlamaLock
        $lockData.tag = 'master'
        $lockPath = Join-Path $TestDrive 'branch.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        {
            Read-ArtifactLock `
                -Path $lockPath `
                -ExpectedRepository $expectedRepository `
                -ExpectedTag $expectedTag `
                -ExpectedLicense 'MIT'
        } | Should -Throw '*tag*'
    }

    It 'rejects a short commit hash' {
        $lockData = New-ValidLlamaLock
        $lockData.commit = '8730567'
        $lockPath = Join-Path $TestDrive 'short-sha.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*40 lowercase hexadecimal*'
    }

    It 'rejects a changed repository' {
        $lockData = New-ValidLlamaLock
        $lockData.repository = 'https://github.com/example/llama.cpp-omni.git'
        $lockPath = Join-Path $TestDrive 'wrong-repository.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        {
            Read-ArtifactLock -Path $lockPath -ExpectedRepository $expectedRepository
        } | Should -Throw '*repository*'
    }

    It 'rejects a staging directory and requires READY for a published artifact' {
        $stagingRoot = Join-Path $TestDrive 'llama.cpp-omni-b267.part'
        New-Item -ItemType Directory -Path $stagingRoot | Out-Null
        $lockPath = Join-Path $stagingRoot 'artifact-lock.json'
        Write-TestLock -Lock (New-ValidLlamaLock) -Path $lockPath

        {
            Read-ArtifactLock -Path $lockPath -ArtifactRoot $stagingRoot
        } | Should -Throw '*.part*'

        $publishedRoot = Join-Path $TestDrive 'llama.cpp-omni-b267'
        New-Item -ItemType Directory -Path $publishedRoot | Out-Null
        $publishedLockPath = Join-Path $publishedRoot 'artifact-lock.json'
        Write-TestLock -Lock (New-ValidLlamaLock) -Path $publishedLockPath

        {
            Read-ArtifactLock -Path $publishedLockPath -ArtifactRoot $publishedRoot
        } | Should -Throw '*READY*'

        New-Item -ItemType File -Path (Join-Path $publishedRoot 'READY') | Out-Null
        $lock = Read-ArtifactLock -Path $publishedLockPath -ArtifactRoot $publishedRoot
        $lock.Commit | Should -BeExactly $expectedCommit
    }
}

Describe 'Assert-ArtifactHash' {
    It 'returns the canonical SHA-256 for a matching file' {
        $artifactPath = Join-Path $TestDrive 'artifact.bin'
        [System.IO.File]::WriteAllBytes($artifactPath, [byte[]](0..31))
        $expectedHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()

        $result = Assert-ArtifactHash -Path $artifactPath -Sha256 $expectedHash

        $result.Path | Should -BeExactly (Resolve-Path -LiteralPath $artifactPath).Path
        $result.Sha256 | Should -BeExactly $expectedHash
        $result.Length | Should -Be 32
    }

    It 'rejects a hash mismatch' {
        $artifactPath = Join-Path $TestDrive 'changed.bin'
        [System.IO.File]::WriteAllBytes($artifactPath, [byte[]](1..8))
        $wrongHash = '0' * 64

        { Assert-ArtifactHash -Path $artifactPath -Sha256 $wrongHash } |
            Should -Throw '*SHA-256 mismatch*'
    }
}

Describe 'Test-LockedArtifactSet' {
    It 'accepts exact locked bytes and rejects a corrupted resumable phase' {
        $root = Join-Path $TestDrive 'phase'
        New-Item -ItemType Directory -Path $root | Out-Null
        $records = @(
            foreach ($name in @('base.gguf', 'acoustic.gguf')) {
                $path = Join-Path $root $name
                "locked:$name" | Set-Content -LiteralPath $path -Encoding utf8NoBOM
                $item = Get-Item -LiteralPath $path
                [pscustomobject]@{
                    name = $name
                    length = [long]$item.Length
                    sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
        )

        Test-LockedArtifactSet -Root $root -Records $records -Names @('base.gguf', 'acoustic.gguf') |
            Should -BeTrue

        'corrupted' | Set-Content -LiteralPath (Join-Path $root 'base.gguf') -Encoding utf8NoBOM
        Test-LockedArtifactSet -Root $root -Records $records -Names @('base.gguf', 'acoustic.gguf') |
            Should -BeFalse
    }
}

Describe 'Assert-CleanAuthoringTargets' {
    It 'requires both publication and staging paths to be absent' {
        $output = Join-Path $TestDrive 'published'
        $staging = "$output.part"

        { Assert-CleanAuthoringTargets -OutputRoot $output -StagingRoot $staging } |
            Should -Not -Throw

        New-Item -ItemType Directory -Path $staging | Out-Null
        { Assert-CleanAuthoringTargets -OutputRoot $output -StagingRoot $staging } |
            Should -Throw '*clean authoring*staging*'
    }
}

Describe 'Assert-ExactArtifactInventory' {
    It 'accepts only the declared relative files and rejects extra files or directories' {
        $artifactRoot = Join-Path $TestDrive 'exact-inventory'
        New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
        'one' | Set-Content -LiteralPath (Join-Path $artifactRoot 'one.bin') -Encoding utf8NoBOM
        'two' | Set-Content -LiteralPath (Join-Path $artifactRoot 'two.bin') -Encoding utf8NoBOM

        {
            Assert-ExactArtifactInventory `
                -Root $artifactRoot `
                -AllowedRelativePaths @('one.bin', 'two.bin')
        } | Should -Not -Throw

        'extra' | Set-Content -LiteralPath (Join-Path $artifactRoot 'extra.part') -Encoding utf8NoBOM
        {
            Assert-ExactArtifactInventory `
                -Root $artifactRoot `
                -AllowedRelativePaths @('one.bin', 'two.bin')
        } | Should -Throw '*unexpected*extra.part*'

        Remove-Item -LiteralPath (Join-Path $artifactRoot 'extra.part')
        New-Item -ItemType Directory -Path (Join-Path $artifactRoot 'empty-cache') | Out-Null
        {
            Assert-ExactArtifactInventory `
                -Root $artifactRoot `
                -AllowedRelativePaths @('one.bin', 'two.bin')
        } | Should -Throw '*unexpected*empty-cache*'
    }
}

Describe 'Invoke-CheckedProcess' {
    It 'preserves literal arguments and captures stdout, stderr, exit code and duration' {
        $fixturePath = Join-Path $TestDrive 'process-fixture.ps1'
        @'
param([string]$Value)
[Console]::Out.WriteLine("out:{0}", $Value)
[Console]::Error.WriteLine("err:{0}", $Value)
exit 7
'@ | Set-Content -LiteralPath $fixturePath -Encoding utf8NoBOM
        $literalArgument = 'spaces & shell $(must-not-run)'
        $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source

        $result = Invoke-CheckedProcess `
            -FilePath $pwshPath `
            -ArgumentList @('-NoProfile', '-File', $fixturePath, $literalArgument) `
            -WorkingDirectory $TestDrive

        $result.ExitCode | Should -Be 7
        $result.StdOut.Trim() | Should -BeExactly "out:$literalArgument"
        $result.StdErr.Trim() | Should -BeExactly "err:$literalArgument"
        $result.Duration.TotalMilliseconds | Should -BeGreaterOrEqual 0
    }
}

Describe 'Resolve-Upstream.ps1' {
    It 'publishes a verified tagged Git snapshot and writes READY last' {
        $sourceRoot = Join-Path $TestDrive 'upstream-source'
        $destinationRoot = Join-Path $TestDrive 'resolved'
        $lockPath = Join-Path $TestDrive 'resolved.lock.json'
        New-Item -ItemType Directory -Path $sourceRoot | Out-Null

        & git -C $sourceRoot init --quiet
        & git -C $sourceRoot config user.name 'Spike Test'
        & git -C $sourceRoot config user.email 'spike@example.invalid'
        & git -C $sourceRoot config core.longpaths true
        'MIT License' | Set-Content -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Encoding utf8NoBOM
        'fixture' | Set-Content -LiteralPath (Join-Path $sourceRoot 'fixture.txt') -Encoding utf8NoBOM
        $longDirectory = Join-Path $sourceRoot ('long-segment-' + ('a' * 48))
        $longDirectory = Join-Path $longDirectory ('long-segment-' + ('b' * 48))
        $longDirectory = Join-Path $longDirectory ('long-segment-' + ('c' * 48))
        New-Item -ItemType Directory -Force -Path $longDirectory | Out-Null
        'long-path-fixture' | Set-Content -LiteralPath (Join-Path $longDirectory 'fixture.txt') -Encoding utf8NoBOM
        & git -C $sourceRoot add .
        & git -C $sourceRoot commit --quiet -m 'fixture'
        & git -C $sourceRoot tag b267
        $commit = (& git -C $sourceRoot rev-parse HEAD).Trim()
        $resolverPath = Join-Path $repoRoot 'scripts\spike\Resolve-Upstream.ps1'

        $result = & $resolverPath `
            -Repository $sourceRoot `
            -Tag 'b267' `
            -ExpectedCommit $commit `
            -DestinationRoot $destinationRoot `
            -LockPath $lockPath

        $publishedRoot = Join-Path $destinationRoot 'llama.cpp-omni-b267'
        $result.Path | Should -BeExactly $publishedRoot
        Test-Path -LiteralPath (Join-Path $publishedRoot 'READY') -PathType Leaf | Should -BeTrue
        Test-Path -LiteralPath "$publishedRoot.part" | Should -BeFalse
        $lock = Read-ArtifactLock -Path $lockPath -ArtifactRoot $publishedRoot
        $lock.Commit | Should -BeExactly $commit
        $lock.Tag | Should -BeExactly 'b267'
        $lock.License | Should -BeExactly 'MIT'
    }
}

Describe 'Build-VoxCpmWorker.ps1' {
    It 'emits a pinned CUDA release build plan without invoking a shell' {
        $sourceRoot = Join-Path $TestDrive 'build-source'
        $buildRoot = Join-Path $TestDrive 'build-output'
        New-Item -ItemType Directory -Path $sourceRoot | Out-Null
        $builderPath = Join-Path $repoRoot 'scripts\spike\Build-VoxCpmWorker.ps1'

        $plan = & $builderPath `
            -SourceRoot $sourceRoot `
            -BuildRoot $buildRoot `
            -CudaArchitectures '75' `
            -MsvcToolset '14.38' `
            -PlanOnly

        $plan.Generator | Should -BeExactly 'Ninja'
        $plan.ConfigureArguments | Should -Contain '-DGGML_CUDA=ON'
        $plan.ConfigureArguments | Should -Contain '-DGGML_CUDA_CUB_3DOT2=ON'
        $plan.ConfigureArguments | Should -Contain '-DCMAKE_CUDA_ARCHITECTURES=75'
        $plan.ConfigureArguments | Should -Contain '-DCMAKE_BUILD_TYPE=Release'
        $plan.ConfigureArguments | Should -Contain '-DBUILD_SHARED_LIBS=ON'
        $plan.ConfigureArguments | Should -Contain '-DLLAMA_OPENSSL=OFF'
        $plan.BuildTargets | Should -Be @('voxcpm2-cli', 'llama-quantize')
        $plan.RuntimeDllNames | Should -Be @(
            'cudart64_12.dll',
            'cublas64_12.dll',
            'cublasLt64_12.dll'
        )
        $plan.VerifiedSourceScopes | Should -Contain 'tools/quantize'
        $plan.VerifiedSourceScopes | Should -Contain 'src'
        $plan.VerifiedSourceScopes | Should -Contain 'ggml'
        $plan.VerifiedSourceScopes | Should -Contain ':(glob)**/CMakeLists.txt'
        $plan.VerifiedSourceScopes | Should -Contain ':(glob)**/*.cmake'
        $plan.VerifiedSourceScopes | Should -Contain 'tools/omni/CMakeLists.txt'
        $plan.PatchPath | Should -BeExactly (
            Join-Path $repoRoot 'eng\patches\llama-cpp-omni-b267\0001-voxcpm2-windows-utf8-and-wav-safety.patch'
        )
        $plan.PatchSha256 | Should -BeExactly 'd83350dc53c73713102c9fe9bbc5a0ab984edbeebf59926c6b45272a6c67a26a'
        $plan.PatchedCliSha256 | Should -BeExactly '39c5ceace9275582e5e2fdff88435d402ec9e4fc746e97b0a6cf2e2459ac731c'
        Split-Path $plan.EffectiveSourceRoot -Leaf | Should -Match '^src-b267-voxcpm2-[0-9a-f]{12}$'
        Split-Path $plan.BaselineBuildRoot -Leaf | Should -BeExactly 'build-b267-cuda128-sm75'
        $plan.MsvcToolset | Should -BeExactly '14.38'
    }
}

Describe 'official VoxCPM2 model lock' {
    It 'pins official source hashes, converter commit and five generated artifacts' {
        $lockPath = Join-Path $repoRoot 'eng\locks\voxcpm2.lock.json'

        $lock = Read-ArtifactLock `
            -Path $lockPath `
            -ExpectedRepository 'openbmb/VoxCPM2' `
            -ExpectedLicense 'Apache-2.0'

        $lock.Revision | Should -Match '^[0-9a-f]{40}$'
        $lock.Revision | Should -BeExactly $expectedVoxRevision
        @($lock.Files).Count | Should -BeGreaterThan 0
        @($lock.Files | Where-Object { $_.Sha256 -cnotmatch '^[0-9a-f]{64}$' }).Count | Should -Be 0
        $lock.Converter.Repository | Should -BeExactly $expectedRepository
        $lock.Converter.Tag | Should -BeExactly $expectedTag
        $lock.Converter.Commit | Should -BeExactly $expectedCommit
        $lock.Converter.Commit | Should -Match '^[0-9a-f]{40}$'
        @($lock.Generated.Files).Count | Should -Be 5
        @($lock.Generated.Files.Name | Sort-Object) | Should -Be @(
            'VoxCPM2-Acoustic-F16.gguf',
            'VoxCPM2-Acoustic-F32.gguf',
            'VoxCPM2-BaseLM-F16.gguf',
            'VoxCPM2-BaseLM-F32.gguf',
            'VoxCPM2-BaseLM-Q8_0.gguf'
        )
        @($lock.Generated.Files | Where-Object { $_.Sha256 -cnotmatch '^[0-9a-f]{64}$' }).Count | Should -Be 0
    }

    It 'rejects a floating main revision' {
        $lockData = New-ValidVoxCpmLock
        $lockData.revision = 'main'
        $lockPath = Join-Path $TestDrive 'floating-model.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*revision*'
    }

    It 'rejects an upstream file without a SHA-256 hash' {
        $lockData = New-ValidVoxCpmLock
        $lockData.files[0].sha256 = $null
        $lockPath = Join-Path $TestDrive 'unhashed-model.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*SHA-256*'
    }

    It 'rejects a generated artifact without a SHA-256 hash' {
        $lockData = New-ValidVoxCpmLock
        $lockData.generated.files = @(
            [ordered]@{
                name   = 'VoxCPM2-BaseLM-F16.gguf'
                length = 456
                sha256 = $null
            }
        )
        $lockPath = Join-Path $TestDrive 'unhashed-generated-model.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*generated*SHA-256*'
    }

    It 'rejects community GGUF sources' {
        $lockData = New-ValidVoxCpmLock
        $lockData.repository = 'DennisHuang648/VoxCPM2-GGUF'
        $lockPath = Join-Path $TestDrive 'community-model.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*official*'

        $lockData = New-ValidVoxCpmLock
        $lockData.artifact = 'renamed-artifact'
        $lockData.repository = 'DennisHuang648/renamed-community-copy'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*community*'
    }

    It 'rejects a changed VoxCPM2 artifact identity before applying model policy' {
        $lockData = New-ValidVoxCpmLock
        $lockData.artifact = 'voxcpm2'
        $lockPath = Join-Path $TestDrive 'wrong-model-artifact.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*artifact*VoxCPM2*'
    }

    It 'rejects any nonofficial VoxCPM2 repository or license' {
        $lockData = New-ValidVoxCpmLock
        $lockData.repository = 'example/VoxCPM2'
        $lockPath = Join-Path $TestDrive 'wrong-official-model.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath
        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*official*'

        $lockData = New-ValidVoxCpmLock
        $lockData.license = 'MIT'
        Write-TestLock -Lock $lockData -Path $lockPath
        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*Apache-2.0*'
    }

    It 'rejects missing converter provenance and unsafe or duplicate source paths' {
        $lockData = New-ValidVoxCpmLock
        $lockData.Remove('converter')
        $lockPath = Join-Path $TestDrive 'missing-converter.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath
        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*converter*'

        $lockData = New-ValidVoxCpmLock
        $lockData.files[0].path = '..\config.json'
        Write-TestLock -Lock $lockData -Path $lockPath
        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*safe relative path*'

        $lockData = New-ValidVoxCpmLock
        $lockData.files += [ordered]@{
            path = 'CONFIG.json'; length = 123; sha256 = 'd' * 64
        }
        Write-TestLock -Lock $lockData -Path $lockPath
        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*duplicate*'
    }

    It 'requires the complete unique five-file VoxCPM2 generated set once populated' {
        $lockData = New-ValidVoxCpmLock
        $lockData.generated.files = @(
            [ordered]@{
                name = 'VoxCPM2-BaseLM-F16.gguf'; length = 456; sha256 = 'e' * 64
            }
        )
        $lockPath = Join-Path $TestDrive 'incomplete-generated.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*exactly five*'
    }

    It 'requires the complete nine-file official source snapshot' {
        $lockData = New-ValidVoxCpmLock
        $lockData.files = @($lockData.files | Where-Object { $_.path -cne 'model.safetensors' })
        $lockPath = Join-Path $TestDrive 'incomplete-source.lock.json'
        Write-TestLock -Lock $lockData -Path $lockPath

        { Read-ArtifactLock -Path $lockPath } | Should -Throw '*nine canonical source files*'
    }
}

Describe 'Get-VoxCpmModel.ps1' {
    It 'emits a reproducible official-model acquisition plan' {
        $destinationRoot = Join-Path $TestDrive 'models'
        $venvPath = Join-Path $TestDrive '.venv-spike'
        $downloaderPath = Join-Path $repoRoot 'scripts\spike\Get-VoxCpmModel.ps1'

        $plan = & $downloaderPath `
            -Revision $expectedVoxRevision `
            -DestinationRoot $destinationRoot `
            -VenvPath $venvPath `
            -PlanOnly

        $plan.Repository | Should -BeExactly 'openbmb/VoxCPM2'
        $plan.Revision | Should -BeExactly $expectedVoxRevision
        $plan.License | Should -BeExactly 'Apache-2.0'
        $plan.Transport | Should -BeExactly 'HuggingFace'
        $plan.AvailableTransports | Should -Be @('HuggingFace', 'ModelScope')
        $plan.HuggingFaceHubVersion | Should -BeExactly '0.36.0'
        $plan.PythonPackages | Should -Contain 'torch==2.8.0'
        $plan.PythonPackages | Should -Contain 'hf-xet==1.6.0'
        $plan.PythonPackages | Should -Contain 'numpy==2.2.6'
        $plan.PublishedRoot | Should -Not -Match '\.part$'
        $plan.StagingRoot | Should -BeExactly "$($plan.PublishedRoot).part"
        $plan.PreservePartialDownloads | Should -BeTrue
        $plan.CanonicalLockIsAuthority | Should -BeTrue
        $plan.UpdateLock | Should -BeFalse
        $plan.ValidatesPinnedEnvironmentBeforeNetwork | Should -BeTrue
        $plan.ValidatesHuggingFaceGitBlobIds | Should -BeTrue
        $plan.DownloadHeadroomBytes | Should -Be 1GB
        $plan.CreditsOnlyVerifiedStagingFiles | Should -BeTrue
        $plan.AccountsForModelScopeMergeBuffer | Should -BeTrue
        $plan.UsesXetDownloads | Should -BeTrue
        $plan.CanonicalHashesRequired | Should -BeTrue
        $plan.ReadyMarkerCreatedBeforeAtomicPublish | Should -BeTrue
        $plan.CleansKnownTransportPartFiles | Should -BeTrue
        $plan.RejectsUnexpectedPublishedEntries | Should -BeTrue
        $plan.ReusesVerifiedArtifactBeforePythonSetup | Should -BeTrue
    }

    It 'uses the pinned official ModelScope mirror only as a hash-verified transport' {
        $destinationRoot = Join-Path $TestDrive 'models'
        $venvPath = Join-Path $TestDrive '.venv-spike'
        $downloaderPath = Join-Path $repoRoot 'scripts\spike\Get-VoxCpmModel.ps1'

        $plan = & $downloaderPath `
            -Revision $expectedVoxRevision `
            -Transport ModelScope `
            -DestinationRoot $destinationRoot `
            -VenvPath $venvPath `
            -PlanOnly

        $plan.Transport | Should -BeExactly 'ModelScope'
        $plan.ModelScopeRepository | Should -BeExactly 'OpenBMB/VoxCPM2'
        $plan.ModelScopeRevision | Should -BeExactly '519da200db2bc31a0395635fc7042653b9c4c862'
        $plan.ModelScopeRevision | Should -Match '^[0-9a-f]{40}$'
        $plan.ModelScopeHubVersion | Should -BeExactly '0.4.5'
        $plan.ModelScopePackages | Should -Be @('modelscope-hub==0.4.5')
        $plan.ModelScopeExcludedFiles | Should -Be @('.gitattributes')
        $plan.ModelScopeTransferFiles | Should -Be @('audiovae.pth', 'model.safetensors')
        $plan.ModelScopePartSizeMB | Should -Be 160
        $plan.ModelScopeRangeWorkers | Should -Be 4
        $plan.ModelScopeTimeoutSeconds | Should -Be 600
        $plan.ModelScopeMaxRetries | Should -Be 20
        $plan.ModelScopePerFileHashVerification | Should -BeTrue
        $plan.CanonicalHashAuthority | Should -BeExactly 'HuggingFaceRevision'
        $plan.CanonicalHashesRequired | Should -BeTrue
    }

    It 'rejects any alternate revision for the pinned spike' {
        $downloaderPath = Join-Path $repoRoot 'scripts\spike\Get-VoxCpmModel.ps1'

        {
            & $downloaderPath -Revision ('a' * 40) -PlanOnly
        } | Should -Throw '*pinned VoxCPM2 revision*'
    }

    It 'normalizes case-insensitive transport input before strict runtime branching' {
        $downloaderPath = Join-Path $repoRoot 'scripts\spike\Get-VoxCpmModel.ps1'

        (& $downloaderPath -Transport 'modelscope' -PlanOnly).Transport |
            Should -BeExactly 'ModelScope'
        (& $downloaderPath -Transport 'huggingface' -PlanOnly).Transport |
            Should -BeExactly 'HuggingFace'
    }

    It 'keeps an existing canonical lock authoritative when reusing local weights' {
        $destinationRoot = Join-Path $TestDrive 'models'
        $publishedRoot = Join-Path $destinationRoot "VoxCPM2-$($expectedVoxRevision.Substring(0, 12))"
        $canonicalLockPath = Join-Path $TestDrive 'canonical-voxcpm2.lock.json'
        New-Item -ItemType Directory -Force -Path $publishedRoot | Out-Null

        $localLock = New-ValidVoxCpmLock
        $localLock.files = @(
            foreach ($sourceName in @($localLock.files.path)) {
                $sourcePath = Join-Path $publishedRoot $sourceName
                "fixture:$sourceName" | Set-Content -LiteralPath $sourcePath -Encoding utf8NoBOM
                $sourceItem = Get-Item -LiteralPath $sourcePath
                [ordered]@{
                    path = $sourceName
                    length = [long]$sourceItem.Length
                    sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
        )
        Write-TestLock -Lock $localLock -Path (Join-Path $publishedRoot 'artifact-lock.json')
        New-Item -ItemType File -Path (Join-Path $publishedRoot 'READY') | Out-Null

        $canonicalLock = New-ValidVoxCpmLock
        $canonicalLock.files = $localLock.files
        $canonicalGeneratedNames = @(
            'VoxCPM2-Acoustic-F16.gguf',
            'VoxCPM2-Acoustic-F32.gguf',
            'VoxCPM2-BaseLM-F16.gguf',
            'VoxCPM2-BaseLM-F32.gguf',
            'VoxCPM2-BaseLM-Q8_0.gguf'
        )
        $canonicalLock.generated.files = @(
            foreach ($name in $canonicalGeneratedNames) {
                [ordered]@{ name = $name; length = 456; sha256 = 'c' * 64 }
            }
        )
        Write-TestLock -Lock $canonicalLock -Path $canonicalLockPath
        $canonicalHashBefore = (Get-FileHash -LiteralPath $canonicalLockPath -Algorithm SHA256).Hash

        $downloaderPath = Join-Path $repoRoot 'scripts\spike\Get-VoxCpmModel.ps1'
        Mock Get-Command {
            throw 'Python discovery must not run while reusing a verified artifact.'
        } -ParameterFilter { $Name -eq 'py' }

        $result = & $downloaderPath `
            -Revision $expectedVoxRevision `
            -DestinationRoot $destinationRoot `
            -VenvPath (Join-Path $TestDrive 'missing-venv') `
            -LockPath $canonicalLockPath

        $result.Reused | Should -BeTrue
        (Get-FileHash -LiteralPath $canonicalLockPath -Algorithm SHA256).Hash |
            Should -BeExactly $canonicalHashBefore
        $reloadedCanonical = Read-ArtifactLock -Path $canonicalLockPath
        @($reloadedCanonical.Generated.Files).Count | Should -Be 5
    }

    It 'never authors the canonical lock from an existing self-authored local artifact' {
        $destinationRoot = Join-Path $TestDrive 'untrusted-models'
        $publishedRoot = Join-Path $destinationRoot "VoxCPM2-$($expectedVoxRevision.Substring(0, 12))"
        $canonicalLockPath = Join-Path $TestDrive 'missing-canonical.lock.json'
        New-Item -ItemType Directory -Force -Path $publishedRoot | Out-Null

        $localLock = New-ValidVoxCpmLock
        $localLock.files = @(
            foreach ($sourceName in @($localLock.files.path)) {
                $sourcePath = Join-Path $publishedRoot $sourceName
                "untrusted:$sourceName" | Set-Content -LiteralPath $sourcePath -Encoding utf8NoBOM
                $sourceItem = Get-Item -LiteralPath $sourcePath
                [ordered]@{
                    path = $sourceName
                    length = [long]$sourceItem.Length
                    sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
        )
        Write-TestLock -Lock $localLock -Path (Join-Path $publishedRoot 'artifact-lock.json')
        New-Item -ItemType File -Path (Join-Path $publishedRoot 'READY') | Out-Null

        $downloaderPath = Join-Path $repoRoot 'scripts\spike\Get-VoxCpmModel.ps1'
        {
            & $downloaderPath `
                -Revision $expectedVoxRevision `
                -DestinationRoot $destinationRoot `
                -VenvPath (Join-Path $TestDrive '.venv-spike') `
                -LockPath $canonicalLockPath `
                -UpdateLock
        } | Should -Throw '*canonical lock*existing local*'
        Test-Path -LiteralPath $canonicalLockPath | Should -BeFalse
    }
}

Describe 'Convert-VoxCpmModel.ps1' {
    It 'emits the locked five-artifact conversion plan' {
        $modelRoot = Join-Path $TestDrive 'source-model'
        $outputRoot = Join-Path $TestDrive 'generated-models'
        $venvPath = Join-Path $TestDrive '.venv-spike'
        $converterPath = Join-Path $repoRoot 'scripts\spike\Convert-VoxCpmModel.ps1'

        $plan = & $converterPath `
            -ModelRoot $modelRoot `
            -OutputRoot $outputRoot `
            -VenvPath $venvPath `
            -PlanOnly

        $plan.ConverterCommit | Should -BeExactly $expectedCommit
        $plan.ConversionDtypes | Should -Be @('f16', 'f32')
        $plan.Quantization.Type | Should -BeExactly 'Q8_0'
        $plan.Quantization.Source | Should -BeExactly 'VoxCPM2-BaseLM-F32.gguf'
        $plan.Quantization.Output | Should -BeExactly 'VoxCPM2-BaseLM-Q8_0.gguf'
        @($plan.ExpectedArtifacts | Sort-Object) | Should -Be @(
            'VoxCPM2-Acoustic-F16.gguf',
            'VoxCPM2-Acoustic-F32.gguf',
            'VoxCPM2-BaseLM-F16.gguf',
            'VoxCPM2-BaseLM-F32.gguf',
            'VoxCPM2-BaseLM-Q8_0.gguf'
        )
        $plan.OutputRoot | Should -Not -Match '\.part$'
        $plan.StagingRoot | Should -BeExactly "$($plan.OutputRoot).part"
        $plan.BuildManifestPath | Should -Match 'voxcpm2-worker-manifest\.json$'
        $plan.CanonicalLockIsAuthority | Should -BeTrue
        $plan.UpdateLock | Should -BeFalse
        $plan.ValidatesPinnedPythonEnvironment | Should -BeTrue
        $plan.ValidatesExistingGgufSemantics | Should -BeTrue
        $plan.ReadyMarkerCreatedBeforeAtomicPublish | Should -BeTrue
        $plan.ChecksDiskBeforeRemovingStaging | Should -BeTrue
        $plan.PreservesValidatedStagingArtifacts | Should -BeTrue
        $plan.FailureLeavesStagingForResume | Should -BeTrue
        $plan.RejectsUnexpectedStagingEntries | Should -BeTrue
        $plan.VerifiedSourceScopes | Should -Contain 'tools/quantize'
        $plan.VerifiedSourceScopes | Should -Contain ':(glob)**/CMakeLists.txt'
        $plan.VerifiedSourceScopes | Should -Contain ':(glob)**/*.cmake'
        $plan.RequiredBuildArtifacts | Should -Contain 'llama-quantize.exe'
        $plan.RequiredBuildArtifacts | Should -Contain 'llama-quantize-impl.dll'
        $plan.ForcesUtf8ChildProcesses | Should -BeTrue
        $plan.RequiresCleanFirstAuthoring | Should -BeTrue
        $plan.RevalidatesResumedBytesAgainstCanonicalLock | Should -BeTrue
        $plan.VerifiedSourceScopes | Should -Contain 'tools/omni/CMakeLists.txt'
    }
}

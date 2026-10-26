[CmdletBinding()]
param(
    [string]$SourceRoot,
    [string]$BuildRoot,
    [string]$CudaArchitectures = '75',
    [string]$MsvcToolset = '14.38',
    [int]$Parallel = [Math]::Min([Environment]::ProcessorCount, 8),
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repoRoot 'src\SpikeTools\SpikeTools.psm1') -Force

$patchPath = Join-Path $repoRoot 'eng\patches\llama-cpp-omni-b267\0001-voxcpm2-windows-utf8-and-wav-safety.patch'
$expectedPatchSha256 = 'd83350dc53c73713102c9fe9bbc5a0ab984edbeebf59926c6b45272a6c67a26a'
$expectedSourceCliSha256 = '702c9ca79af0312a28ae073c239bb1834761daf6f5de81202761dd665d5bb19e'
$expectedPatchedCliSha256 = '39c5ceace9275582e5e2fdff88435d402ec9e4fc746e97b0a6cf2e2459ac731c'
$patchWorkspaceId = 'f958345897f5'

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $repoRoot '.tools\upstream\llama.cpp-omni-b267'
}

if ($CudaArchitectures -notmatch '^\d+(?:;\d+)*$') {
    throw "CudaArchitectures must be a semicolon-delimited list of numeric architectures: $CudaArchitectures"
}

if ($MsvcToolset -notmatch '^14\.38(?:\.\d+)?$') {
    throw "CUDA 12.8 worker builds require the supported MSVC 14.38 toolset, got '$MsvcToolset'."
}

$sourceFull = [System.IO.Path]::GetFullPath($SourceRoot)
$sourcePathBytes = [System.Text.Encoding]::UTF8.GetBytes($sourceFull.ToLowerInvariant())
$sourcePathHash = [Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData($sourcePathBytes)
).Substring(0, 12).ToLowerInvariant()
$shortExecutionRoot = Join-Path $env:LOCALAPPDATA "VoxBookStudio\spike\$sourcePathHash"
$effectiveSourceRoot = Join-Path $shortExecutionRoot "src-b267-voxcpm2-$patchWorkspaceId"
$baselineBuildRoot = Join-Path $shortExecutionRoot 'build-b267-cuda128-sm75'
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot = Join-Path $shortExecutionRoot "build-b267-cuda128-sm75-p$patchWorkspaceId"
}
$buildFull = [System.IO.Path]::GetFullPath($BuildRoot)
$configureArguments = @(
    '-S', $effectiveSourceRoot,
    '-B', $buildFull,
    '-G', 'Ninja',
    '-DCMAKE_BUILD_TYPE=Release',
    '-DBUILD_SHARED_LIBS=ON',
    '-DGGML_CUDA=ON',
    '-DGGML_CUDA_CUB_3DOT2=ON',
    "-DCMAKE_CUDA_ARCHITECTURES=$CudaArchitectures",
    '-DGGML_NATIVE=OFF',
    '-DGGML_CCACHE=OFF',
    '-DLLAMA_OPENSSL=OFF',
    '-DLLAMA_BUILD_TESTS=ON',
    '-DLLAMA_BUILD_TOOLS=ON',
    '-DLLAMA_BUILD_SERVER=OFF',
    '-DLLAMA_BUILD_APP=OFF',
    '-DLLAMA_BUILD_UI=OFF',
    '-DLLAMA_BUILD_EXAMPLES=OFF'
)
$buildTargets = @('voxcpm2-cli', 'llama-quantize')
$runtimeDllNames = @(
    'cudart64_12.dll',
    'cublas64_12.dll',
    'cublasLt64_12.dll'
)
$verifiedSourceScopes = @(
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
$buildArguments = @(
    '--build', $buildFull,
    '--config', 'Release',
    '--parallel', [string]$Parallel,
    '--target'
) + $buildTargets

$plan = [pscustomobject]@{
    Generator          = 'Ninja'
    SourceRoot         = $sourceFull
    EffectiveSourceRoot = $effectiveSourceRoot
    BaselineBuildRoot   = $baselineBuildRoot
    BuildRoot          = $buildFull
    PatchPath          = $patchPath
    PatchSha256        = $expectedPatchSha256
    PatchedCliSha256   = $expectedPatchedCliSha256
    CudaArchitectures  = $CudaArchitectures
    MsvcToolset        = $MsvcToolset
    ConfigureArguments = [string[]]$configureArguments
    BuildArguments     = [string[]]$buildArguments
    BuildTargets       = [string[]]$buildTargets
    RuntimeDllNames    = [string[]]$runtimeDllNames
    VerifiedSourceScopes = [string[]]$verifiedSourceScopes
}

if ($PlanOnly) {
    return $plan
}

if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
    throw "llama.cpp-omni source root does not exist: $sourceFull"
}

New-Item -ItemType Directory -Force -Path $shortExecutionRoot | Out-Null

$sourceLockPath = Join-Path $sourceFull 'artifact-lock.json'
$sourceLock = Read-ArtifactLock `
    -Path $sourceLockPath `
    -ExpectedRepository 'https://github.com/tc-mb/llama.cpp-omni.git' `
    -ExpectedTag 'b267' `
    -ExpectedLicense 'MIT' `
    -ArtifactRoot $sourceFull
if ([string]$sourceLock.commit -cne '873056743b74e1a4ce5dcf7290e2298428e214db') {
    throw "Source lock has unexpected commit '$($sourceLock.commit)'."
}

$gitPath = (Get-Command git -ErrorAction Stop).Source
$headResult = Invoke-CheckedProcess `
    -FilePath $gitPath `
    -ArgumentList @('-C', $sourceFull, 'rev-parse', 'HEAD') `
    -WorkingDirectory $sourceFull
if ($headResult.ExitCode -ne 0 -or $headResult.StdOut.Trim() -cne $sourceLock.commit) {
    throw "Worker source checkout HEAD is not the locked commit '$($sourceLock.commit)'."
}
$statusResult = Invoke-CheckedProcess `
    -FilePath $gitPath `
    -ArgumentList (@(
        '-C', $sourceFull,
        'status', '--porcelain', '--untracked-files=all', '--'
    ) + $verifiedSourceScopes) `
    -WorkingDirectory $sourceFull
if ($statusResult.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($statusResult.StdOut)) {
    throw "Worker build dependency tree is dirty at the locked commit.`n$($statusResult.StdOut)$($statusResult.StdErr)"
}

if (-not (Test-Path -LiteralPath $patchPath -PathType Leaf)) {
    throw "Pinned worker patch is missing: $patchPath"
}
$patchSha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($patchSha256 -cne $expectedPatchSha256) {
    throw "Pinned worker patch hash mismatch: expected $expectedPatchSha256, got $patchSha256."
}
$sourceCliPath = Join-Path $sourceFull 'tools\omni\voxcpm2\voxcpm2_cli.cpp'
if ((Get-FileHash -LiteralPath $sourceCliPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedSourceCliSha256) {
    throw 'Pinned upstream VoxCPM2 CLI source hash does not match the reviewed baseline.'
}

if (-not (Test-Path -LiteralPath $effectiveSourceRoot -PathType Container)) {
    $worktreeResult = Invoke-CheckedProcess `
        -FilePath $gitPath `
        -ArgumentList @('-C', $sourceFull, 'worktree', 'add', '--detach', $effectiveSourceRoot, $sourceLock.commit) `
        -WorkingDirectory $sourceFull
    if ($worktreeResult.ExitCode -ne 0) {
        throw "Could not create the isolated patched worker source tree.`n$($worktreeResult.StdErr)"
    }
}
$patchedHeadResult = Invoke-CheckedProcess `
    -FilePath $gitPath `
    -ArgumentList @('-C', $effectiveSourceRoot, 'rev-parse', 'HEAD') `
    -WorkingDirectory $effectiveSourceRoot
if ($patchedHeadResult.ExitCode -ne 0 -or $patchedHeadResult.StdOut.Trim() -cne $sourceLock.commit) {
    throw 'Patched worker source tree is not at the locked upstream commit.'
}
$patchedCliPath = Join-Path $effectiveSourceRoot 'tools\omni\voxcpm2\voxcpm2_cli.cpp'
$patchedCliSha256 = (Get-FileHash -LiteralPath $patchedCliPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($patchedCliSha256 -ceq $expectedSourceCliSha256) {
    $patchResult = Invoke-CheckedProcess `
        -FilePath $gitPath `
        -ArgumentList @('-C', $effectiveSourceRoot, 'apply', '--recount', $patchPath) `
        -WorkingDirectory $effectiveSourceRoot
    if ($patchResult.ExitCode -ne 0) {
        throw "Could not apply the pinned worker patch.`n$($patchResult.StdErr)"
    }
    $patchedCliSha256 = (Get-FileHash -LiteralPath $patchedCliPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($patchedCliSha256 -cne $expectedPatchedCliSha256) {
    throw "Patched VoxCPM2 CLI source hash mismatch: expected $expectedPatchedCliSha256, got $patchedCliSha256."
}
$patchedStatusResult = Invoke-CheckedProcess `
    -FilePath $gitPath `
    -ArgumentList @('-C', $effectiveSourceRoot, 'status', '--porcelain', '--untracked-files=all') `
    -WorkingDirectory $effectiveSourceRoot
$expectedPatchedStatus = 'M tools/omni/voxcpm2/voxcpm2_cli.cpp'
if ($patchedStatusResult.ExitCode -ne 0 -or $patchedStatusResult.StdOut.Trim() -cne $expectedPatchedStatus) {
    throw "Patched worker tree contains changes outside the reviewed patch.`n$($patchedStatusResult.StdOut)$($patchedStatusResult.StdErr)"
}

$vsWherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vsWherePath -PathType Leaf)) {
    throw "Visual Studio locator is missing: $vsWherePath"
}

$vsWhereResult = Invoke-CheckedProcess `
    -FilePath $vsWherePath `
    -ArgumentList @(
        '-latest', '-products', '*',
        '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
        '-property', 'installationPath'
    ) `
    -WorkingDirectory $repoRoot
if ($vsWhereResult.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($vsWhereResult.StdOut)) {
    throw "Visual Studio Build Tools could not be resolved. $($vsWhereResult.StdErr)"
}

$vsRoot = $vsWhereResult.StdOut.Trim()
$devShellModule = Join-Path $vsRoot 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
if (-not (Test-Path -LiteralPath $devShellModule -PathType Leaf)) {
    throw "Visual Studio Developer PowerShell module is missing: $devShellModule"
}

Import-Module $devShellModule -Force
Enter-VsDevShell `
    -VsInstallPath $vsRoot `
    -SkipAutomaticLocation `
    -DevCmdArguments "-arch=x64 -host_arch=x64 -vcvars_ver=$MsvcToolset"

$cudaRoot = [Environment]::GetEnvironmentVariable('CUDA_PATH_V12_8', 'Machine')
if ([string]::IsNullOrWhiteSpace($cudaRoot)) {
    $cudaRoot = [Environment]::GetEnvironmentVariable('CUDA_PATH', 'Machine')
}
if ([string]::IsNullOrWhiteSpace($cudaRoot) -or
    -not (Test-Path -LiteralPath (Join-Path $cudaRoot 'bin\nvcc.exe') -PathType Leaf)) {
    throw 'CUDA Toolkit 12.8 was not found in the machine environment.'
}

$env:CUDA_PATH = $cudaRoot
$env:CUDA_PATH_V12_8 = $cudaRoot
$standaloneCMakeRoot = 'C:\Program Files\CMake\bin'
$cmakePath = Join-Path $standaloneCMakeRoot 'cmake.exe'
$ctestPath = Join-Path $standaloneCMakeRoot 'ctest.exe'
$ninjaPath = Get-ChildItem `
    -Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Ninja-build.Ninja_*\ninja.exe') `
    -File `
    -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not (Test-Path -LiteralPath $cmakePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $ctestPath -PathType Leaf)) {
    throw 'Standalone Kitware CMake is missing from C:\Program Files\CMake\bin.'
}
if ([string]::IsNullOrWhiteSpace($ninjaPath) -or
    -not (Test-Path -LiteralPath $ninjaPath -PathType Leaf)) {
    throw 'Standalone Ninja installed by winget could not be found.'
}
$env:Path = "$(Join-Path $cudaRoot 'bin');$standaloneCMakeRoot;$(Split-Path -Parent $ninjaPath);$env:Path"

$clCommand = Get-Command cl -ErrorAction Stop
$nvccCommand = Get-Command nvcc -ErrorAction Stop
$cmakeCommand = Get-Item -LiteralPath $cmakePath
$ninjaCommand = Get-Item -LiteralPath $ninjaPath
$ctestCommand = Get-Item -LiteralPath $ctestPath

New-Item -ItemType Directory -Force -Path $buildFull | Out-Null
$logRoot = Join-Path $buildFull 'logs'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

function Invoke-BuildProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$Arguments = @(),

        [string]$WorkingDirectory = $repoRoot
    )

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

$clVersion = Invoke-BuildProcess -Name 'cl-version' -FilePath $clCommand.Source -Arguments @('/Bv', '/?')
$clVersionText = "$($clVersion.StdOut)`n$($clVersion.StdErr)"
if ($clVersionText -notmatch '19\.38\.') {
    throw "Developer shell selected an unsupported compiler instead of MSVC 19.38: $clVersionText"
}

$nvccVersion = Invoke-BuildProcess -Name 'nvcc-version' -FilePath $nvccCommand.Source -Arguments @('--version')
if ($nvccVersion.StdOut -notmatch 'release 12\.8') {
    throw "Expected CUDA compiler 12.8, got: $($nvccVersion.StdOut)"
}

$cmakeVersion = Invoke-BuildProcess -Name 'cmake-version' -FilePath $cmakeCommand.FullName -Arguments @('--version')
$ninjaVersion = Invoke-BuildProcess -Name 'ninja-version' -FilePath $ninjaCommand.FullName -Arguments @('--version')

$executionConfigureArguments = [System.Collections.Generic.List[string]]::new()
foreach ($argument in $configureArguments) {
    $executionConfigureArguments.Add($argument)
}
$executionConfigureArguments.Add("-DCUDAToolkit_ROOT=$cudaRoot")
$executionConfigureArguments.Add("-DCMAKE_CUDA_COMPILER=$($nvccCommand.Source)")
$executionConfigureArguments.Add("-DCMAKE_MAKE_PROGRAM=$($ninjaCommand.FullName)")

$null = Invoke-BuildProcess `
    -Name 'cmake-configure' `
    -FilePath $cmakeCommand.FullName `
    -Arguments $executionConfigureArguments.ToArray() `
    -WorkingDirectory $effectiveSourceRoot
$null = Invoke-BuildProcess `
    -Name 'cmake-build' `
    -FilePath $cmakeCommand.FullName `
    -Arguments $buildArguments `
    -WorkingDirectory $effectiveSourceRoot
$null = Invoke-BuildProcess `
    -Name 'ctest-voxcpm2-cli-smoke' `
    -FilePath $ctestCommand.FullName `
    -Arguments @(
        '--test-dir', $buildFull,
        '-C', 'Release',
        '-R', '^voxcpm2-cli-smoke$',
        '--output-on-failure'
    ) `
    -WorkingDirectory $effectiveSourceRoot

$binaryRoot = Join-Path $buildFull 'bin'
$cliPath = Join-Path $binaryRoot 'voxcpm2-cli.exe'
$quantizePath = Join-Path $binaryRoot 'llama-quantize.exe'
foreach ($requiredPath in @($cliPath, $quantizePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required build artifact is missing: $requiredPath"
    }
}

$unicodeProbeText = 'Проверка кириллицы: ё, «кавычки» — тире.'
$unicodeProbeResult = Invoke-CheckedProcess `
    -FilePath $cliPath `
    -ArgumentList @(
        '-t', $unicodeProbeText,
        (Join-Path $buildFull 'missing-base.gguf'),
        (Join-Path $buildFull 'missing-acoustic.gguf')
    ) `
    -WorkingDirectory $binaryRoot
if ($unicodeProbeResult.ExitCode -eq 0 -or $unicodeProbeResult.StdErr -cnotmatch [regex]::Escape($unicodeProbeText)) {
    throw "Patched worker failed the UTF-8 Windows argument probe.`n$($unicodeProbeResult.StdErr)"
}

foreach ($runtimeDllName in $runtimeDllNames) {
    $runtimeSourcePath = Join-Path $cudaRoot "bin\$runtimeDllName"
    $runtimeDestinationPath = Join-Path $binaryRoot $runtimeDllName
    if (-not (Test-Path -LiteralPath $runtimeSourcePath -PathType Leaf)) {
        throw "Required CUDA runtime library is missing: $runtimeSourcePath"
    }

    $copyRequired = -not (Test-Path -LiteralPath $runtimeDestinationPath -PathType Leaf)
    if (-not $copyRequired) {
        $sourceHash = (Get-FileHash -LiteralPath $runtimeSourcePath -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $runtimeDestinationPath -Algorithm SHA256).Hash
        $copyRequired = $sourceHash -cne $destinationHash
    }

    if ($copyRequired) {
        Copy-Item -Force -LiteralPath $runtimeSourcePath -Destination $runtimeDestinationPath
    }
}

$files = @(
    Get-ChildItem -LiteralPath $binaryRoot -File |
        Where-Object { $_.Extension -in @('.exe', '.dll') } |
        Sort-Object Name |
        ForEach-Object {
            [ordered]@{
                name   = $_.Name
                path   = $_.FullName
                length = [long]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)

$manifest = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    source = [ordered]@{
        repository = $sourceLock.repository
        tag = $sourceLock.tag
        commit = $sourceLock.commit
        path = $sourceFull
        shortPath = $effectiveSourceRoot
        patch = [ordered]@{
            fileName = [IO.Path]::GetFileName($patchPath)
            sha256 = $patchSha256
            patchedCliSha256 = $patchedCliSha256
            windowsUtf8Arguments = $true
            unicodePaths = $true
            strictPcm16Input = $true
            checkedWavWrites = $true
        }
    }
    toolchain = [ordered]@{
        msvcToolset = $MsvcToolset
        cl = ($clVersionText -split "`r?`n" | Where-Object { $_ -match '19\.38\.' } | Select-Object -First 1).Trim()
        cudaRoot = $cudaRoot
        nvcc = ($nvccVersion.StdOut -split "`r?`n" | Where-Object { $_ -match 'release 12\.8' } | Select-Object -First 1).Trim()
        cmake = ($cmakeVersion.StdOut -split "`r?`n" | Select-Object -First 1).Trim()
        ninja = $ninjaVersion.StdOut.Trim()
        generator = 'Ninja'
        cudaArchitectures = $CudaArchitectures
    }
    configureArguments = $executionConfigureArguments.ToArray()
    buildTargets = $buildTargets
    runtimeDllNames = $runtimeDllNames
    files = $files
}

$manifestPath = Join-Path $buildFull 'voxcpm2-worker-manifest.json'
$partialManifestPath = "$manifestPath.part"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $partialManifestPath -Encoding utf8NoBOM
Move-Item -Force -LiteralPath $partialManifestPath -Destination $manifestPath

[pscustomobject]@{
    CliPath      = $cliPath
    QuantizePath = $quantizePath
    ManifestPath = $manifestPath
    BinaryRoot   = $binaryRoot
}

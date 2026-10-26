<# Prepares an explicit offline descriptor. Never installs/imports ML or copies weights/cache. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$PythonBaseRoot,
    [Parameter(Mandatory)][string[]]$DependencySites,
    [Parameter(Mandatory)][string]$WorkerRoot,
    [Parameter(Mandatory)][string]$SourceInputRoot,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$ModelRoot,
    [Parameter(Mandatory)][string]$CachePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$TrainingLockPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Local-Path([string]$Value) {
    if ($Value -notmatch '^[A-Za-z]:[\\/]' -or $Value -match '[\x00-\x1f~]' -or $Value -match '(^|[\\/])\.\.?([\\/]|$)') { throw 'An explicit absolute local path is required.' }
    $lasAbsolute = [IO.Path]::GetFullPath($Value).TrimEnd('\')
    if ($lasAbsolute.Length -le 3) { throw 'A drive root is not an input or output directory.' }
    $lasCheck = $lasAbsolute
    while ($lasCheck) {
        try {
            if ([IO.File]::GetAttributes($lasCheck) -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse paths are not allowed.' }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
        $lasCheck = [IO.Path]::GetDirectoryName($lasCheck)
    }
    return $lasAbsolute
}
function Hash-Bytes([byte[]]$Value) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Value)).ToLowerInvariant() }
function Hash-File([string]$Path) {
    # Every caller supplies a path checked by Local-Path/Asset-Path/the tree
    # walker. Recheck the leaf, not every ancestor again for every file. Product
    # launch independently re-pins/re-hashes the complete closed inventory.
    if ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse files are not allowed.' }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Write-New([string]$Path, [byte[]]$Value) {
    $lasStream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $lasStream.Write($Value); $lasStream.Flush($true) } finally { $lasStream.Dispose() }
}
function Json-Bytes($Value) { [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 24 -Compress -EscapeHandling EscapeNonAscii)) }
function Asset-Path([string]$Root, [string]$Relative) {
    if ($Relative -match '[\\:]' -or ($Relative -split '/' | Where-Object { $_ -in '', '.', '..' })) { throw 'Invalid public asset name.' }
    return Local-Path ([IO.Path]::Combine($Root, $Relative.Replace('/', '\')))
}
function Verify-Asset([string]$Root, [string]$Relative, $Expected) {
    $lasPath = Asset-Path $Root $Relative
    if (-not [IO.File]::Exists($lasPath) -or ([IO.FileInfo]::new($lasPath)).Length -ne $Expected.bytes -or (Hash-File $lasPath) -ne $Expected.sha256) { throw 'A public asset does not match the shipped lock.' }
    return $lasPath
}
function Enumerate-Plain([string]$Root) {
    $lasPending = [Collections.Generic.Stack[string]]::new(); $lasPending.Push((Local-Path $Root)); $lasCount = 0
    while ($lasPending.Count -gt 0) {
        $lasCurrent = $lasPending.Pop()
        foreach ($lasPath in [IO.Directory]::EnumerateFileSystemEntries($lasCurrent)) {
            $lasCount++
            if ($lasCount -gt 150000) { throw 'Runtime tree is too large.' }
            $lasPlain = Local-Path $lasPath
            if ([IO.Directory]::Exists($lasPlain)) { $lasPending.Push($lasPlain) } else { $lasPlain }
        }
    }
}
function Header-Layout([string]$Path, [long]$ExpectedBytes) {
    # Bounded .NET/stdlib header read, no Python/model import or weight mapping.
    $lasStream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($lasStream.Length -ne $ExpectedBytes) { throw 'Unexpected base weight size.' }
        $lasPrefix = [byte[]]::new(8); $lasStream.ReadExactly($lasPrefix)
        $lasSize = [BitConverter]::ToUInt64($lasPrefix)
        if ($lasSize -lt 2 -or $lasSize -gt 1048576 -or $lasSize + 8 -ge $lasStream.Length) { throw 'Invalid base header.' }
        $lasBytes = [byte[]]::new([int]$lasSize); $lasStream.ReadExactly($lasBytes)
    } finally { $lasStream.Dispose() }
    $lasHeader = [Text.Encoding]::UTF8.GetString($lasBytes) | ConvertFrom-Json -AsHashtable
    $lasLayout = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($lasName in $lasHeader.Keys) {
        if ($lasName -match '^(base_lm\.|residual_lm\.|feat_decoder\.estimator\.).*\.(q_proj|k_proj|v_proj|o_proj)\.weight$') {
            $lasShape = $lasHeader[$lasName].shape
            if ($lasShape.Count -ne 2 -or $lasShape[0] -lt 1 -or $lasShape[0] -gt 32768 -or $lasShape[1] -lt 1 -or $lasShape[1] -gt 32768) { throw 'Unexpected adapter shape.' }
            $lasStem = $lasName.Substring(0, $lasName.Length - 6)
            $lasLayout[$lasStem+'lora_A'] = [ordered]@{dtype='F32';name=$lasStem+'lora_A';shape=@(2,[int]$lasShape[1])}
            $lasLayout[$lasStem+'lora_B'] = [ordered]@{dtype='F32';name=$lasStem+'lora_B';shape=@([int]$lasShape[0],2)}
        }
    }
    [string[]]$lasNames = @($lasLayout.Keys); [Array]::Sort($lasNames, [StringComparer]::Ordinal)
    return ,@($lasNames | ForEach-Object { $lasLayout[$_] })
}

if (-not $TrainingLockPath) { $TrainingLockPath = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '../../eng/locks/voxcpm2-training.lock.json')) }
$TrainingLockPath = Local-Path $TrainingLockPath
if ((Hash-File $TrainingLockPath) -ne '16946f34805f17a42ebd888f6a69eee1ee3d75cbba6f3f35800b6e538cf2398c') { throw 'Unexpected training lock.' }
$lasLock = Get-Content -Raw -Encoding utf8 -LiteralPath $TrainingLockPath | ConvertFrom-Json
$PythonPath = Local-Path $PythonPath; $PythonBaseRoot = Local-Path $PythonBaseRoot
$WorkerRoot = Local-Path $WorkerRoot; $SourceInputRoot = Local-Path $SourceInputRoot; $SourceRoot = Local-Path $SourceRoot
$ModelRoot = Local-Path $ModelRoot; $CachePath = Local-Path $CachePath; $OutputDirectory = Local-Path $OutputDirectory
$DependencySites = @($DependencySites | ForEach-Object { Local-Path $_ })
if ($DependencySites.Count -lt 1 -or $DependencySites.Count -gt 2 -or (@($DependencySites | Sort-Object -Unique)).Count -ne $DependencySites.Count) { throw 'Provide one or two distinct dependency roots.' }
foreach ($lasExisting in @($PythonBaseRoot,$WorkerRoot,$SourceInputRoot,$ModelRoot)+$DependencySites) { if (-not [IO.Directory]::Exists($lasExisting)) { throw 'A configured input root is missing.' } }
if (-not [IO.File]::Exists($PythonPath) -or -not [IO.File]::Exists($CachePath) -or ([IO.FileInfo]::new($CachePath)).Length -ne $lasLock.cache.bytes) { throw 'The explicit interpreter or retained cache is unavailable.' }
if ((Test-Path -LiteralPath $SourceRoot) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Source/output directories must be new; existing files are never overwritten.' }
foreach ($lasParent in @([IO.Path]::GetDirectoryName($SourceRoot), [IO.Path]::GetDirectoryName($OutputDirectory))) { if (-not [IO.Directory]::Exists($lasParent)) { throw 'Create the intended parent directory first.' } }
foreach ($lasInput in @($PythonBaseRoot,$WorkerRoot,$SourceInputRoot,$ModelRoot)+$DependencySites) {
    foreach ($lasOutput in @($OutputDirectory,$SourceRoot,$CachePath,$CachePath+'.voxcpm-training.lock')) {
        if ($lasOutput.Equals($lasInput,[StringComparison]::OrdinalIgnoreCase) -or $lasOutput.StartsWith($lasInput+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Outputs/cache must not be inside an import/input tree.' }
    }
}
if ([IO.Directory]::GetFileSystemEntries($WorkerRoot).Length -ne 8) { throw 'The worker must contain exactly eight files.' }
foreach ($lasItem in $lasLock.workerFiles.PSObject.Properties) { $null = Verify-Asset $WorkerRoot $lasItem.Name $lasItem.Value }
foreach ($lasItem in $lasLock.sourceFiles.PSObject.Properties) { $null = Verify-Asset $SourceInputRoot $lasItem.Name $lasItem.Value }
$null = Verify-Asset $SourceInputRoot $lasLock.license.filename $lasLock.license
foreach ($lasItem in $lasLock.modelFiles.PSObject.Properties) {
    $lasPath = Asset-Path $ModelRoot $lasItem.Name
    if (-not [IO.File]::Exists($lasPath) -or ([IO.FileInfo]::new($lasPath)).Length -ne $lasItem.Value.bytes) { throw 'A pinned base model file is missing or has the wrong size.' }
}
$lasLayout = Header-Layout (Asset-Path $ModelRoot 'model.safetensors') $lasLock.modelFiles.'model.safetensors'.bytes
if ($lasLayout.Count -ne 384 -or (Hash-Bytes (Json-Bytes $lasLayout)) -ne $lasLock.adapterLayoutSha256) { throw 'The base-derived adapter layout does not match the public lock.' }
$null = [IO.Directory]::CreateDirectory($SourceRoot)
foreach ($lasItem in $lasLock.sourceFiles.PSObject.Properties) {
    $lasFrom = Asset-Path $SourceInputRoot $lasItem.Name; $lasTo = Asset-Path $SourceRoot $lasItem.Name
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($lasTo)); [IO.File]::Copy($lasFrom,$lasTo,$false)
    $null = Verify-Asset $SourceRoot $lasItem.Name $lasItem.Value
}
[IO.File]::Copy((Asset-Path $SourceInputRoot 'LICENSE'),(Asset-Path $SourceRoot 'LICENSE'),$false)
$null = Verify-Asset $SourceRoot 'LICENSE' $lasLock.license
$lasRequired = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($lasTree in @($PythonBaseRoot,$WorkerRoot)+$DependencySites) { foreach ($lasFile in (Enumerate-Plain $lasTree)) { $null = $lasRequired.Add($lasFile) } }
$null = $lasRequired.Add($PythonPath)
$lasInterpreterDirectory = [IO.Path]::GetDirectoryName($PythonPath)
foreach ($lasFile in [IO.Directory]::EnumerateFiles($lasInterpreterDirectory)) { $null = $lasRequired.Add((Local-Path $lasFile)) }
if ([IO.Path]::GetFileName($lasInterpreterDirectory) -ieq 'Scripts') {
    $lasCfg = [IO.Path]::Combine([IO.Path]::GetDirectoryName($lasInterpreterDirectory),'pyvenv.cfg')
    if (-not [IO.File]::Exists($lasCfg)) { throw 'Missing explicit venv configuration.' }
    $null = $lasRequired.Add((Local-Path $lasCfg))
}
if ($lasRequired.Count -gt 100000) { throw 'Runtime inventory exceeds its file limit.' }
$lasFiles = [Collections.Generic.List[object]]::new()
foreach ($lasFile in ($lasRequired | Sort-Object)) { $lasFiles.Add([ordered]@{path=$lasFile;bytes=([IO.FileInfo]::new($lasFile)).Length;sha256=(Hash-File $lasFile)}) }
$lasInventoryBytes = Json-Bytes ([ordered]@{schemaVersion=1;files=@($lasFiles.ToArray())})
if ($lasInventoryBytes.Length -gt 67108864) { throw 'Runtime inventory exceeds its byte limit.' }
$null = [IO.Directory]::CreateDirectory($OutputDirectory)
$lasInventoryPath = [IO.Path]::Combine($OutputDirectory,'runtime-inventory.json')
Write-New $lasInventoryPath $lasInventoryBytes
$lasDescriptor = [ordered]@{pythonPath=$PythonPath;pythonBaseRoot=$PythonBaseRoot;dependencySites=$DependencySites;workerRoot=$WorkerRoot;sourceRoot=$SourceRoot;modelRoot=$ModelRoot;trainingLockPath=$TrainingLockPath;runtimeInventoryPath=$lasInventoryPath;cachePath=$CachePath}
$lasDescriptorPath = [IO.Path]::Combine($OutputDirectory,'runtime-config.json')
Write-New $lasDescriptorPath (Json-Bytes $lasDescriptor)
[pscustomobject]@{descriptorPath=$lasDescriptorPath;sourceFiles=38;licensePreserved=$true;runtimeFiles=$lasRequired.Count;adapterTensors=384;modelCopied=$false;cacheCopied=$false}

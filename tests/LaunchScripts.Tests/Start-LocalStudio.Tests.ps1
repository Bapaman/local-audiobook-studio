param(
    [string]$PowerShellPath = (Get-Process -Id $PID).Path,
    [switch]$VoxOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testTempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $testTempParent ('las-launch-' + [Guid]::NewGuid().ToString('N'))
$testFixture = Join-Path $testRoot (([string][char]0x0416) + ' project with spaces')
$testScript = Join-Path $testFixture 'scripts/app/Start-LocalStudio.ps1'
$testExe = Join-Path $testFixture 'src/LocalAudiobookStudio.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/LocalAudiobookStudio.App.exe'
$testConfig = Join-Path $testFixture (([string][char]0x042F) + ' runtime config.json')
$testRvcConfig = Join-Path $testFixture 'legacy RVC config.json'
$testCorpusConfig = Join-Path $testFixture 'corpus config.json'
$testWrapper = Join-Path $testRoot 'invoke.ps1'
$testPassed = 0

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Invoke-Launch([hashtable]$Parameters) {
    $inputPath = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + '.json')
    $inputJson = @{ script = $testScript; parameters = $Parameters } | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($inputPath, $inputJson, [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $PowerShellPath
    $start.Arguments = '-NoLogo -NoProfile -NonInteractive -File "' + $testWrapper + '" -InputPath "' + $inputPath + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            # Only this explicitly created test interpreter; it cannot launch App/ML.
            $process.Kill()
            $process.WaitForExit()
            throw 'Owned test interpreter timed out.'
        }
        return @{ code = $process.ExitCode; output = $stdout.GetAwaiter().GetResult(); error = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
function Check-Forwarded {
    $result = Invoke-Launch @{
        SkipBuild = $true; SyntheticDataRoot = (Join-Path $testFixture 'data');
        DeveloperRuntimeRoot = (Join-Path $testFixture 'runtime'); DeveloperModelRoot = (Join-Path $testFixture 'models');
        DeveloperLockRoot = (Join-Path $testFixture 'locks');
        TrainingRuntimeConfig = $testRvcConfig; TtsCorpusRuntimeConfig = $testCorpusConfig;
        VoxCpmTrainingRuntimeConfig = $testConfig
    }
    Require ($result.code -eq 0) ('Valid VoxCPM2 configuration was not accepted: ' + $result.error.Trim())
    Require ([string]::IsNullOrWhiteSpace($result.error)) 'Successful launch printed errors.'
    $launch = $result.output | ConvertFrom-Json
    $expected = @('--synthetic-data-root', ('"' + (Join-Path $testFixture 'data') + '"'),
        '--developer-cpu-runtime', ('"' + (Join-Path $testFixture 'runtime') + '"'),
        ('"' + (Join-Path $testFixture 'models') + '"'), ('"' + (Join-Path $testFixture 'locks') + '"'),
        '--training-runtime-config', ('"' + $testRvcConfig + '"'),
        '--tts-corpus-runtime-config', ('"' + $testCorpusConfig + '"'),
        '--voxcpm-training-runtime-config', ('"' + $testConfig + '"'))
    Require (($launch.arguments -join "`n") -ceq ($expected -join "`n")) ('Runtime arguments differ. Actual: ' + ($launch.arguments | ConvertTo-Json -Compress) + '; Expected: ' + ($expected | ConvertTo-Json -Compress))
    Require ($launch.executable -ceq $testExe -and $launch.windowStyle -ceq 'Hidden' -and $launch.passThru) 'Wrong executable or process launch options.'
}
function Check-Rejected([string]$Path) {
    $result = Invoke-Launch @{ SkipBuild = $true; VoxCpmTrainingRuntimeConfig = $Path }
    Require ($result.code -ne 0) 'Invalid configuration was accepted.'
    Require ([string]::IsNullOrWhiteSpace($result.output)) 'Invalid configuration reached Start-Process.'
}
function Case([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:testPassed++
    Write-Output ('PASS ' + $Name)
}

try {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $testScript))
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $testExe))
    [IO.File]::Copy((Join-Path $testRepository 'scripts/app/Start-LocalStudio.ps1'), $testScript)
    [IO.File]::WriteAllBytes($testExe, [byte[]]@())
    foreach ($config in @($testConfig, $testRvcConfig, $testCorpusConfig)) {
        [IO.File]::WriteAllText($config, '{}', [Text.UTF8Encoding]::new($false))
    }
    # Exercise the actual copied script and replace only external side effects.
    [IO.File]::WriteAllText($testWrapper, @'
param([string]$InputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
function dotnet { throw 'Unexpected build: tests must always use SkipBuild.' }
function Start-Process {
    [CmdletBinding()]
    param([string]$FilePath, [string[]]$ArgumentList, [string]$WindowStyle, [switch]$PassThru)
    [ordered]@{ executable = $FilePath; arguments = @($ArgumentList | Where-Object { $null -ne $_ });
        windowStyle = $WindowStyle; passThru = [bool]$PassThru } | ConvertTo-Json -Compress
}
$inputValue = Get-Content -LiteralPath $InputPath -Raw -Encoding UTF8 | ConvertFrom-Json
$parameters = @{}
foreach ($property in $inputValue.parameters.PSObject.Properties) { $parameters[$property.Name] = $property.Value }
& $inputValue.script @parameters
'@, [Text.UTF8Encoding]::new($false))

    if (-not $VoxOnly) {
        Case 'Default launch remains unchanged' {
            $result = Invoke-Launch @{ SkipBuild = $true }
            Require ($result.code -eq 0) 'Default launch failed.'
            $launch = $result.output | ConvertFrom-Json
            Require (@($launch.arguments).Count -eq 0 -and $launch.executable -ceq $testExe) 'Default launch gained unexpected arguments.'
        }
        Case 'Legacy runtime flags remain unchanged without VoxCPM2' {
            $result = Invoke-Launch @{ SkipBuild = $true; TrainingRuntimeConfig = $testRvcConfig; TtsCorpusRuntimeConfig = $testCorpusConfig }
            Require ($result.code -eq 0) 'Legacy configuration failed.'
            $launch = $result.output | ConvertFrom-Json
            $expected = @('--training-runtime-config', ('"' + $testRvcConfig + '"'), '--tts-corpus-runtime-config', ('"' + $testCorpusConfig + '"'))
            Require (($launch.arguments -join "`n") -ceq ($expected -join "`n")) 'Legacy arguments changed.'
        }
    }
    Case 'VoxCPM2 descriptor composes with legacy configurations and Unicode paths' { Check-Forwarded }
    if (-not $VoxOnly) {
        Case 'Relative path is rejected before process launch' { Check-Rejected 'relative.json' }
        Case 'UNC path is rejected before process launch' { Check-Rejected '\\server\share\runtime.json' }
        Case 'Quoted path is rejected before process launch' { Check-Rejected 'C:\bad"config.json' }
        Case 'Control character is rejected before process launch' { Check-Rejected "C:\bad`nconfig.json" }
        Case 'Missing configuration is rejected before process launch' { Check-Rejected (Join-Path $testFixture 'missing.json') }
        Case 'Directory cannot be used as configuration' { Check-Rejected $testFixture }
    }
    Write-Output ('Passed: ' + $testPassed)
} finally {
    $cleanup = [IO.Path]::GetFullPath($testRoot)
    $prefix = $testTempParent.TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
    if (-not $cleanup.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($cleanup) -cnotmatch '^las-launch-[a-f0-9]{32}$') { throw 'Unsafe test cleanup target.' }
    if ([IO.Directory]::Exists($cleanup)) { [IO.Directory]::Delete($cleanup, $true) }
}

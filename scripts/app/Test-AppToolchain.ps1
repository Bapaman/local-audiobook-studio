[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sdkVersion = '10.0.401'
$windowsSdkVersion = '10.0.26100.0'
$templatePackage = 'Microsoft.WindowsAppSDK.WinUI.CSharp.Templates'
$templateVersion = '0.0.7-alpha'
$checks = [System.Collections.Generic.List[object]]::new()

function Add-Check {
    param([string]$Name, [bool]$Present, [string]$Expected, [string]$Remediation, [string]$Severity = 'error')
    $checks.Add([pscustomobject]@{
        name = $Name
        present = $Present
        expected = $Expected
        severity = $Severity
        remediation = $(if ($Present) { $null } else { $Remediation })
    })
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
$sdkPresent = $false
$templatePresent = $false
if (Test-Path -LiteralPath $dotnetPath -PathType Leaf) {
    $installedSdks = @(& $dotnetPath --list-sdks 2>&1)
    $sdkPresent = $LASTEXITCODE -eq 0 -and @($installedSdks | Where-Object { "$_" -match "^$([regex]::Escape($sdkVersion)) \[" }).Count -gt 0
    if ($sdkPresent) {
        $sdkInfo = @(& $dotnetPath --info 2>&1)
        $sdkPresent = $LASTEXITCODE -eq 0 -and ($sdkInfo -join "`n") -match '\bwin-x64\b'
    }
    if ($sdkPresent) {
        $templateList = @(& $dotnetPath new list winui 2>&1)
        $templatePresent = $LASTEXITCODE -eq 0 -and @($templateList | Where-Object { "$_" -match '\swinui(?:,|\s)' }).Count -gt 0
        $installedTemplates = @(& $dotnetPath new uninstall 2>&1)
        $templatePresent = $templatePresent -and ($installedTemplates -join "`n") -match "$([regex]::Escape($templatePackage))\s+[^\r\n]+$([regex]::Escape($templateVersion))(?:\s|$)"
    }
}
Add-Check '.NET SDK x64' $sdkPresent $sdkVersion "winget install --id Microsoft.DotNet.SDK.10 --version $sdkVersion --exact --source winget --scope machine"

$windowsSdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$windowsSdkPresent = (Test-Path -LiteralPath (Join-Path $windowsSdkRoot "Include\$windowsSdkVersion\um\Windows.h")) -and
    (Test-Path -LiteralPath (Join-Path $windowsSdkRoot "Lib\$windowsSdkVersion\um\x64\kernel32.lib"))
Add-Check 'Windows SDK' $windowsSdkPresent $windowsSdkVersion "Install Windows SDK $windowsSdkVersion with Visual Studio Installer or https://developer.microsoft.com/windows/downloads/windows-sdk/"

$developerMode = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
$developerModePresent = $null -ne $developerMode -and $developerMode.AllowDevelopmentWithoutDevLicense -eq 1
Add-Check 'Developer Mode' $developerModePresent 'Enabled for packaged app launch' 'Open Windows Settings > System > For developers and enable Developer Mode before packaged app launch.' 'warning'
Add-Check 'Windows App SDK winui template' $templatePresent "$templatePackage@$templateVersion" "dotnet new install $templatePackage@$templateVersion"

$ready = @($checks | Where-Object { -not $_.present -and $_.severity -eq 'error' }).Count -eq 0
[pscustomobject]@{ ready = $ready; checks = @($checks.ToArray()) } | ConvertTo-Json -Depth 4
if (-not $ready) { exit 1 }
exit 0

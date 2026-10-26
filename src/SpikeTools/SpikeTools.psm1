. $PSScriptRoot\Environment.ps1
. $PSScriptRoot\ArtifactLock.ps1
. $PSScriptRoot\ProcessRunner.ps1
. $PSScriptRoot\VoxCpmArguments.ps1
. $PSScriptRoot\Matrix.ps1
. $PSScriptRoot\VoxCpmRunner.ps1

Export-ModuleMember -Function @(
    'Get-SpikeEnvironment',
    'Test-SpikeEnvironment',
    'Read-ArtifactLock',
    'Assert-ArtifactHash',
    'Test-LockedArtifactSet',
    'Assert-CleanAuthoringTargets',
    'Assert-ExactArtifactInventory',
    'Invoke-CheckedProcess',
    'New-VoxCpmArgumentList',
    'Get-VoxCpmMatrix',
    'Get-VoxCpmFailureCode',
    'Invoke-VoxCpmProfileSequence',
    'New-VoxCpmRunPlan',
    'Get-VoxCpmCaseFingerprint',
    'Read-VoxCpmWavInfo',
    'Invoke-VoxCpmCase'
)

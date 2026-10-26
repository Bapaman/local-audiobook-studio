function Invoke-CheckedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList = @(),

        [string]$WorkingDirectory = (Get-Location).Path
    )

    if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
        throw "Process working directory does not exist: $WorkingDirectory"
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        if (-not $process.Start()) {
            throw "Failed to start process: $FilePath"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $stopwatch.Stop()

        [pscustomobject]@{
            FilePath        = $FilePath
            ArgumentList    = [string[]]$ArgumentList
            WorkingDirectory = $startInfo.WorkingDirectory
            ExitCode        = $process.ExitCode
            StdOut          = $stdout
            StdErr          = $stderr
            Duration        = $stopwatch.Elapsed
        }
    } finally {
        $stopwatch.Stop()
        $process.Dispose()
    }
}

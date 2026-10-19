param(
    [string]$ArtifactPath,
    [string]$ReportPath,
    [switch]$ScanSystemDrive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testsRoot
if (-not $ArtifactPath) {
    $artifacts = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'dist') -Filter '*.exe' -File)
    if ($artifacts.Count -ne 1) { throw 'Expected exactly one executable in dist.' }
    $ArtifactPath = $artifacts[0].FullName
}
$ArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).Path
$startupArguments = @()
if ($ScanSystemDrive) {
    $systemDriveRoot = [IO.Path]::GetPathRoot([Environment]::SystemDirectory)
    if ($systemDriveRoot -notmatch '^[A-Za-z]:\\$') { throw 'Expected a local Windows system-drive root.' }
    $startupArguments = @($systemDriveRoot)
}
if (-not $ReportPath) {
    $reportName = if ($ScanSystemDrive) { 'portability-scan-close-test.json' } else { 'portability-test.json' }
    $ReportPath = Join-Path (Join-Path $projectRoot 'docs') $reportName
}

$sandboxName = '.portability-test-' + [Guid]::NewGuid().ToString('N')
$sandboxRoot = Join-Path $testsRoot $sandboxName
$appDirectoryName = ([string][char]0x4fbf + [char]0x643a + [char]0x76ee + [char]0x5f55 + ' Unicode space')
$appDirectory = Join-Path $sandboxRoot $appDirectoryName
$tempDirectory = Join-Path $sandboxRoot 'Temp'
$roamingDirectory = Join-Path $sandboxRoot 'Roaming'
$localDirectory = Join-Path $sandboxRoot 'Local'
$process = $null
$watcher = $null
$eventSources = @()
$failure = $null
$report = [ordered]@{
    schemaVersion = 1
    testedAtUtc = [DateTime]::UtcNow.ToString('o')
    executableName = [IO.Path]::GetFileName($ArtifactPath)
    executableBytes = (Get-Item -LiteralPath $ArtifactPath).Length
    executableSha256 = (Get-FileHash -LiteralPath $ArtifactPath -Algorithm SHA256).Hash
    startupArguments = $startupArguments
    closeDuringStartupScan = [bool]$ScanSystemDrive
    scanWarmupMilliseconds = $(if ($ScanSystemDrive) { 150 } else { 0 })
    unicodePathWithSpaces = $true
    applicationDirectoryReadOnlyAcl = $false
    redirectedEnvironment = @('TEMP', 'TMP', 'APPDATA', 'LOCALAPPDATA')
    inputIdleReached = $false
    closeMainWindowAccepted = $false
    exitWithinThreeSeconds = $false
    exitCode = $null
    forcedTermination = $false
    fileSystemEvents = @()
    unexpectedFileSystemEvents = @()
    unexpectedFilesAfterClose = @()
    originalExecutableUnchanged = $false
    childProcessesObserved = @()
    tcpConnectionsObserved = @()
    tcpInspectionAvailable = $false
    sandboxRemoved = $false
    passed = $false
    limitations = @(
        'This is a lifecycle smoke test; it does not validate complete filesystem scan totals or CSV export.',
        'No ACLs are changed. The isolated Unicode directory and redirected folders are observed for writes.',
        'Changed notifications for pre-existing directories are retained as metadata observations; file writes and object creation/deletion/renaming fail the test.',
        'Process and TCP inspection are samples and cannot rule out very short-lived activity between samples.',
        'Environment redirection does not intercept every Windows known-folder API or every system location.',
        'No registry instrumentation is performed. Source review supports the absence of application registry writes.',
        'Windows-managed execution, security, icon, and recycle-bin records are outside this application test.'
    )
}

try {
    New-Item -ItemType Directory -Path $sandboxRoot | Out-Null
    foreach ($directory in @($appDirectory, $tempDirectory, $roamingDirectory, $localDirectory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    $sandboxExecutable = Join-Path $appDirectory ([IO.Path]::GetFileName($ArtifactPath))
    Copy-Item -LiteralPath $ArtifactPath -Destination $sandboxExecutable
    $beforeHash = (Get-FileHash -LiteralPath $sandboxExecutable -Algorithm SHA256).Hash

    $watcher = New-Object IO.FileSystemWatcher $sandboxRoot
    $watcher.IncludeSubdirectories = $true
    $watcher.InternalBufferSize = 32768
    $watcher.NotifyFilter = [IO.NotifyFilters]::FileName -bor [IO.NotifyFilters]::DirectoryName -bor [IO.NotifyFilters]::LastWrite -bor [IO.NotifyFilters]::Size
    foreach ($eventName in @('Created', 'Changed', 'Deleted', 'Renamed', 'Error')) {
        $source = $sandboxName + '-' + $eventName
        Register-ObjectEvent -InputObject $watcher -EventName $eventName -SourceIdentifier $source | Out-Null
        $eventSources += $source
    }
    $watcher.EnableRaisingEvents = $true

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $sandboxExecutable
    # A validated drive root has no spaces or command-line quote characters.
    if ($ScanSystemDrive) { $startInfo.Arguments = $systemDriveRoot }
    $startInfo.WorkingDirectory = $appDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables['TEMP'] = $tempDirectory
    $startInfo.EnvironmentVariables['TMP'] = $tempDirectory
    $startInfo.EnvironmentVariables['APPDATA'] = $roamingDirectory
    $startInfo.EnvironmentVariables['LOCALAPPDATA'] = $localDirectory
    $process = [Diagnostics.Process]::Start($startInfo)
    $report.inputIdleReached = $process.WaitForInputIdle(5000)
    if ($ScanSystemDrive) { Start-Sleep -Milliseconds 150 }

    if (-not $process.HasExited) {
        $processSnapshot = @(Get-CimInstance -ClassName Win32_Process)
        $descendantIds = New-Object 'System.Collections.Generic.HashSet[uint32]'
        $descendantIds.Add([uint32]$process.Id) | Out-Null
        do {
            $added = $false
            foreach ($candidate in $processSnapshot) {
                if ($descendantIds.Contains([uint32]$candidate.ParentProcessId) -and -not $descendantIds.Contains([uint32]$candidate.ProcessId)) {
                    $descendantIds.Add([uint32]$candidate.ProcessId) | Out-Null
                    $report.childProcessesObserved += [ordered]@{ name = $candidate.Name; processId = $candidate.ProcessId; parentProcessId = $candidate.ParentProcessId }
                    $added = $true
                }
            }
        } while ($added)
        if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
            $report.tcpInspectionAvailable = $true
            $report.tcpConnectionsObserved = @(Get-NetTCPConnection -OwningProcess $process.Id -ErrorAction SilentlyContinue | ForEach-Object {
                [ordered]@{ localAddress = $_.LocalAddress; localPort = $_.LocalPort; remoteAddress = $_.RemoteAddress; remotePort = $_.RemotePort; state = $_.State.ToString() }
            })
        }
        $report.closeMainWindowAccepted = $process.CloseMainWindow()
        $report.exitWithinThreeSeconds = $process.WaitForExit(3000)
    }
    if ($process.HasExited) { $report.exitCode = $process.ExitCode }
    else {
        $report.forcedTermination = $true
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }

    Start-Sleep -Milliseconds 150
    $watcher.EnableRaisingEvents = $false
    foreach ($source in $eventSources) {
        foreach ($event in @(Get-Event -SourceIdentifier $source -ErrorAction SilentlyContinue)) {
            $arguments = $event.SourceEventArgs
            $eventPath = $null
            if ($arguments -is [IO.FileSystemEventArgs]) { $eventPath = $arguments.FullPath.Substring($sandboxRoot.Length).TrimStart('\') }
            $eventName = $source.Substring($sandboxName.Length + 1)
            $classification = 'unexpectedWriteOrObjectChange'
            if ($eventName -eq 'Changed' -and $arguments -is [IO.FileSystemEventArgs] -and
                @($sandboxRoot, $appDirectory, $tempDirectory, $roamingDirectory, $localDirectory) -contains $arguments.FullPath -and
                (Test-Path -LiteralPath $arguments.FullPath -PathType Container)) {
                $classification = 'existingDirectoryMetadata'
            }
            $observation = [ordered]@{ event = $eventName; relativePath = $eventPath; classification = $classification }
            $report.fileSystemEvents += $observation
            if ($classification -ne 'existingDirectoryMetadata') { $report.unexpectedFileSystemEvents += $observation }
        }
    }
    $report.unexpectedFilesAfterClose = @(Get-ChildItem -LiteralPath $sandboxRoot -Recurse -File -Force | Where-Object { $_.FullName -ne $sandboxExecutable } | ForEach-Object { $_.FullName.Substring($sandboxRoot.Length).TrimStart('\') })
    $report.originalExecutableUnchanged = (Test-Path -LiteralPath $sandboxExecutable) -and ((Get-FileHash -LiteralPath $sandboxExecutable -Algorithm SHA256).Hash -eq $beforeHash)
    $report.passed = $report.inputIdleReached -and $report.closeMainWindowAccepted -and $report.exitWithinThreeSeconds -and ($report.exitCode -eq 0) -and
        -not $report.forcedTermination -and $report.unexpectedFileSystemEvents.Count -eq 0 -and $report.unexpectedFilesAfterClose.Count -eq 0 -and
        $report.originalExecutableUnchanged -and $report.childProcessesObserved.Count -eq 0 -and $report.tcpInspectionAvailable -and $report.tcpConnectionsObserved.Count -eq 0
}
catch { $failure = $_; $report.passed = $false; $report['failure'] = $_.Exception.Message }
finally {
    if ($process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(3000) | Out-Null; $report.forcedTermination = $true }
        $process.Dispose()
    }
    if ($watcher) { $watcher.EnableRaisingEvents = $false; $watcher.Dispose() }
    foreach ($source in $eventSources) {
        Unregister-Event -SourceIdentifier $source -ErrorAction SilentlyContinue
        Get-Event -SourceIdentifier $source -ErrorAction SilentlyContinue | Remove-Event -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $sandboxRoot) {
        $resolvedSandbox = (Resolve-Path -LiteralPath $sandboxRoot).Path
        $sandboxItem = Get-Item -LiteralPath $resolvedSandbox -Force
        $expectedParent = [IO.Path]::GetFullPath($testsRoot).TrimEnd('\')
        if ([IO.Path]::GetDirectoryName($resolvedSandbox).TrimEnd('\') -ne $expectedParent -or
            $sandboxItem.Name -ne $sandboxName -or ($sandboxItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to remove a sandbox outside this test directory or with an unexpected identity.'
        }
        Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force
    }
    $report.sandboxRemoved = -not (Test-Path -LiteralPath $sandboxRoot)
    if (-not $report.sandboxRemoved) { $report.passed = $false }
    $reportDirectory = Split-Path -Parent $ReportPath
    if (-not (Test-Path -LiteralPath $reportDirectory)) { New-Item -ItemType Directory -Path $reportDirectory | Out-Null }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath), ($report | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))
}

Write-Host ('Portability test passed: ' + $report.passed)
Write-Host ('Report: ' + [IO.Path]::GetFullPath($ReportPath))
if ($failure) { throw $failure }
if (-not $report.passed) { throw 'The portability test did not pass. Review the JSON report.' }

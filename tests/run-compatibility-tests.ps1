$ErrorActionPreference = 'Stop'
$testDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$workspaceDirectory = [IO.Path]::GetFullPath((Join-Path $testDirectory '..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4 compiler was not found.' }
$runDirectory = Join-Path $testDirectory ('.compatibility-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
try {
    $originalSource = Join-Path $workspaceDirectory 'DiskSpaceVisualizer.ps1'
    $baselineArchivePath = Join-Path $workspaceDirectory 'baseline\original.zip'
    $baselineArchiveHash = $null
    if (Test-Path -LiteralPath $baselineArchivePath) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $baselineArchiveHash = (Get-FileHash -LiteralPath $baselineArchivePath -Algorithm SHA256).Hash
        $archive = [IO.Compression.ZipFile]::OpenRead($baselineArchivePath)
        try {
            $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'DiskSpaceVisualizer.ps1' })
            if ($entries.Count -ne 1) { throw 'Baseline archive must contain exactly one root DiskSpaceVisualizer.ps1 entry.' }
            $originalSource = Join-Path $runDirectory 'Original-DiskSpaceVisualizer.ps1'
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $originalSource, $false)
        }
        finally { $archive.Dispose() }
    }
    elseif (-not (Test-Path -LiteralPath $originalSource)) {
        throw 'Original source is unavailable: expected baseline\original.zip or DiskSpaceVisualizer.ps1.'
    }
    $output = Join-Path $runDirectory 'CompatibilityTests.exe'
    & $compiler /nologo /target:exe /optimize+ /warn:4 /codepage:65001 /r:System.Core.dll /r:System.Xml.dll "/out:$output" (Join-Path $workspaceDirectory 'src\ScanEngine.cs') (Join-Path $testDirectory 'CompatibilityTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility test compilation failed.' }
    & $output $originalSource $runDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Original/new compatibility tests failed.' }
    if ($baselineArchiveHash -and $baselineArchiveHash -ne (Get-FileHash -LiteralPath $baselineArchivePath -Algorithm SHA256).Hash) {
        throw 'Baseline archive changed during compatibility testing.'
    }
}
finally {
    $resolvedRunDirectory = [IO.Path]::GetFullPath($runDirectory)
    if (-not $resolvedRunDirectory.StartsWith($testDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedRunDirectory) -notmatch '^\.compatibility-tests-[a-f0-9]{32}$') {
        throw 'Refusing to remove a test directory outside the expected boundary.'
    }
    if (Test-Path -LiteralPath $resolvedRunDirectory) { Remove-Item -LiteralPath $resolvedRunDirectory -Recurse -Force }
}

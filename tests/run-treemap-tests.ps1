$ErrorActionPreference = 'Stop'
$testDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$workspaceDirectory = [IO.Path]::GetFullPath((Join-Path $testDirectory '..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4 compiler was not found.' }
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runDirectory = Join-Path $temporaryParent ('DiskSpace-TreemapTests-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $runDirectory 'TreemapTests.exe'
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
try {
    & $compiler /nologo /target:exe /optimize+ /warn:4 /langversion:5 /codepage:65001 `
        /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll "/out:$output" `
        (Join-Path $workspaceDirectory 'src\ScanEngine.cs') `
        (Join-Path $workspaceDirectory 'src\TreemapView.cs') `
        (Join-Path $testDirectory 'TreemapTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Treemap test compilation failed.' }
    & $output
    if ($LASTEXITCODE -ne 0) { throw 'Treemap tests failed.' }
}
finally {
    $resolvedRunDirectory = [IO.Path]::GetFullPath($runDirectory)
    if ([IO.Path]::GetDirectoryName($resolvedRunDirectory) -ne $temporaryParent -or
        [IO.Path]::GetFileName($resolvedRunDirectory) -notmatch '^DiskSpace-TreemapTests-[a-f0-9]{32}$' -or
        [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($output)) -ne $resolvedRunDirectory) {
        throw 'Refusing cleanup outside this run''s private temporary directory.'
    }
    # Only this invocation's explicitly named compiler output may be removed.
    if ([IO.File]::Exists($output)) { [IO.File]::Delete($output) }
    if ([IO.Directory]::Exists($resolvedRunDirectory)) {
        if ([IO.Directory]::GetFileSystemEntries($resolvedRunDirectory).Length -ne 0) {
            throw 'Unexpected files in the test directory were preserved for inspection.'
        }
        [IO.Directory]::Delete($resolvedRunDirectory, $false)
    }
}

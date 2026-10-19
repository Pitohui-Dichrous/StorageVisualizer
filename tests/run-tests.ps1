$ErrorActionPreference = 'Stop'
$testDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$workspaceDirectory = [IO.Path]::GetFullPath((Join-Path $testDirectory '..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4 compiler was not found.' }
$runDirectory = Join-Path $testDirectory ('.scan-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
try {
    $output = Join-Path $runDirectory 'ScanEngineTests.exe'
    & $compiler /nologo /target:exe /optimize+ /warn:4 /codepage:65001 "/out:$output" (Join-Path $workspaceDirectory 'src\ScanEngine.cs') (Join-Path $testDirectory 'ScanEngineTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Scan-engine test compilation failed.' }
    & $output $runDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Scan-engine tests failed.' }
}
finally {
    $resolvedRunDirectory = [IO.Path]::GetFullPath($runDirectory)
    $expectedParent = $testDirectory.TrimEnd('\') + '\'
    if (-not $resolvedRunDirectory.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedRunDirectory) -notmatch '^\.scan-tests-[a-f0-9]{32}$') {
        throw 'Refusing to remove a test directory outside the expected boundary.'
    }
    if (Test-Path -LiteralPath $resolvedRunDirectory) { Remove-Item -LiteralPath $resolvedRunDirectory -Recurse -Force }
}

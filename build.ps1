$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compilerPaths = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw '.NET Framework C# compiler was not found. This build requires Windows with .NET Framework 4.x.' }

$sourceRoot = Join-Path $projectRoot 'src'
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw 'No C# source files were found in src.' }

$outputDirectory = Join-Path $projectRoot 'dist'
if (-not (Test-Path -LiteralPath $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory | Out-Null }
$outputFile = Join-Path $outputDirectory ([char]0x78c1 + [string][char]0x76d8 + [char]0x7a7a + [char]0x95f4 + [char]0x5206 + [char]0x6790 + [char]0x5668 + '.exe')
$iconFile = Join-Path $projectRoot 'assets\app.ico'
$manifestFile = Join-Path $projectRoot 'assets\app.manifest'
$arguments = @(
    '/nologo', '/target:winexe', '/optimize+', '/debug-', '/platform:anycpu',
    '/codepage:65001', '/utf8output', '/warn:4',
    ('/out:' + $outputFile),
    ('/win32icon:' + $iconFile),
    ('/win32manifest:' + $manifestFile),
    ('/resource:' + $iconFile + ',DiskSpace.app.ico'),
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:Accessibility.dll'
) + $sources

& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw ('Compilation failed with exit code ' + $LASTEXITCODE + '.') }
if (-not (Test-Path -LiteralPath $outputFile)) { throw 'The compiler did not produce the executable.' }
$output = Get-Item -LiteralPath $outputFile
Write-Host ('Built: ' + $output.FullName)
Write-Host ('Size: ' + $output.Length + ' bytes')
Write-Host 'Only this executable is required to run the application.'

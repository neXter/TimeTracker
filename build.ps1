<#
  Builds TimeTracker into dist\TimeTracker.exe: one self-contained file that runs on Windows 10/11 x64
  (and on ARM64 PCs through emulation) with nothing to install first.

  Usage:   powershell -ExecutionPolicy Bypass -File .\build.ps1            (tests + publish)
           powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipTests
  Needs the .NET 10 SDK (https://dotnet.microsoft.com/download).
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# Pick a dotnet that really has a .NET 10 SDK (a machine can have the runtime only).
$candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue)?.Source, "$env:USERPROFILE\.dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe") |
  Where-Object { $_ -and (Test-Path $_) }
$dotnet = $candidates | Where-Object { (& $_ --list-sdks 2>$null) -match '^10\.' } | Select-Object -First 1
if (-not $dotnet) { throw '.NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download' }
'Using ' + $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not $SkipTests) {
  & $dotnet test "$root\tests\TimeTracker.Tests" -nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& $dotnet publish "$root\src\TimeTracker\TimeTracker.csproj" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false -nologo -v q -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Get-ChildItem $dist -Filter *.xml | Remove-Item -Force   # package doc files, not needed at runtime
$exe = Join-Path $dist 'TimeTracker.exe'
'{0}  ({1:N1} MB)' -f $exe, ((Get-Item $exe).Length / 1MB)
Get-ChildItem $dist | Where-Object Name -ne 'TimeTracker.exe' | ForEach-Object { 'extra file: ' + $_.Name }

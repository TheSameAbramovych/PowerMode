<#
.SYNOPSIS
    Restore, build (warnings as errors), test with coverage, publish publish\PowerMode.exe.
.DESCRIPTION
    Uses the per-user .NET SDK in %LOCALAPPDATA%\Microsoft\dotnet when there is no dotnet on PATH.
    The exe is self-contained, single file, win-x64, ReadyToRun.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) {
    $env:DOTNET_ROOT = $userDotnet
    $env:PATH = "$userDotnet;$env:PATH"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$solution = Join-Path $root 'PowerMode.slnx'
$app = Join-Path $root 'src\PowerMode.App\PowerMode.App.csproj'
$tests = Join-Path $root 'tests\PowerMode.Core.Tests\PowerMode.Core.Tests.csproj'
$publish = Join-Path $root 'publish'
$results = Join-Path $root 'TestResults'

function Invoke-Step([string]$name, [scriptblock]$command) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit code $LASTEXITCODE)" }
}

Invoke-Step 'Restore' { dotnet restore $solution }
Invoke-Step 'Build' { dotnet build $solution -c $Configuration --no-restore -warnaserror }

if (Test-Path $results) { Remove-Item $results -Recurse -Force }
Invoke-Step 'Test' { dotnet test $tests -c $Configuration --no-build --collect:'XPlat Code Coverage' --results-directory $results }

$coverage = Get-ChildItem $results -Recurse -Filter 'coverage.cobertura.xml' | Select-Object -First 1
if ($coverage) {
    [xml]$report = Get-Content $coverage.FullName
    $lines = [math]::Round([double]$report.coverage.'line-rate' * 100, 1)
    $branches = [math]::Round([double]$report.coverage.'branch-rate' * 100, 1)
    Write-Host "Core coverage: $lines % lines, $branches % branches"
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Invoke-Step 'Publish' {
    dotnet publish $app -c $Configuration -r win-x64 --self-contained true -o $publish `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -warnaserror
}

$exe = Join-Path $publish 'PowerMode.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
$size = (Get-Item $exe).Length
Write-Host ("Published {0} ({1:N1} MB, {2:N0} bytes)" -f $exe, ($size / 1MB), $size) -ForegroundColor Green

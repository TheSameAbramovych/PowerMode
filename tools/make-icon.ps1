<#
.SYNOPSIS
    Regenerate src\PowerMode.App\Assets\PowerMode.ico (16-256 px) with the app's own IconRenderer.
.DESCRIPTION
    Builds the app (Release), then runs "PowerMode.exe --make-icon <file>": the plug glyph of the tray icon in the
    accent colour on a rounded dark tile. The .ico is checked in, so a normal build does not need this script; run it
    after changing IconRenderer.RenderAppIcon and commit the new file.
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\PowerMode.App\Assets\PowerMode.ico')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) {
    $env:DOTNET_ROOT = $userDotnet
    $env:PATH = "$userDotnet;$env:PATH"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$project = Join-Path $root 'src\PowerMode.App\PowerMode.App.csproj'
dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }

$exe = Join-Path $root 'src\PowerMode.App\bin\Release\net10.0-windows\PowerMode.exe'
$target = [System.IO.Path]::GetFullPath($Output)
& $exe --make-icon $target | Out-Host
if ($LASTEXITCODE -ne 0) { throw "--make-icon failed (exit code $LASTEXITCODE)" }
Write-Host ("Wrote {0} ({1:N0} bytes)" -f $target, (Get-Item $target).Length) -ForegroundColor Green

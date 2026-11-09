<#
.SYNOPSIS
    Build PowerMode and install it for this user: %LOCALAPPDATA%\Programs\PowerMode\PowerMode.exe, a desktop shortcut,
    and the autostart task pointed at the installed exe.
.DESCRIPTION
    1. Runs build.ps1 (restore, build, tests, publish) unless -NoBuild is given.
    2. Quits a PowerMode that runs from the install folder (it holds the exe open) with --quit, and stops it only
       if it does not end, saying which process it is.
       Stopping the tray does not change the mode: the PC stays in the current mode until PowerMode runs again.
    3. Copies publish\PowerMode.exe to %LOCALAPPDATA%\Programs\PowerMode\PowerMode.exe.
    4. Creates or updates PowerMode.lnk on the desktop (the real Desktop folder, also when it is redirected to
       OneDrive), pointing at the installed exe with its icon and working folder.
    5. If the scheduled task "PowerMode" (Start with Windows) exists, points it at the installed exe; when that needs
       administrator rights, the installed exe re-registers the task itself (one UAC prompt).
    The app is started afterwards only with -Start. Config and state in %LOCALAPPDATA%\PowerMode are not touched.
.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -NoBuild -Start
#>
[CmdletBinding()]
param(
    # Install the existing publish\PowerMode.exe without building.
    [switch]$NoBuild,
    # Start the installed PowerMode when done.
    [switch]$Start
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$TaskName = 'PowerMode'
$InstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\PowerMode'
$InstalledExe = Join-Path $InstallDirectory 'PowerMode.exe'

function Write-Step([string]$text) {
    Write-Host "==> $text" -ForegroundColor Cyan
}

<# Create or update a shortcut to an exe, with the exe's own icon and its folder as working directory. #>
function Set-PowerModeShortcut {
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$TargetPath
    )

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.WorkingDirectory = Split-Path $TargetPath -Parent
        $shortcut.IconLocation = "$TargetPath,0"
        $shortcut.Description = 'PowerMode: switch this PC between Grid, Station and Reserve'
        $shortcut.Save()
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

<# PowerMode processes whose exe is the given file. #>
function Get-PowerModeProcess([string]$exePath) {
    @(Get-Process -Name PowerMode -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and [string]::Equals([System.IO.Path]::GetFullPath($_.Path), [System.IO.Path]::GetFullPath($exePath), [StringComparison]::OrdinalIgnoreCase)
    })
}

function Stop-InstalledPowerMode {
    $running = @(Get-PowerModeProcess $InstalledExe)
    if ($running.Count -gt 0) {
        # Graceful first: the tray finishes a running switch and quits, keeping the current mode.
        Write-Host "Quitting PowerMode (PID $($running.Id -join ', ')) running from $InstalledExe with --quit; the PC keeps its current mode."
        & $InstalledExe --quit | Out-Host
    }

    foreach ($process in Get-PowerModeProcess $InstalledExe) {
        Write-Host "PowerMode (PID $($process.Id)) did not quit; stopping it."
        try {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit(10000) | Out-Null
        }
        catch {
            throw "Cannot stop PowerMode (PID $($process.Id)), it probably runs as administrator. Quit it from the tray menu (Quit, keep mode) and run install.ps1 again."
        }
    }

    $others = @(Get-Process -Name PowerMode -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ne $InstalledExe })
    foreach ($other in $others) {
        Write-Host "Note: another PowerMode runs from $($other.Path) (PID $($other.Id)); it is left running." -ForegroundColor Yellow
    }
}

function Update-AutostartTask {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) {
        Write-Host "No scheduled task '$TaskName' (Start with Windows is off); nothing to update."
        return
    }

    $current = @($task.Actions | ForEach-Object { $_.Execute })
    if ($current.Count -eq 1 -and [string]::Equals($current[0], $InstalledExe, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Task '$TaskName' already starts $InstalledExe."
        return
    }

    Write-Host "Task '$TaskName' starts $($current -join ', '); pointing it at $InstalledExe."
    try {
        $action = New-ScheduledTaskAction -Execute $InstalledExe -WorkingDirectory $InstallDirectory
        Set-ScheduledTask -TaskName $TaskName -Action $action | Out-Null
    }
    catch {
        # The task runs with highest privileges, so changing it may need an administrator: let the installed exe
        # register it again (same settings, its own path), which asks for UAC once.
        Write-Host 'That needs administrator rights; re-registering the task through the installed PowerMode (UAC prompt).'
        & $InstalledExe --install-autostart | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Updating the autostart task failed (exit code $LASTEXITCODE)."
        }
    }
}

function Install-PowerMode {
    $root = $PSScriptRoot
    $published = Join-Path $root 'publish\PowerMode.exe'
    if (-not $NoBuild) {
        Write-Step 'Build (build.ps1)'
        & (Join-Path $root 'build.ps1')
    }

    if (-not (Test-Path $published)) {
        throw "$published not found; run without -NoBuild."
    }

    Write-Step "Install to $InstallDirectory"
    Stop-InstalledPowerMode
    New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    Copy-Item -LiteralPath $published -Destination $InstalledExe -Force
    Write-Host ("Copied {0} ({1:N1} MB)" -f $InstalledExe, ((Get-Item $InstalledExe).Length / 1MB))

    Write-Step 'Desktop shortcut'
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shortcut = Join-Path $desktop 'PowerMode.lnk'
    Set-PowerModeShortcut -ShortcutPath $shortcut -TargetPath $InstalledExe
    Write-Host "Shortcut: $shortcut -> $InstalledExe"

    Write-Step 'Autostart task'
    Update-AutostartTask

    if ($Start) {
        Write-Step 'Start'
        Start-Process -FilePath $InstalledExe -WorkingDirectory $InstallDirectory
        Write-Host "Started $InstalledExe"
    }
    else {
        Write-Host "Done. Start PowerMode from the desktop shortcut (or run .\install.ps1 -Start next time)." -ForegroundColor Green
    }
}

# Dot-sourcing (". .\install.ps1") only defines the functions, for testing; running the script installs.
if ($MyInvocation.InvocationName -ne '.') {
    Install-PowerMode
}

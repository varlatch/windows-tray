<#
.SYNOPSIS
Removes Varlatch for Windows for this user.

.DESCRIPTION
Settings > Apps > Installed apps runs this from the app's folder. It quits
the app, stops it opening at login, removes its notifications, its Start
menu shortcut, and its files. Its settings and state in
%LOCALAPPDATA%\Varlatch for Windows stay unless -Purge is given. The
varlatch CLI and your sign-ins stay: they belong to the CLI.
#>
[CmdletBinding()]
param(
    # The app's folder; this script's own by default.
    [string]$InstallDir,
    # Also remove the app's settings and state.
    [switch]$Purge,
    # No questions.
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
if (-not $InstallDir) { $InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$exe = Join-Path $InstallDir 'Varlatch.exe'
$state = Join-Path $env:LOCALAPPDATA 'Varlatch for Windows'

if (-not $Quiet) {
    $answer = Read-Host 'Remove Varlatch for Windows? The varlatch CLI and your sign-ins stay. [y/N]'
    if ($answer -notmatch '^[yY]') { exit 0 }
}

if (Test-Path $exe) {
    # Quits a running copy, or does the cleanup itself: no more opening at
    # login, and Windows forgets the app's notifications.
    & $exe --unregister
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name Varlatch -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -gt 0) {
        Start-Sleep -Milliseconds 200
    }
    Get-Process -Name Varlatch -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Varlatch.lnk') -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Varlatch for Windows' -Recurse -ErrorAction SilentlyContinue
Set-Location ([IO.Path]::GetTempPath())
Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
if ($Purge) { Remove-Item -Recurse -Force $state -ErrorAction SilentlyContinue }

Write-Host 'Varlatch for Windows is removed.'
if (-not $Purge -and (Test-Path $state)) { Write-Host "Its settings stay in $state; delete that folder for a clean slate." }

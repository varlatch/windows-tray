<#
.SYNOPSIS
Installs Varlatch for Windows for this user, or updates it.

.DESCRIPTION
Downloads the release for this PC (x64 or Arm64) from GitHub, checks it
against the release's SHA256SUMS, and installs it in
%LOCALAPPDATA%\Programs\Varlatch for Windows, with a Start menu shortcut and
an entry in Settings > Apps > Installed apps. No administrator rights are
needed. A running Varlatch is asked to quit first and started again after.

The app finds the varlatch CLI by itself and, without it, offers to install
it. Your sign-ins live with the CLI and are not touched.

Runs in Windows PowerShell 5.1 and PowerShell 7:

  irm https://github.com/varlatch/windows-tray/releases/latest/download/install.ps1 | iex

.PARAMETER Version
A release, such as 0.1.0; the latest by default.

.PARAMETER Zip
A release archive on disk (Varlatch-for-Windows-<version>-win-<arch>.zip) to
install instead of downloading one. When a SHA256SUMS lies next to it, the
archive is checked against it.
#>
[CmdletBinding()]
param(
    [string]$Version = 'latest',
    [string]$Zip,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Varlatch for Windows'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$Repo = 'varlatch/windows-tray'
$UserAgent = 'Varlatch-for-Windows-installer'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Varlatch for Windows'
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Varlatch.lnk'

function Stop-Install([string]$Message) {
    Write-Host "Varlatch for Windows: $Message" -ForegroundColor Red
    exit 1
}

function Test-Checksum([string]$File, [string]$Sums) {
    $name = [IO.Path]::GetFileName($File)
    $line = Get-Content $Sums | Where-Object { $_ -match "^([0-9a-f]{64}) [ *]$([regex]::Escape($name))$" } | Select-Object -First 1
    if (-not $line) { Stop-Install "SHA256SUMS does not list $name. Nothing was installed." }
    $actual = (Get-FileHash -Algorithm SHA256 $File).Hash.ToLowerInvariant()
    if ($actual -ne ($line -split ' ')[0]) { Stop-Install "$name does not match SHA256SUMS. Nothing was installed." }
}

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('varlatch-app-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if ($Zip) {
        $Zip = (Resolve-Path $Zip).Path
        $sums = Join-Path (Split-Path $Zip) 'SHA256SUMS'
        if (Test-Path $sums) { Test-Checksum $Zip $sums; $checked = 'ok (SHA256SUMS)' } else { $checked = 'not checked: no SHA256SUMS next to it' }
        $archive = $Zip
    } else {
        if ($Version -eq 'latest') {
            try {
                $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -TimeoutSec 20 `
                    -Headers @{ Accept = 'application/vnd.github+json' } -UserAgent $UserAgent -UseBasicParsing
            } catch {
                Stop-Install "cannot find the latest release of $Repo (offline?)."
            }
            $Version = $release.tag_name -replace '^v', ''
        }
        $Version = $Version -replace '^v', ''
        $name = "Varlatch-for-Windows-$Version-win-$arch.zip"
        $base = "https://github.com/$Repo/releases/download/v$Version"
        Write-Host "Downloading $name..."
        $archive = Join-Path $work $name
        $sums = Join-Path $work 'SHA256SUMS'
        try {
            Invoke-WebRequest -Uri "$base/$name" -OutFile $archive -UseBasicParsing -UserAgent $UserAgent
            Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile $sums -UseBasicParsing -UserAgent $UserAgent
        } catch {
            Stop-Install "download failed: $($_.Exception.Message)"
        }
        # The release workflow signs SHA256SUMS; with cosign installed, check that too.
        $bundle = Join-Path $work 'SHA256SUMS.sigstore.json'
        $signature = 'none: this release carries no signature'
        try {
            Invoke-WebRequest -Uri "$base/SHA256SUMS.sigstore.json" -OutFile $bundle -UseBasicParsing -UserAgent $UserAgent
            $cosign = Get-Command cosign.exe -ErrorAction SilentlyContinue
            if ($cosign) {
                & $cosign.Source verify-blob $sums --bundle $bundle `
                    --certificate-identity "https://github.com/$Repo/.github/workflows/release.yml@refs/tags/v$Version" `
                    --certificate-oidc-issuer https://token.actions.githubusercontent.com 2>$null | Out-Null
                if ($LASTEXITCODE -ne 0) { Stop-Install 'the signature on SHA256SUMS does NOT verify. Nothing was installed.' }
                $signature = "verified: signed by the $Repo release workflow for v$Version"
            } else {
                $signature = 'NOT checked: the release is signed, but cosign is not installed'
            }
        } catch {
            Remove-Item $bundle -ErrorAction SilentlyContinue
        }
        Test-Checksum $archive $sums
        $checked = 'ok (SHA256SUMS)'
    }

    $unpacked = Join-Path $work 'app'
    Expand-Archive -Path $archive -DestinationPath $unpacked
    $exeName = 'Varlatch.exe'
    if (-not (Test-Path (Join-Path $unpacked $exeName))) { Stop-Install "the archive has no $exeName. Nothing was installed." }
    $newVersion = (Get-Item (Join-Path $unpacked $exeName)).VersionInfo.ProductVersion

    # A running copy holds its files: ask it to quit (a sign-in it started is
    # cancelled), and end it if it does not.
    $exe = Join-Path $InstallDir $exeName
    $running = @(Get-Process -Name Varlatch -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    if ($running.Count -gt 0) {
        Write-Host 'Quitting the running Varlatch...'
        & $exe --quit
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and @(Get-Process -Id $running.Id -ErrorAction SilentlyContinue).Count -gt 0) { Start-Sleep -Milliseconds 200 }
        Get-Process -Id $running.Id -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 300
    }

    # The new copy goes in beside the old one, then takes its place.
    $parent = Split-Path $InstallDir
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $old = "$InstallDir.old"
    Remove-Item -Recurse -Force $old -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) { Rename-Item $InstallDir (Split-Path -Leaf $old) }
    Move-Item $unpacked $InstallDir
    Remove-Item -Recurse -Force $old -ErrorAction SilentlyContinue
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

# Start menu, and Settings > Apps > Installed apps.
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($Shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $InstallDir
$link.IconLocation = "$exe,0"
$link.Description = 'Your Varlatch sessions in the notification area'
$link.Save()

$size = [int]((Get-ChildItem -Recurse -File $InstallDir | Measure-Object -Sum Length).Sum / 1KB)
New-Item -Path $UninstallKey -Force | Out-Null
$uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
@{
    DisplayName = 'Varlatch for Windows'
    DisplayVersion = $newVersion
    Publisher = 'Robotsson'
    DisplayIcon = "$exe,0"
    InstallLocation = $InstallDir
    UninstallString = $uninstall
    QuietUninstallString = "$uninstall -Quiet"
    URLInfoAbout = "https://github.com/$Repo"
}.GetEnumerator() | ForEach-Object { Set-ItemProperty -Path $UninstallKey -Name $_.Key -Value $_.Value }
Set-ItemProperty -Path $UninstallKey -Name EstimatedSize -Value $size -Type DWord
Set-ItemProperty -Path $UninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name NoRepair -Value 1 -Type DWord

Write-Host "Installed Varlatch for Windows $newVersion in $InstallDir"
Write-Host "  checksum:  $checked"
if ($signature) { Write-Host "  signature: $signature" }
if (-not $NoLaunch) {
    Start-Process -FilePath $exe
    Write-Host 'Varlatch is in the notification area. Windows may keep its icon in the ^ menu by the clock; drag it onto the taskbar to keep it in view.'
}

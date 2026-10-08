<#
.SYNOPSIS
Installs the Varlatch CLI for this user, checked, where Varlatch for Windows
looks for it.

.DESCRIPTION
Varlatch for Windows runs this in a terminal when the CLI is not installed
(Install in Terminal). It asks for your server's address, so it can install
the CLI version that server runs (press Enter for the latest release), makes
sure Node.js 22 or newer is there (offering to install it with winget), and
downloads the release CLI. It checks the download the way
`varlatch self-update` does: the signature on SHA256SUMS when the release
carries one and cosign is installed, the file against SHA256SUMS, and the
version the file reports. It shows what it checked and installs only once
you confirm:

  %LOCALAPPDATA%\Programs\Varlatch\varlatch.cjs   the CLI
  %LOCALAPPDATA%\Programs\Varlatch\varlatch.cmd   runs it with Node.js

and puts that directory on your user PATH, so `varlatch` works in new
terminals. `varlatch self-update` replaces varlatch.cjs in place later.

Runs in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File install-cli.ps1
#>
[CmdletBinding()]
param(
    # The server whose version to install; asked for when not given.
    [string]$Server,
    # A release to install, such as 0.15.1, instead of asking.
    [string]$Version,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Varlatch'),
    # Varlatch.exe, told about the new CLI and asked to sign in.
    [string]$App,
    # Install without asking.
    [switch]$Yes,
    # Leave the user PATH alone.
    [switch]$NoPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$Repo = 'varlatch/varlatch'
$UserAgent = 'Varlatch-for-Windows'

function Stop-Install([string]$Message) {
    Write-Host ''
    Write-Host "varlatch: $Message" -ForegroundColor Red
    exit 1
}

function Confirm-Step([string]$Question, [bool]$Default) {
    if ($Yes) { return $true }
    $suffix = if ($Default) { '[Y/n]' } else { '[y/N]' }
    $answer = Read-Host "$Question $suffix"
    if (-not $answer) { return $Default }
    return $answer -match '^[yY]'
}

# A terminal started before Node.js or the CLI was installed has an old PATH.
function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($machine, $user, $env:Path) | Where-Object { $_ }) -join ';'
}

# "https://varlatch.example.com" from what a person typed, as the app does it.
function ConvertTo-ServerAddress([string]$Text) {
    $s = ($Text -replace '\s', '').TrimEnd('/')
    if (-not $s) { return $null }
    if ($s -notmatch '^[A-Za-z][A-Za-z0-9+.-]*://') { $s = "https://$s" }
    if ($s -notmatch '^https?://[^/?#]+(/[^?#]*)?$') { return $null }
    return $s
}

function Get-HostName([string]$Url) { return ($Url -replace '^[^:]+://', '') -replace '/.*$', '' }

# The version a server runs, from its anonymous /v1/meta.
function Get-ServerVersion([string]$Url) {
    try {
        $meta = Invoke-RestMethod -Uri "$Url/v1/meta" -TimeoutSec 10 -UseBasicParsing
        if ($meta.serverVersion -match '^\d+\.\d+\.\d+$') { return $meta.serverVersion }
    } catch { }
    return $null
}

function Test-ReleaseCli([string]$V) {
    try {
        Invoke-WebRequest -Method Head -Uri "https://github.com/$Repo/releases/download/v$V/varlatch-cli-$V.cjs" `
            -TimeoutSec 15 -UseBasicParsing -UserAgent $UserAgent | Out-Null
        return $true
    } catch { return $false }
}

function Get-LatestRelease {
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -TimeoutSec 15 `
            -Headers @{ Accept = 'application/vnd.github+json' } -UserAgent $UserAgent -UseBasicParsing
        return ($release.tag_name -replace '^v', '')
    } catch { return $null }
}

function Get-NodeMajor {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { return 0 }
    try { return [int](& $node.Source -p "process.versions.node.split('.')[0]") } catch { return 0 }
}

# Tells Explorer and new terminals that the user PATH changed.
function Send-EnvironmentChange {
    if (-not ('Varlatch.Environment' -as [type])) {
        Add-Type -Namespace Varlatch -Name Environment -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
'@
    }
    $result = [UIntPtr]::Zero
    [void][Varlatch.Environment]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)
}

# Adds a directory to the user PATH, keeping the %VARIABLES% in it as they are.
function Add-UserPath([string]$Dir) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        $raw = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $entries = $raw -split ';' | Where-Object { $_ }
        $expanded = $entries | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') }
        if ($expanded -contains $Dir.TrimEnd('\')) { return $false }
        $key.SetValue('Path', (@($entries) + $Dir) -join ';', [Microsoft.Win32.RegistryValueKind]::ExpandString)
    } finally {
        $key.Close()
    }
    Send-EnvironmentChange
    return $true
}

Write-Host 'Install the Varlatch CLI' -ForegroundColor Cyan
Write-Host ''
Update-SessionPath
$target = Join-Path $InstallDir 'varlatch.cjs'
$shim = Join-Path $InstallDir 'varlatch.cmd'

if ((Test-Path $target) -and -not $Version) {
    $have = ''
    if ((Get-NodeMajor) -gt 0) { $have = (& node $target --version 2>$null | Select-Object -First 1) }
    Write-Host "The CLI is already installed: $target $have"
    Write-Host 'Update it from the Varlatch panel, or with: varlatch self-update'
    if ($App) { & $App --refresh }
    exit 0
}

# The CLI should match the Varlatch the server runs.
if (-not $Server -and -not $Version -and -not $Yes) {
    Write-Host '  The CLI should match the Varlatch your server runs. Give the server''s'
    Write-Host '  address to install that version, or press Enter for the latest release.'
    Write-Host ''
    $Server = Read-Host 'Server address'
}
$serverUrl = $null
if ($Server) {
    $serverUrl = ConvertTo-ServerAddress $Server
    if (-not $serverUrl) { Stop-Install "'$Server' is not a server address, such as varlatch.example.com." }
}

if ($Version) {
    $Version = $Version -replace '^v', ''
} elseif ($serverUrl) {
    $hostName = Get-HostName $serverUrl
    $running = Get-ServerVersion $serverUrl
    if (-not $running) {
        Write-Host "Could not read the version $hostName runs; using the latest release."
    } elseif (-not (Test-ReleaseCli $running)) {
        Write-Host "$hostName runs $running, which has no release CLI; using the latest release."
        Write-Host 'If the newer CLI does not work with it, upgrade the server (varlatch upgrade).'
    } else {
        Write-Host "$hostName runs Varlatch $running."
        $Version = $running
    }
}
if (-not $Version) { $Version = Get-LatestRelease }
if (-not $Version) { Stop-Install "cannot find the latest release of $Repo (offline?)." }

# Node.js 22 or newer runs the release CLI.
if ((Get-NodeMajor) -lt 22) {
    $found = (Get-Command node.exe -ErrorAction SilentlyContinue)
    $what = if ($found) { "Node.js $(& node --version) is too old" } else { 'Node.js is not installed' }
    Write-Host ''
    Write-Host "$what; the varlatch CLI needs Node.js 22 or newer."
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { Stop-Install 'install Node.js 22 or newer from https://nodejs.org, then run this again.' }
    if (-not (Confirm-Step 'Install Node.js LTS with winget?' $true)) { Stop-Install 'Node.js 22 or newer is needed. Nothing was installed.' }
    & $winget.Source install --id OpenJS.NodeJS.LTS --exact --source winget --accept-source-agreements --accept-package-agreements
    Update-SessionPath
    if ((Get-NodeMajor) -lt 22) { Stop-Install 'Node.js 22 or newer is still not on the PATH. Open a new terminal and run this again.' }
}

# Download and check, before anything is installed.
$asset = "varlatch-cli-$Version.cjs"
$base = "https://github.com/$Repo/releases/download/v$Version"
$work = Join-Path ([IO.Path]::GetTempPath()) ("varlatch-install-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Write-Host ''
    Write-Host "Downloading $asset from $Repo..."
    try {
        Invoke-WebRequest -Uri "$base/$asset" -OutFile (Join-Path $work $asset) -UseBasicParsing -UserAgent $UserAgent
        Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $work 'SHA256SUMS') -UseBasicParsing -UserAgent $UserAgent
    } catch {
        Stop-Install "download failed: $($_.Exception.Message)"
    }
    $bundle = Join-Path $work 'SHA256SUMS.sigstore.json'
    try {
        Invoke-WebRequest -Uri "$base/SHA256SUMS.sigstore.json" -OutFile $bundle -UseBasicParsing -UserAgent $UserAgent
    } catch {
        Remove-Item $bundle -ErrorAction SilentlyContinue
    }

    if (Test-Path $bundle) {
        $cosign = Get-Command cosign.exe -ErrorAction SilentlyContinue
        if ($cosign) {
            & $cosign.Source verify-blob (Join-Path $work 'SHA256SUMS') --bundle $bundle `
                --certificate-identity "https://github.com/$Repo/.github/workflows/release.yml@refs/tags/v$Version" `
                --certificate-oidc-issuer https://token.actions.githubusercontent.com 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) { Stop-Install 'the signature on SHA256SUMS does NOT verify. Nothing was installed.' }
            $signature = "verified: signed by the $Repo release workflow for v$Version"
        } else {
            $signature = 'NOT checked: the release is signed, but cosign is not installed'
        }
    } else {
        $signature = 'none: this release carries no signature'
    }

    # This asset's own line; a file SHA256SUMS does not list does not pass.
    $line = Get-Content (Join-Path $work 'SHA256SUMS') | Where-Object { $_ -match "^([0-9a-f]{64}) [ *]$([regex]::Escape($asset))$" } | Select-Object -First 1
    if (-not $line) { Stop-Install "SHA256SUMS does not list $asset. Nothing was installed." }
    $expected = ($line -split ' ')[0]
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $work $asset)).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { Stop-Install "$asset does not match SHA256SUMS. Nothing was installed." }

    $reported = (& node (Join-Path $work $asset) --version 2>$null | Select-Object -First 1)
    if ($reported -notmatch "^varlatch $([regex]::Escape($Version))( |$)") {
        Stop-Install "the downloaded CLI reports `"$reported`", expected varlatch $Version. Nothing was installed."
    }

    Write-Host ''
    Write-Host "  varlatch $Version"
    Write-Host "  install:   $target"
    Write-Host '  checksum:  ok (SHA256SUMS)'
    Write-Host "  signature: $signature"
    Write-Host "  changes:   https://github.com/$Repo/releases/tag/v$Version"
    Write-Host ''
    if (-not (Confirm-Step 'Install?' $false)) {
        Write-Host 'Nothing installed.'
        exit 130
    }

    # Written next to its place, then moved over it, so there is never a
    # half-written CLI.
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $next = Join-Path $InstallDir ".varlatch-new-$PID"
    Copy-Item (Join-Path $work $asset) $next -Force
    if (Test-Path $target) { [IO.File]::Replace($next, $target, $null) } else { [IO.File]::Move($next, $target) }
    [IO.File]::WriteAllText($shim, "@node `"%~dp0varlatch.cjs`" %*`r`n", (New-Object System.Text.UTF8Encoding $false))
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

Write-Host "Installed: $(& node $target --version | Select-Object -First 1)"
if (-not $NoPath) {
    if (Add-UserPath $InstallDir) {
        Write-Host "Added $InstallDir to your PATH: varlatch works in new terminals."
    }
    $env:Path = "$InstallDir;$env:Path"
}
if ($App -and (Test-Path $App)) { & $App --refresh }

if (-not $serverUrl) { exit 0 }
Write-Host ''
if (-not (Confirm-Step "Sign in to $(Get-HostName $serverUrl) now?" $true)) { exit 0 }
if ($App -and (Test-Path $App)) {
    # The app runs the browser sign-in and shows it in its panel.
    & $App --sign-in $serverUrl
    Write-Host 'Finish signing in in your browser; the Varlatch panel shows how it goes.'
} else {
    & node $target login --server $serverUrl
}
exit 0

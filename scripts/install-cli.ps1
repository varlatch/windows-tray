<#
.SYNOPSIS
Installs the Varlatch CLI for this user, checked, where Varlatch for Windows
looks for it.

.DESCRIPTION
Varlatch for Windows runs this in a terminal when the CLI is not installed
(Install in Terminal). It asks for your server's address, so it can install
the CLI version that server runs (press Enter for the latest release). Then
it downloads what that release's SHA256SUMS lists for this PC and checks it:

  from 0.50    varlatch.exe, a program of its own. It must match SHA256SUMS,
               run here, and confirm that Varlatch's release workflow signed
               SHA256SUMS.
  before 0.50  varlatch.cjs, which Node.js 22 or newer runs (the script
               offers to install Node.js with winget), with a varlatch.cmd
               that runs it. It must match SHA256SUMS, whose signature is
               checked too when the release carries one and cosign is
               installed.

It shows what it checked and installs only once you confirm, in
%LOCALAPPDATA%\Programs\Varlatch, which it puts on your user PATH, so
`varlatch` works in new terminals. varlatch.exe takes the place of a
varlatch.cjs and varlatch.cmd there: they go once it runs.

With -Update (Update in Terminal, for a varlatch.cjs), it updates that CLI:
to a release with varlatch.exe as above, in the CLI's own folder; to one with
a varlatch.cjs by the CLI's own self-update.

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
    # A varlatch.cjs to update, instead of installing.
    [string]$Update,
    # The node.exe to run a varlatch.cjs with; by default the PATH's.
    [string]$Node,
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
        if ($meta.serverVersion -match '^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$') { return $meta.serverVersion }
    } catch { }
    return $null
}

# A release file, or $false when the release does not have it.
function Get-ReleaseFile([string]$V, [string]$Name, [string]$Path) {
    try {
        Invoke-WebRequest -Uri "https://github.com/$Repo/releases/download/v$V/$Name" -OutFile $Path `
            -TimeoutSec 300 -UseBasicParsing -UserAgent $UserAgent | Out-Null
        return $true
    } catch {
        Remove-Item $Path -Force -ErrorAction SilentlyContinue
        return $false
    }
}

# The SHA-256 that SHA256SUMS lists for exactly that file name, once; else
# nothing.
function Get-ListedHash([string]$Sums, [string]$Name) {
    $found = @(Get-Content -LiteralPath $Sums | ForEach-Object {
        if ($_ -cmatch '^([0-9a-f]{64}) [ *](.+)$' -and $Matches[2] -ceq $Name) { $Matches[1] }
    })
    if ($found.Count -eq 1) { return $found[0] }
    return $null
}

# The CLI a release has for this PC, by what its SHA256SUMS lists: the
# program, else the single file that Node.js runs; nothing when it has neither.
function Get-ReleaseAsset([string]$V, [string]$Sums) {
    # A 32-bit PowerShell on a 64-bit Windows names the machine in the first.
    $machine = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $arch = switch ($machine) { 'AMD64' { 'amd64' } 'ARM64' { 'arm64' } default { $null } }
    $names = @("varlatch-cli-$V.cjs")
    if ($arch) { $names = @("varlatch-$V-windows-$arch.exe") + $names }
    foreach ($name in $names) { if (Get-ListedHash $Sums $name) { return $name } }
    return $null
}

function Get-Sha256([string]$Path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

# Whether "varlatch <version>" came back from the CLI just run.
function Test-Reported([string]$Reported, [string]$V) {
    return ($LASTEXITCODE -eq 0) -and ($Reported -ceq "varlatch $V" -or $Reported.StartsWith("varlatch $V "))
}

# The TypeScript CLI's single-file build names itself in its first lines.
function Test-TypeScriptCli([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $stream = [IO.File]::OpenRead($Path)
    try {
        $head = New-Object byte[] 512
        $read = $stream.Read($head, 0, $head.Length)
        return [Text.Encoding]::ASCII.GetString($head, 0, $read).Contains('Varlatch CLI')
    } finally { $stream.Dispose() }
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
    try { return [int](Invoke-Quietly $node.Source -p "process.versions.node.split('.')[0]") } catch { return 0 }
}

# A program's first line of output, with what it writes to stderr dropped.
# Windows PowerShell turns stderr into errors, which would stop this script
# when, say, cosign says "Verified OK" there; the exit status is in
# $LASTEXITCODE.
#
# No named parameters: every argument, -p included, goes to the program.
function Invoke-Quietly {
    $program = $args[0]
    # An array also for one argument: splatting a string passes its characters.
    $arguments = @($args | Select-Object -Skip 1)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $program @arguments 2>$null
        return ($out | Select-Object -First 1)
    } finally {
        $ErrorActionPreference = $previous
    }
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

# Node.js 22 or newer runs a varlatch.cjs; release $Version has no varlatch.exe.
function Confirm-Node {
    if ((Get-NodeMajor) -ge 22) { return }
    $found = (Get-Command node.exe -ErrorAction SilentlyContinue)
    $what = if ($found) { "Node.js $(Invoke-Quietly node --version) is too old" } else { 'Node.js is not installed' }
    Write-Host ''
    Write-Host "$what; the CLI of release $Version needs Node.js 22 or newer."
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { Stop-Install 'install Node.js 22 or newer from https://nodejs.org, then run this again.' }
    # Never without asking, even with -Yes: it changes the machine's Node.js
    # for everything else on it.
    if ($Yes) { Stop-Install 'Node.js 22 or newer is needed: install it (winget install OpenJS.NodeJS.LTS), then run this again.' }
    $answer = Read-Host 'Install Node.js LTS with winget? [Y/n]'
    if ($answer -and $answer -notmatch '^[yY]') { Stop-Install 'Node.js 22 or newer is needed. Nothing was installed.' }
    & $winget.Source install --id OpenJS.NodeJS.LTS --exact --source winget --accept-source-agreements --accept-package-agreements
    Update-SessionPath
    if ((Get-NodeMajor) -lt 22) { Stop-Install 'Node.js 22 or newer is still not on the PATH. Open a new terminal and run this again.' }
}

# Puts a checked file in place: written next to it, then moved over it, so
# there is never a half-written CLI. What was there is kept at $Backup.
function Set-CheckedFile([string]$From, [string]$To, [string]$Hash, [string]$Backup) {
    $next = Join-Path $InstallDir ".varlatch-new-$PID"
    try {
        Copy-Item -LiteralPath $From $next -Force
        # What goes in place is what was checked.
        if ((Get-Sha256 $next) -cne $Hash) { Stop-Install "$(Split-Path -Leaf $From) changed after it was checked. Nothing was installed." }
        try {
            if (Test-Path -LiteralPath $To) { [IO.File]::Replace($next, $To, $Backup) }
            else { [IO.File]::Move($next, $To) }
        } catch {
            Stop-Install "could not write $To (is varlatch running?): $($_.Exception.Message)"
        }
    } finally {
        Remove-Item -LiteralPath $next -Force -ErrorAction SilentlyContinue
    }
}

# varlatch.exe replaces the TypeScript CLI in its folder once it runs there:
# varlatch.cjs, and the varlatch.cmd that runs it. Both use the same store of
# sign-ins and settings.
function Remove-TypeScriptCli {
    $left = @()
    $removed = $false
    if ((Test-Path -LiteralPath $shim -PathType Leaf) -and [IO.File]::ReadAllText($shim).Trim() -ceq '@node "%~dp0varlatch.cjs" %*') {
        try { Remove-Item -LiteralPath $shim -Force -ErrorAction Stop; $removed = $true } catch { $left += 'varlatch.cmd' }
    }
    if (Test-TypeScriptCli $cjs) {
        try { Remove-Item -LiteralPath $cjs -Force -ErrorAction Stop; $removed = $true } catch { $left += 'varlatch.cjs' }
    }
    if ($left) { Write-Host "$($left -join ' and ') in $InstallDir could not be removed (in use?). varlatch.exe comes before it; remove it later." }
    elseif ($removed) { Write-Host 'It replaces varlatch.cjs. Your sign-ins and settings carry over.' }
}

Write-Host $(if ($Update) { 'Update the Varlatch CLI' } else { 'Install the Varlatch CLI' }) -ForegroundColor Cyan
Write-Host ''
Update-SessionPath
if ($Node) { $env:Path = "$(Split-Path -Parent $Node);$env:Path" }
if ($Update) {
    $InstallDir = Split-Path -Parent $Update
    # The shim that runs varlatch.cjs stands for it.
    if ((Split-Path -Leaf $Update) -ieq 'varlatch.cmd') { $Update = Join-Path $InstallDir 'varlatch.cjs' }
}
$exe = Join-Path $InstallDir 'varlatch.exe'
$cjs = Join-Path $InstallDir 'varlatch.cjs'
$shim = Join-Path $InstallDir 'varlatch.cmd'

if (-not $Version -and -not $Update) {
    $have = @($exe, $cjs) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($have) {
        $reported = ''
        if ($have -eq $exe) { $reported = Invoke-Quietly $exe --version }
        elseif ((Get-NodeMajor) -gt 0) { $reported = Invoke-Quietly node $cjs --version }
        Write-Host "The CLI is already installed: $have $reported"
        Write-Host 'Update it from the Varlatch panel.'
        if ($App) { & $App --refresh }
        exit 0
    }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("varlatch-install-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $sums = Join-Path $work 'SHA256SUMS'

    # The CLI should match the Varlatch the server runs.
    if (-not $Server -and -not $Version -and -not $Update -and -not $Yes) {
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
        } elseif (-not ((Get-ReleaseFile $running 'SHA256SUMS' $sums) -and (Get-ReleaseAsset $running $sums))) {
            Write-Host "$hostName runs $running, which has no release CLI; using the latest release."
            Write-Host 'If the newer CLI does not work with it, upgrade the server (varlatch upgrade).'
        } else {
            Write-Host "$hostName runs Varlatch $running."
            $Version = $running
        }
    }
    if (-not $Version) { $Version = Get-LatestRelease }
    if (-not $Version) { Stop-Install "cannot find the latest release of $Repo (offline?)." }

    # Download and check, before anything is installed.
    if (-not (Get-ReleaseFile $Version 'SHA256SUMS' $sums)) { Stop-Install "download failed: release $Version has no SHA256SUMS (offline?)." }
    $asset = Get-ReleaseAsset $Version $sums
    if (-not $asset) { Stop-Install "release $Version has no CLI for this PC. Nothing was installed." }
    $native = $asset.EndsWith('.exe')
    if ($Update) {
        if (-not $native) {
            # The CLI replaces its own file, with the same checks, and asks first.
            Confirm-Node
            & node $Update self-update $Version
            exit $LASTEXITCODE
        }
        if ((Split-Path -Leaf $Update) -ine 'varlatch.cjs') {
            Stop-Install "release $Version is varlatch.exe, which cannot take the place of $Update. Clear the CLI path in Settings, then install the CLI again."
        }
    } elseif (-not $native -and (Test-Path -LiteralPath $exe)) {
        Stop-Install "$exe is installed and would come before the CLI of release $Version. Remove it first."
    }
    if (-not $native) { Confirm-Node }
    $target = if ($native) { $exe } else { $cjs }

    Write-Host ''
    Write-Host "Downloading $asset from $Repo..."
    $staged = Join-Path $work $asset
    if (-not (Get-ReleaseFile $Version $asset $staged)) { Stop-Install "download failed: $asset." }
    $bundle = Join-Path $work 'SHA256SUMS.sigstore.json'
    $signed = Get-ReleaseFile $Version 'SHA256SUMS.sigstore.json' $bundle
    $signature = 'none: this release carries no signature'
    if ($signed) {
        $signature = 'NOT checked: the release is signed, but cosign is not installed'
        $cosign = Get-Command cosign.exe -ErrorAction SilentlyContinue
        if ($cosign) {
            Invoke-Quietly $cosign.Source verify-blob $sums --bundle $bundle `
                --certificate-identity "https://github.com/$Repo/.github/workflows/release.yml@refs/tags/v$Version" `
                --certificate-oidc-issuer https://token.actions.githubusercontent.com | Out-Null
            if ($LASTEXITCODE -ne 0) { Stop-Install 'the signature on SHA256SUMS does NOT verify. Nothing was installed.' }
            $signature = "verified: signed by the $Repo release workflow for v$Version"
        }
    }

    # This asset's own line, which Get-ReleaseAsset found once.
    $expected = Get-ListedHash $sums $asset
    if ((Get-Sha256 $staged) -cne $expected) { Stop-Install "$asset does not match SHA256SUMS. Nothing was installed." }

    # A program must run here, and it checks the signature on SHA256SUMS
    # itself, as Varlatch's install.ps1 has it do: every release with one is
    # signed.
    if ($native -and -not $signed) { Stop-Install "release $Version carries no signature. Nothing was installed." }
    $global:LASTEXITCODE = -1
    try {
        $reported = if ($native) { Invoke-Quietly $staged --version } else { Invoke-Quietly node $staged --version }
    } catch { $reported = '' }
    if (-not (Test-Reported "$reported" $Version)) {
        Stop-Install "the downloaded CLI reports `"$reported`", expected varlatch $Version. Nothing was installed."
    }
    if ($native) {
        $global:LASTEXITCODE = -1
        Invoke-Quietly $staged verify-release --version $Version --sums $sums --bundle $bundle --file "$staged=$asset" | Out-Null
        if ($LASTEXITCODE -ne 0) { Stop-Install 'the signature on SHA256SUMS does NOT verify. Nothing was installed.' }
        $signature = "verified: signed by the $Repo release workflow for v$Version"
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

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $had = Test-Path -LiteralPath $target
    $backup = Join-Path $InstallDir "varlatch-old-$PID.bak"
    Set-CheckedFile $staged $target $expected $backup
    if (-not $native) { [IO.File]::WriteAllText($shim, "@node `"%~dp0varlatch.cjs`" %*`r`n", (New-Object System.Text.UTF8Encoding $false)) }

    # What was there goes only once the new CLI runs in its place.
    $global:LASTEXITCODE = -1
    try {
        $reported = if ($native) { Invoke-Quietly $target --version } else { Invoke-Quietly node $target --version }
    } catch { $reported = '' }
    if (-not (Test-Reported "$reported" $Version)) {
        Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        if ($had) { [IO.File]::Move($backup, $target) }
        if ($had -or (Test-TypeScriptCli $cjs)) { Stop-Install "the new CLI does not run at $target. The CLI that was there is left in place." }
        Stop-Install "the new CLI does not run at $target. It was removed again."
    }
    if ($had) { Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue }
    Write-Host "Installed: $reported"
    if ($native) { Remove-TypeScriptCli }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

# Updated in its folder, which is found as it was.
if ($Update) { exit 0 }
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
    if ($native) { & $target login --server $serverUrl } else { & node $target login --server $serverUrl }
}
exit 0

<#
Tests scripts/install-cli.ps1 without a network: releases of both generations
are made in a temporary folder, with stand-ins for the CLI, and the script's
requests and questions are answered from there.

  powershell -NoProfile -ExecutionPolicy Bypass -File tests/install-cli.tests.ps1

Needs Node.js 22 or newer, for the CLI before 0.50. Also runs in PowerShell 7
on Linux, where the stand-in for varlatch.exe is a shell script.
#>
$ErrorActionPreference = 'Stop'
$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/install-cli.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('varlatch-install-tests-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$onLinux = [bool](Get-Variable IsLinux -ValueOnly -ErrorAction SilentlyContinue)
$passed = 0
$failed = 0

function Check([string]$Label, [bool]$Condition) {
    if ($Condition) { $script:passed++; Write-Host "ok    $Label" }
    else { $script:failed++; Write-Host "FAIL  $Label"; $script:output -split "`n" | ForEach-Object { Write-Host "      $_" } }
}

# The stand-ins. Each records its calls in <root>\calls.log, and the ones
# made with -BreaksInPlace run as downloaded, but not once installed.
$exes = @{}
function New-Program([string]$Path, [string]$Reports, [switch]$BadSignature, [switch]$BreaksInPlace) {
    $log = Join-Path $root 'calls.log'
    if ($onLinux) {
        $breaks = if ($BreaksInPlace) { 'case $0 in *-windows-*) ;; *) exit 126 ;; esac' } else { '' }
        $signature = if ($BadSignature) { 1 } else { 0 }
        [IO.File]::WriteAllText($Path, "#!/bin/sh`n$breaks`necho `"native `$*`" >> '$log'`n[ `"`$1`" = --version ] && echo 'varlatch $Reports'`n[ `"`$1`" = verify-release ] && exit $signature`nexit 0`n")
        chmod 755 $Path
        return
    }
    $key = "$Reports-$BadSignature-$BreaksInPlace"
    if (-not $exes[$key]) {
        $exes[$key] = Join-Path $root "program-$($exes.Count).exe"
        $breaksLine = if ($BreaksInPlace) { 'if (Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location) == "varlatch.exe") return 126;' } else { '' }
        $verifyResult = if ($BadSignature) { '1' } else { '0' }
        Add-Type -OutputType ConsoleApplication -OutputAssembly $exes[$key] -TypeDefinition @"
using System;
using System.IO;
static class Program$($exes.Count) {
    static int Main(string[] args) {
        $breaksLine
        File.AppendAllText(@"$log", "native " + string.Join(" ", args) + "\n");
        if (args.Length > 0 && args[0] == "--version") Console.WriteLine("varlatch $Reports");
        if (args.Length > 0 && args[0] == "verify-release") return $verifyResult;
        return 0;
    }
}
"@
    }
    Copy-Item $exes[$key] $Path
}

function Get-Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 $Path).Hash.ToLowerInvariant() }

# A release: what its SHA256SUMS lists, with any other change made after.
function New-Release([string]$V, [switch]$Native, [string]$Reports = $V, [switch]$BadSignature, [switch]$BreaksInPlace) {
    $dir = Join-Path $root "releases/v$V"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    if ($Native) {
        $name = "varlatch-$V-windows-amd64.exe"
        New-Program (Join-Path $dir $name) $Reports -BadSignature:$BadSignature -BreaksInPlace:$BreaksInPlace
        [IO.File]::WriteAllText((Join-Path $dir 'SHA256SUMS.sigstore.json'), '{}')
    } else {
        $name = "varlatch-cli-$V.cjs"
        New-Script (Join-Path $dir $name) $V
    }
    [IO.File]::WriteAllText((Join-Path $dir 'SHA256SUMS'), "$(Get-Hash (Join-Path $dir $name))  $name`n$('0' * 64)  other`n")
}

function New-Script([string]$Path, [string]$V) {
    $log = (Join-Path $root 'calls.log') -replace '\\', '/'
    [IO.File]::WriteAllText($Path, @"
#!/usr/bin/env node
/*! Varlatch CLI. Copyright 2026 Robotsson. (test stand-in) */
const [command, version] = process.argv.slice(2);
if (command === '--version') console.log('varlatch $V (migration 0)');
else require('fs').appendFileSync('$log', 'typescript ' + command + ' ' + version + '\n');
"@)
}

# What the script asks of the network and of the person. Functions come
# before commands, also in the script.
$global:VarlatchTest = @{}
function global:Invoke-WebRequest {
    param($Uri, $OutFile, $TimeoutSec, [switch]$UseBasicParsing, $UserAgent, $Method)
    $path = Join-Path $global:VarlatchTest.Releases ($Uri -replace '^https://github.com/varlatch/varlatch/releases/download/', '')
    if (-not (Test-Path $path)) { throw "404 $Uri" }
    Copy-Item $path $OutFile
}
function global:Invoke-RestMethod {
    param($Uri, $TimeoutSec, $Headers, $UserAgent, [switch]$UseBasicParsing)
    if ($Uri -like '*/releases/latest' -and $global:VarlatchTest.Latest) { return [pscustomobject]@{ tag_name = "v$($global:VarlatchTest.Latest)" } }
    if ($Uri -like '*/v1/meta' -and $global:VarlatchTest.Running) { return [pscustomobject]@{ serverVersion = $global:VarlatchTest.Running } }
    throw "404 $Uri"
}
function global:Read-Host { param($Prompt) return $global:VarlatchTest.Answers.Dequeue() }

# Runs the script; its output is in $script:output and its exit status in $code.
function Invoke-Installer([hashtable]$With, [string[]]$Answers = @(), [string]$Latest, [string]$Running) {
    $global:VarlatchTest = @{
        Releases = Join-Path $root 'releases'; Latest = $Latest; Running = $Running
        Answers = New-Object System.Collections.Queue (, $Answers)
    }
    Remove-Item (Join-Path $root 'calls.log') -ErrorAction SilentlyContinue
    $global:LASTEXITCODE = 0
    $script:output = (& $installer @With -NoPath *>&1 | Out-String)
    $script:code = $LASTEXITCODE
}

function Calls { $log = Join-Path $root 'calls.log'; if (Test-Path $log) { [IO.File]::ReadAllText($log) } else { '' } }

# A new folder to install in, with no releases yet.
function Reset-Test {
    Remove-Item -Recurse -Force (Join-Path $root 'releases'), (Join-Path $root 'cli') -ErrorAction SilentlyContinue
    $script:cli = Join-Path $root 'cli'
    $script:exe = Join-Path $cli 'varlatch.exe'
    $script:cjs = Join-Path $cli 'varlatch.cjs'
    $script:shim = Join-Path $cli 'varlatch.cmd'
}

# The CLI of 0.23 as this script installs it.
function Add-TypeScriptCli {
    New-Item -ItemType Directory -Force -Path $cli | Out-Null
    New-Script $cjs '9.9.8'
    [IO.File]::WriteAllText($shim, "@node `"%~dp0varlatch.cjs`" %*`r`n")
}
function Version([string]$Path) { return (& $Path --version) }
function Names { return (Get-ChildItem -Force $cli -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object Name) -join ' ' }

New-Item -ItemType Directory -Path $root | Out-Null
$architecture = $env:PROCESSOR_ARCHITECTURE, $env:PROCESSOR_ARCHITEW6432
try {
    $env:PROCESSOR_ARCHITECTURE = 'AMD64'
    $env:PROCESSOR_ARCHITEW6432 = ''

    Reset-Test; New-Release '9.9.8'
    Invoke-Installer @{ InstallDir = $cli } -Answers '', 'y' -Latest '9.9.8'
    Check 'a release with the single file: varlatch.cjs and its shim' ($code -eq 0 -and (Names) -eq 'varlatch.cjs varlatch.cmd')
    Invoke-Installer @{ InstallDir = $cli } -Latest '9.9.8'
    Check 'already installed: nothing is asked' ($code -eq 0 -and $output -match 'already installed')
    Invoke-Installer @{ InstallDir = $cli; Version = '9.9.8'; Yes = $true }
    Check 'installed again over it' ($code -eq 0 -and (Names) -eq 'varlatch.cjs varlatch.cmd')

    Reset-Test; New-Release '10.0.0' -Native
    Invoke-Installer @{ InstallDir = $cli } -Answers '', 'y' -Latest '10.0.0'
    Check 'a native release: varlatch.exe' ($code -eq 0 -and (Names) -eq 'varlatch.exe' -and (Version $exe) -eq 'varlatch 10.0.0')
    Check 'it checked the signature of the release' ($output -match 'signature: verified' -and (Calls) -match 'native verify-release --version 10\.0\.0 --sums .*SHA256SUMS --bundle .*SHA256SUMS\.sigstore\.json --file .*=varlatch-10\.0\.0-windows-amd64\.exe')
    Invoke-Installer @{ InstallDir = $cli } -Latest '10.0.0'
    Check 'varlatch.exe already installed' ($code -eq 0 -and $output -match 'already installed: .*varlatch 10\.0\.0')
    Invoke-Installer @{ InstallDir = $cli; Version = '10.0.0'; Yes = $true }
    Check 'installed again over it: nothing left beside it' ($code -eq 0 -and (Names) -eq 'varlatch.exe')
    New-Release '9.9.8'
    Invoke-Installer @{ InstallDir = $cli; Version = '9.9.8'; Yes = $true }
    Check 'a varlatch.cjs that varlatch.exe would hide is refused' ($code -eq 1 -and (Names) -eq 'varlatch.exe')

    Reset-Test; New-Release '9.9.8'; New-Release '10.0.0' -Native; New-Release '10.0.0-rc.1' -Native
    Invoke-Installer @{ InstallDir = $cli } -Answers 'vl.example.com', 'y', 'n' -Latest '10.0.0' -Running '9.9.8'
    Check 'a server of the TypeScript generation: its single file' ((Names) -eq 'varlatch.cjs varlatch.cmd')
    Reset-Test; New-Release '9.9.8'; New-Release '10.0.0' -Native; New-Release '10.0.0-rc.1' -Native
    Invoke-Installer @{ InstallDir = $cli } -Answers 'vl.example.com', 'y', 'n' -Latest '9.9.8' -Running '10.0.0'
    Check 'a native server while the latest release is not: its program' ((Names) -eq 'varlatch.exe' -and (Version $exe) -eq 'varlatch 10.0.0')
    Reset-Test; New-Release '9.9.8'; New-Release '10.0.0' -Native; New-Release '10.0.0-rc.1' -Native
    Invoke-Installer @{ InstallDir = $cli } -Answers 'vl.example.com', 'y', 'n' -Latest '9.9.8' -Running '10.0.0-rc.1'
    Check 'a server on a prerelease: that prerelease' ((Names) -eq 'varlatch.exe' -and (Version $exe) -eq 'varlatch 10.0.0-rc.1')
    Reset-Test; New-Release '10.0.0' -Native
    Invoke-Installer @{ InstallDir = $cli } -Answers 'vl.example.com', 'y', 'n' -Latest '10.0.0' -Running '9.9.7'
    Check 'a server whose release has no CLI: the latest' ($output -match 'has no release CLI' -and (Names) -eq 'varlatch.exe')

    # Nothing may be installed, or run, when a check fails.
    function Refused([string]$Label, [string]$Message) {
        Invoke-Installer @{ InstallDir = $cli; Yes = $true; Version = '10.0.0' }
        Check $Label ($code -eq 1 -and ($output -replace '\s+', ' ') -match $Message -and -not (Names))
    }
    $sums = Join-Path $root 'releases/v10.0.0/SHA256SUMS'
    Reset-Test; New-Release '10.0.0' -Native
    [IO.File]::WriteAllText($sums, "$('0' * 64)  varlatch-10.0.0-windows-amd64.exe`n")
    Refused 'checksum mismatch: nothing installed' 'does not match SHA256SUMS'
    Check 'and the program is never run' (-not (Calls))
    [IO.File]::WriteAllText($sums, "$('0' * 64)  other`n")
    Refused 'not listed in SHA256SUMS: nothing installed' 'has no CLI for this PC'
    $line = "$('0' * 64)  varlatch-10.0.0-windows-amd64.exe`n"
    [IO.File]::WriteAllText($sums, $line + $line)
    Refused 'listed twice: nothing installed' 'has no CLI for this PC'
    Remove-Item $sums
    Refused 'no SHA256SUMS: nothing installed' 'has no SHA256SUMS'
    Reset-Test; New-Release '10.0.0' -Native; Remove-Item (Join-Path $root 'releases/v10.0.0/SHA256SUMS.sigstore.json')
    Refused 'an unsigned native release: nothing installed' 'carries no signature'
    Reset-Test; New-Release '10.0.0' -Native -BadSignature
    Refused 'a signature that does not verify: nothing installed' 'does NOT verify'
    Reset-Test; New-Release '10.0.0' -Native -Reports '9.0.0'
    Refused 'a program of another version: nothing installed' 'reports "varlatch 9\.0\.0"'
    Reset-Test; New-Release '10.0.0' -Native -BreaksInPlace
    Refused 'a program that does not run in its place is removed again' 'removed again'
    $env:PROCESSOR_ARCHITECTURE = 'x86'
    Reset-Test; New-Release '10.0.0' -Native
    Refused 'a PC without a program: nothing installed' 'has no CLI for this PC'
    $env:PROCESSOR_ARCHITECTURE = 'AMD64'
    Reset-Test; New-Release '9.9.8'
    [IO.File]::WriteAllText((Join-Path $root 'releases/v9.9.8/SHA256SUMS'), "$('0' * 64)  varlatch-cli-9.9.8.cjs`n")
    Invoke-Installer @{ InstallDir = $cli; Yes = $true; Version = '9.9.8' }
    Check 'checksum mismatch of the single file: nothing installed' ($code -eq 1 -and -not (Names))

    Reset-Test; Add-TypeScriptCli; New-Release '10.0.0' -Native
    Invoke-Installer @{ Update = $cjs; Version = '10.0.0' } -Answers 'n'
    Check 'update to a native release, declined: the CLI stays' ($code -eq 130 -and (Names) -eq 'varlatch.cjs varlatch.cmd')
    Invoke-Installer @{ Update = $cjs; Version = '10.0.0' } -Answers 'y'
    Check 'update to a native release: varlatch.exe takes its place' ($code -eq 0 -and (Names) -eq 'varlatch.exe' -and (Version $exe) -eq 'varlatch 10.0.0')
    Check 'its self-update, which would refuse, is not run' (-not ((Calls) -match 'typescript'))
    Reset-Test; Add-TypeScriptCli; New-Release '10.0.0' -Native
    Invoke-Installer @{ Update = $shim } -Answers 'y' -Latest '10.0.0'
    Check 'through its shim, to the latest release' ($code -eq 0 -and (Names) -eq 'varlatch.exe')
    Reset-Test; Add-TypeScriptCli; New-Release '10.0.0' -Native; [IO.File]::WriteAllText($shim, "@echo mine`r`n")
    Invoke-Installer @{ Update = $cjs; Version = '10.0.0'; Yes = $true }
    Check 'a varlatch.cmd of another kind stays' ($code -eq 0 -and (Names) -eq 'varlatch.cmd varlatch.exe')
    Reset-Test; Add-TypeScriptCli; New-Release '10.0.0' -Native -BreaksInPlace
    Invoke-Installer @{ Update = $cjs; Version = '10.0.0'; Yes = $true }
    Check 'the new one does not run in its place: the CLI stays' ($code -eq 1 -and (Names) -eq 'varlatch.cjs varlatch.cmd' -and $output -match 'left in place')
    Reset-Test; Add-TypeScriptCli; New-Release '10.0.0' -Native
    [IO.File]::WriteAllText($sums, "$('0' * 64)  varlatch-10.0.0-windows-amd64.exe`n")
    Invoke-Installer @{ Update = $cjs; Version = '10.0.0'; Yes = $true }
    Check 'update, checksum mismatch: the CLI stays' ($code -eq 1 -and (Names) -eq 'varlatch.cjs varlatch.cmd')
    Reset-Test; Add-TypeScriptCli; New-Release '9.9.9'
    Invoke-Installer @{ Update = $cjs; Version = '9.9.9' }
    Check 'update to a release with the single file: the CLI updates itself' ((Calls) -match 'typescript self-update 9\.9\.9')
    Reset-Test; Add-TypeScriptCli; New-Release '10.0.0' -Native
    $other = Join-Path $cli 'my-varlatch.cjs'; Move-Item $cjs $other
    Invoke-Installer @{ Update = $other; Version = '10.0.0'; Yes = $true }
    Check 'a file of another name is left alone' ($code -eq 1 -and (Names) -eq 'my-varlatch.cjs varlatch.cmd')

    # varlatch.exe over varlatch.exe: the one that was there comes back.
    Reset-Test; New-Release '10.0.0' -Native; New-Release '10.0.1' -Native -BreaksInPlace
    Invoke-Installer @{ InstallDir = $cli; Version = '10.0.0'; Yes = $true }
    Invoke-Installer @{ InstallDir = $cli; Version = '10.0.1'; Yes = $true }
    Check 'a varlatch.exe that was there comes back' ($code -eq 1 -and (Names) -eq 'varlatch.exe' -and (Version $exe) -eq 'varlatch 10.0.0')
} finally {
    $env:PROCESSOR_ARCHITECTURE, $env:PROCESSOR_ARCHITEW6432 = $architecture
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    Remove-Item function:global:Invoke-WebRequest, function:global:Invoke-RestMethod, function:global:Read-Host -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "$passed passed, $failed failed"
if ($failed -or -not $passed) { exit 1 }

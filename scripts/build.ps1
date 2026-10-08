<#
.SYNOPSIS
Builds the release archives of Varlatch for Windows in dist/.

.DESCRIPTION
For each runtime (win-x64 and win-arm64 by default): publishes the app,
self-contained, with uninstall.ps1 next to it, and zips it as
Varlatch-for-Windows-<version>-<runtime>.zip. Then copies install.ps1 and
writes SHA256SUMS for all of them, as the release carries them.

.PARAMETER Version
The app's version; by default the latest v* tag, or 0.0.0 without one.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string[]]$Runtime = @('win-x64', 'win-arm64'),
    [string]$Out = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# git, or nothing when it fails (no tags, no commits, no git).
function Invoke-Git {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git @args 2>$null
        if ($LASTEXITCODE -eq 0) { return $out }
    } catch {
    } finally {
        $ErrorActionPreference = $previous
    }
    return $null
}
Push-Location $root
try {
    if (-not $Version) {
        $tag = Invoke-Git describe --tags --abbrev=0 --match 'v*'
        $Version = if ($tag) { $tag -replace '^v', '' } else { '0.0.0' }
    }
    $revision = Invoke-Git rev-parse --short HEAD
    if (-not $revision) { $revision = '' }
    if ($revision -and (Invoke-Git status --porcelain)) { $revision += '-dirty' }

    $dist = Join-Path $root $Out
    Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $dist | Out-Null

    foreach ($rid in $Runtime) {
        $publish = Join-Path $dist "publish\$rid"
        Write-Host "Publishing $rid..."
        dotnet publish src/Varlatch/Varlatch.csproj -c Release -r $rid -o $publish --nologo `
            "-p:Version=$Version" "-p:SourceRevision=$revision"
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
        Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item
        Copy-Item scripts/uninstall.ps1 $publish
        $zip = Join-Path $dist "Varlatch-for-Windows-$Version-$rid.zip"
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
    }
    Copy-Item scripts/install.ps1 $dist

    $sums = Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'SHA256SUMS' } | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"
    }
    [IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS'), (($sums -join "`n") + "`n"))
    Remove-Item -Recurse -Force (Join-Path $dist 'publish')
    Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
} finally {
    Pop-Location
}

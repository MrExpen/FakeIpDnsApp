#requires -Version 5.1
<#
.SYNOPSIS
    Publishes the DNS App and packs it into a flat zip that the DNS Server can install.

.DESCRIPTION
    The DNS Server loads an app only if <AppName>.dll and <AppName>.deps.json are in the top
    directory of the app folder, so the archive must be flat (no publish/ subfolder).

    Compile-time-only references (DnsServerCore.ApplicationCommon, TechnitiumLibrary,
    TechnitiumLibrary.Net) are deliberately excluded: the DNS Server already provides them in
    its own load context. Shipping them would make the app load them into its own collectible
    AssemblyLoadContext and the IDnsApplication interface check would fail.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = $null,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'src\FakeIpDnsApp'
$project    = Join-Path $projectDir 'FakeIpDnsApp.csproj'
$distDir    = Join-Path $root 'dist'
$stageDir   = Join-Path $distDir 'stage'

if (-not (Test-Path (Join-Path $root 'libs\DnsServerCore.ApplicationCommon.dll'))) {
    throw 'libs/ is not populated. Run build/get-libs.ps1 first.'
}

if (-not $SkipBuild) {
    dotnet publish $project -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}

$publishDir = Join-Path $projectDir "bin\$Configuration\publish"
if (-not (Test-Path $publishDir)) { throw "publish folder not found: $publishDir" }

$appDll  = Get-ChildItem $publishDir -Filter '*.dll' | Where-Object { $_.Name -notmatch '^(TechnitiumLibrary|DnsServerCore\.)' } | Select-Object -First 1
if (-not $appDll) { throw 'app assembly not found in publish output' }

if (-not $Version) {
    $Version = (Get-Content $project -Raw | Select-String -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
}

# flat staging: everything from publish output except host-provided assemblies and symbols
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$excluded = '^(TechnitiumLibrary|DnsServerCore\.)|\.pdb$|\.deps\.json$|\.dll\.config$'
Get-ChildItem $publishDir -File | Where-Object { $_.Name -notmatch $excluded } |
    Copy-Item -Destination $stageDir -Force

# the deps.json of the app assembly itself is mandatory for the loader
Copy-Item (Join-Path $publishDir ($appDll.BaseName + '.deps.json')) -Destination $stageDir -Force

$zip = Join-Path $distDir ("FakeIpDnsApp-{0}.zip" -f $Version)
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host ''
Write-Host "Created $zip"
Write-Host 'Archive contents (flat layout, required by the DNS Server app loader):'
Get-ChildItem $stageDir -File | ForEach-Object { '  ' + $_.Name }

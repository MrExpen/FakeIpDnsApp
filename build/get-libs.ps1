#requires -Version 5.1
<#
.SYNOPSIS
    Clones and builds the third-party assemblies that FakeIpDnsApp needs for compilation,
    then copies them into .\libs.

.DESCRIPTION
    The app references DnsServerCore.ApplicationCommon, TechnitiumLibrary and TechnitiumLibrary.Net
    as plain <Reference><HintPath> assemblies (Private=false), exactly like the Technitium DNS Server
    references TechnitiumLibrary. These DLLs are required only at compile time: at runtime they are
    provided by the DNS Server process that loads this app.

    Versions are pinned - see versions.json.
#>
[CmdletBinding()]
param(
    [string]$DnsServerRepo = 'https://github.com/TechnitiumSoftware/DnsServer.git',
    [string]$LibraryRepo  = 'https://github.com/TechnitiumSoftware/TechnitiumLibrary.git'
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$depsDir = Join-Path $root '.deps'
$libsDir = Join-Path $root 'libs'

$pin    = Get-Content (Join-Path $root 'versions.json') -Raw | ConvertFrom-Json
$dnsTag = $pin.dnsServerTag
$libTag = $pin.technitiumLibraryTag

New-Item -ItemType Directory -Path $libsDir -Force | Out-Null

foreach ($item in @(
    @{ url = $DnsServerRepo; tag = $dnsTag; dir = 'DnsServer' },
    @{ url = $LibraryRepo;  tag = $libTag; dir = 'TechnitiumLibrary' }
)) {
    $dir = Join-Path $depsDir $item.dir
    if (-not (Test-Path (Join-Path $dir '.git'))) {
        Write-Host "Cloning $($item.url) ($($item.tag)) -> $dir"
        git clone --depth 1 --branch $item.tag $item.url $dir
        if ($LASTEXITCODE -ne 0) { throw "git clone failed for $($item.url)" }
    }
}

$projects = @(
    (Join-Path $depsDir 'TechnitiumLibrary\TechnitiumLibrary\TechnitiumLibrary.csproj'),
    (Join-Path $depsDir 'TechnitiumLibrary\TechnitiumLibrary.Net\TechnitiumLibrary.Net.csproj'),
    (Join-Path $depsDir 'DnsServer\DnsServerCore.ApplicationCommon\DnsServerCore.ApplicationCommon.csproj')
)

foreach ($project in $projects) {
    Write-Host "Building $project"
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
}

# TechnitiumLibrary projects output their Release assemblies into <repo>\bin by design.
$copies = @(
    (Join-Path $depsDir 'TechnitiumLibrary\bin\TechnitiumLibrary.dll'),
    (Join-Path $depsDir 'TechnitiumLibrary\bin\TechnitiumLibrary.Net.dll'),
    (Join-Path $depsDir 'DnsServer\DnsServerCore.ApplicationCommon\bin\Release\DnsServerCore.ApplicationCommon.dll')
)

foreach ($source in $copies) {
    if (-not (Test-Path $source)) { throw "expected assembly not found: $source" }
    Copy-Item $source -Destination $libsDir -Force
    Write-Host "copied $(Split-Path -Leaf $source) -> libs\"
}

Write-Host ''
Write-Host 'libs/ is ready. Note: these DLLs are compile-time only and must NOT be shipped inside the app zip.'

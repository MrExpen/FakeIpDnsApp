#requires -Version 5.1
<#
.SYNOPSIS
    Fills .\libs with the third-party assemblies that FakeIpDnsApp needs to compile.

.DESCRIPTION
    Tags are pinned in versions.json as full tag names, exactly as they are spelled in the upstream
    repositories; the script clones those tags as they are, without composing names:

        "dnsServerTag":         "v15.2.0"             -> DnsServer at tag          v15.2.0
        "technitiumLibraryTag": "dns-server-v15.2.0"  -> TechnitiumLibrary at tag  dns-server-v15.2.0

    TechnitiumLibrary has no numbering of its own: its tags are named after the DNS Server release
    that used them, hence the "dns-server-" prefix inside the tag name.

    The cloned trees are kept in .\deps (gitignored). A tree that already exists is fetched and
    checked out to the requested tag, so switching versions does not silently reuse an old tree.

    These DLLs are compile-time only: at runtime they are provided by the DNS Server process, and
    they must never end up next to the app assembly inside the installed app folder.

.PARAMETER ReleaseVersion
    Bumps both dependencies to another release in one go, for example -ReleaseVersion v15.3.0. The
    DNS Server tag becomes v15.3.0 and the pinned TechnitiumLibrary tag gets its version part
    replaced, so dns-server-v15.2.0 turns into dns-server-v15.3.0.

.PARAMETER DnsServerTag
    Overrides versions.json dnsServerTag with a full tag name, for example -DnsServerTag v15.3.0.

.PARAMETER LibraryTag
    Overrides versions.json technitiumLibraryTag with a full tag name, for example
    -LibraryTag dns-server-v15.1.0.

.PARAMETER DryRun
    Print the resolved repository/tag pairs, check that both tags exist upstream and stop without
    touching the disk.

.EXAMPLE
    .\build\get-libs.ps1
.EXAMPLE
    .\build\get-libs.ps1 -ReleaseVersion v15.3.0
.EXAMPLE
    .\build\get-libs.ps1 -LibraryTag dns-server-v15.1.0 -DryRun
#>
[CmdletBinding()]
param(
    [string]$DnsServerRepo = 'https://github.com/TechnitiumSoftware/DnsServer.git',
    [string]$LibraryRepo   = 'https://github.com/TechnitiumSoftware/TechnitiumLibrary.git',
    [string]$ReleaseVersion,
    [string]$DnsServerTag,
    [string]$LibraryTag,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$depsDir = Join-Path $root '.deps'
$libsDir = Join-Path $root 'libs'

$pin = Get-Content (Join-Path $root 'versions.json') -Raw | ConvertFrom-Json

$dnsTag = if ($DnsServerTag) {
    $DnsServerTag
} elseif ($ReleaseVersion) {
    $ReleaseVersion
} else {
    $pin.dnsServerTag
}

$libTag = if ($LibraryTag) {
    $LibraryTag
} elseif ($ReleaseVersion) {
    # swap only the version part of the pinned tag, its prefix ("dns-server-") stays as it is
    if ($pin.technitiumLibraryTag -notmatch 'v\d+\.\d+\.\d+$') {
        throw "cannot derive a tag from versions.json technitiumLibraryTag '$($pin.technitiumLibraryTag)': it has no vMAJOR.MINOR.PATCH part; pass -LibraryTag instead"
    }
    $pin.technitiumLibraryTag -replace 'v\d+\.\d+\.\d+$', $ReleaseVersion
} else {
    $pin.technitiumLibraryTag
}

foreach ($checked in @(
    @{ name = 'dnsServerTag';         value = $dnsTag },
    @{ name = 'technitiumLibraryTag'; value = $libTag }
)) {
    if ($checked.value -notmatch '^(?:[a-z]+-)*v\d+\.\d+\.\d+$') {
        throw "$($checked.name) must be a full tag name (v15.2.0 or dns-server-v15.2.0), got '$($checked.value)' (see versions.json)"
    }
}

function Assert-RemoteTag {
    param([string]$Url, [string]$Tag)

    $refs = git ls-remote --tags $Url
    if ($LASTEXITCODE -ne 0) { throw "git ls-remote failed for $Url" }

    $found = $refs | Where-Object { ($_ -split "`t")[-1] -eq "refs/tags/$Tag" }
    if ($found) { return }

    $available = ($refs |
        Where-Object { ($_ -split "`t")[-1] -match '^refs/tags/[^^]+$' } |
        ForEach-Object { ($_ -split "`t")[-1] -replace '^refs/tags/', '' } |
        Select-Object -Last 10) -join ', '
    throw "tag '$Tag' does not exist in $Url. Latest tags there: $available"
}

function Sync-ToTag {
    param([string]$Url, [string]$Tag, [string]$Dir)

    if (-not (Test-Path (Join-Path $Dir '.git'))) {
        Write-Host "cloning $Url ($Tag) -> $Dir"
        git clone --depth 1 --branch $Tag $Url $Dir
        if ($LASTEXITCODE -ne 0) { throw "git clone failed for $Url ($Tag)" }
        return
    }

    Write-Host "checking $Dir out at $Tag"
    git -C $Dir fetch --depth 1 origin "+refs/tags/${Tag}:refs/tags/${Tag}"
    if ($LASTEXITCODE -ne 0) { throw "git fetch of tag '$Tag' failed in $Dir" }

    # refs/tags/... explicitly: cloning a tag also creates a local branch with the same name, and a bare
    # tag name would then be ambiguous.
    git -C $Dir checkout --quiet --detach "refs/tags/$Tag"
    if ($LASTEXITCODE -ne 0) { throw "git checkout of tag '$Tag' failed in $Dir" }
}

Write-Host "DnsServer          : $DnsServerRepo  tag '$dnsTag'"
Write-Host "TechnitiumLibrary  : $LibraryRepo  tag '$libTag'"

if ($DryRun) {
    Assert-RemoteTag -Url $DnsServerRepo -Tag $dnsTag
    Assert-RemoteTag -Url $LibraryRepo  -Tag $libTag
    Write-Host ''
    Write-Host 'DryRun: both tags exist; nothing was cloned, built or copied.'
    return
}

Assert-RemoteTag -Url $DnsServerRepo -Tag $dnsTag
Assert-RemoteTag -Url $LibraryRepo  -Tag $libTag

Sync-ToTag -Url $DnsServerRepo -Tag $dnsTag -Dir (Join-Path $depsDir 'DnsServer')
Sync-ToTag -Url $LibraryRepo  -Tag $libTag -Dir (Join-Path $depsDir 'TechnitiumLibrary')

$projects = @(
    (Join-Path $depsDir 'TechnitiumLibrary\TechnitiumLibrary\TechnitiumLibrary.csproj'),
    (Join-Path $depsDir 'TechnitiumLibrary\TechnitiumLibrary.Net\TechnitiumLibrary.Net.csproj'),
    (Join-Path $depsDir 'DnsServer\DnsServerCore.ApplicationCommon\DnsServerCore.ApplicationCommon.csproj')
)

foreach ($project in $projects) {
    if (-not (Test-Path $project)) { throw "project not found after checkout: $project" }
    Write-Host "building $project"
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project" }
}

New-Item -ItemType Directory -Path $libsDir -Force | Out-Null

# TechnitiumLibrary projects write their Release assemblies into <repo root>\bin by design.
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
Write-Host "libs/ is ready: DnsServer $dnsTag / TechnitiumLibrary $libTag."
Write-Host 'These DLLs are compile-time only and must NOT be shipped inside the app zip.'

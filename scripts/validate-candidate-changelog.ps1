[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedPackageVersion
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $RepositoryRoot).Path

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
    Fail 'ExpectedVersion must be a stable X.Y.Z version.'
}
if ($ExpectedPackageVersion -ne $ExpectedVersion) {
    Fail "Package version '$ExpectedPackageVersion' does not match candidate version '$ExpectedVersion'."
}

$propsPath = Join-Path $root 'Directory.Build.props'
$props = Get-Content -Raw $propsPath
$versionMatches = [regex]::Matches($props, '<Version>(?<version>[^<]+)</Version>')
if ($versionMatches.Count -ne 1) {
    Fail 'Directory.Build.props must contain exactly one source version.'
}
if ($versionMatches[0].Groups['version'].Value -ne $ExpectedPackageVersion) {
    Fail "Directory.Build.props version '$($versionMatches[0].Groups['version'].Value)' does not match candidate version '$ExpectedPackageVersion'."
}

$changelog = Get-Content -Raw (Join-Path $root 'CHANGELOG.md')
$unreleasedHeadings = [regex]::Matches($changelog, '(?m)^## \[Unreleased\]\s*$')
if ($unreleasedHeadings.Count -ne 1) {
    Fail 'CHANGELOG.md must contain one exact ## [Unreleased] heading for a release candidate.'
}

$firstSection = [regex]::Match($changelog, '(?m)^## (?<heading>[^\r\n]+)')
if (-not $firstSection.Success -or $firstSection.Groups['heading'].Value -cne '[Unreleased]') {
    Fail 'The Unreleased section must be the first changelog section for a release candidate.'
}

$escapedVersion = [regex]::Escape($ExpectedVersion)
if ([regex]::IsMatch($changelog, "(?m)^## \[$escapedVersion\](?:\s|$)")) {
    Fail "CHANGELOG.md must keep candidate version [$ExpectedVersion] under Unreleased until release finalization."
}

Write-Output "Candidate changelog contract passed: candidate=$ExpectedVersion package=$ExpectedPackageVersion state=Unreleased"

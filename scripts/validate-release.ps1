[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedPackageVersion,
    [string]$ExpectedTag,
    [string]$ExpectedCommit,
    [switch]$RequireIcon
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $RepositoryRoot).Path

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

if ($RequireIcon -and -not (Test-Path -LiteralPath (Join-Path $root 'icon.png') -PathType Leaf)) {
    Fail "Release validation requires the founder-owned icon at: $(Join-Path $root 'icon.png')"
}

if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
    Fail "ExpectedVersion must be a stable X.Y.Z version."
}
if ($ExpectedPackageVersion -ne $ExpectedVersion) {
    Fail "Package version '$ExpectedPackageVersion' does not match release version '$ExpectedVersion'."
}

$propsPath = Join-Path $root 'Directory.Build.props'
$props = Get-Content -Raw $propsPath
$versionMatch = [regex]::Match($props, '<Version>(?<version>[^<]+)</Version>')
if (-not $versionMatch.Success) {
    Fail 'Directory.Build.props does not contain a single source version.'
}
if ($versionMatch.Groups['version'].Value -ne $ExpectedPackageVersion) {
    Fail "Directory.Build.props version '$($versionMatch.Groups['version'].Value)' does not match '$ExpectedPackageVersion'."
}

$changelogPath = Join-Path $root 'CHANGELOG.md'
$changelog = Get-Content -Raw $changelogPath
$escapedVersion = [regex]::Escape($ExpectedVersion)
$entryMatch = [regex]::Match($changelog, "(?ms)^## \[$escapedVersion\](?<heading>[^\r\n]*)\r?\n(?<body>.*?)(?=^## |\z)")
if (-not $entryMatch.Success) {
    Fail "CHANGELOG.md has no finalized [$ExpectedVersion] entry."
}

$heading = $entryMatch.Groups['heading'].Value
$body = $entryMatch.Groups['body'].Value
if ($heading -notmatch '\-\s*\d{4}-\d{2}-\d{2}') {
    Fail "CHANGELOG.md entry [$ExpectedVersion] must include an ISO release date."
}
if ($body -match '(?i)\b(planned|unreleased|tbd|not\s+yet\s+published)\b' -or $heading -match '(?i)\b(planned|unreleased|tbd|not\s+yet\s+published)\b') {
    Fail "CHANGELOG.md entry [$ExpectedVersion] is still marked as planned or unpublished."
}

if ($ExpectedTag) {
    if ($ExpectedTag -ne "v$ExpectedVersion") {
        Fail "Tag '$ExpectedTag' does not match v$ExpectedVersion."
    }
}

if ($ExpectedCommit) {
    $actualCommit = (git -C $root rev-parse HEAD).Trim()
    if ($actualCommit -ne $ExpectedCommit) {
        Fail "Current commit '$actualCommit' does not match expected commit '$ExpectedCommit'."
    }
}

Write-Output "Release contract passed: version=$ExpectedVersion package=$ExpectedPackageVersion"

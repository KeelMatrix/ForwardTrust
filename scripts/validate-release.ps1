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

function Read-BigEndianUInt32([byte[]]$Bytes, [int]$Offset) {
    return [uint32]((([uint32]$Bytes[$Offset]) -shl 24) -bor (([uint32]$Bytes[$Offset + 1]) -shl 16) -bor (([uint32]$Bytes[$Offset + 2]) -shl 8) -bor [uint32]$Bytes[$Offset + 3])
}

function Assert-PngProperties([byte[]]$Bytes, [string]$Description) {
    if ($Bytes.Length -gt 200KB) {
        Fail "$Description must be no more than 200 KB (was $($Bytes.Length) bytes)."
    }
    if ($Bytes.Length -lt 33) {
        Fail "$Description is not a complete PNG file."
    }

    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    for ($index = 0; $index -lt $signature.Length; $index++) {
        if ($Bytes[$index] -ne $signature[$index]) {
            Fail "$Description is not a PNG file."
        }
    }

    if ((Read-BigEndianUInt32 $Bytes 8) -ne 13 -or [Text.Encoding]::ASCII.GetString($Bytes, 12, 4) -cne 'IHDR') {
        Fail "$Description does not have a valid PNG IHDR header."
    }

    $width = Read-BigEndianUInt32 $Bytes 16
    $height = Read-BigEndianUInt32 $Bytes 20
    if ($width -ne 512 -or $height -ne 512) {
        Fail "$Description must be exactly 512x512 pixels (was ${width}x${height})."
    }
}

$rootIcon = Join-Path $root 'icon.png'
if ($RequireIcon -and -not (Test-Path -LiteralPath $rootIcon -PathType Leaf)) {
    Fail "Release validation requires the founder-owned icon at: $rootIcon"
}
if ($RequireIcon) {
    Assert-PngProperties ([IO.File]::ReadAllBytes($rootIcon)) 'Repository-root icon.png'
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

if ($ExpectedVersion -eq '0.1.0') {
    $categoryMatches = [regex]::Matches($body, '(?m)^###\s+(.+?)\s*$')
    $categories = @($categoryMatches | ForEach-Object { $_.Groups[1].Value.Trim() })
    if ($categories.Count -ne 1 -or $categories[0] -cne 'Added') {
        Fail "First-release CHANGELOG.md entry [$ExpectedVersion] must contain exactly one category: ### Added."
    }

    $remediationMarkers = [ordered]@{
        'this removes' = '\bthis\s+removes\b'
        'this fixes' = '\bthis\s+fixes\b'
        'now' = '\bnow\b'
        'no longer' = '\bno\s+longer\b'
        'previously' = '\bpreviously\b'
        'formerly' = '\bformerly\b'
        'used to' = '\bused\s+to\b'
        'fixed' = '\bfixed\b'
        'fixes' = '\bfixes\b'
        'corrected' = '\bcorrected\b'
        'resolved' = '\bresolved\b'
        'addressed' = '\baddressed\b'
        'changed from' = '\bchanged\s+from\b'
    }
    foreach ($marker in $remediationMarkers.Keys) {
        if ($body -match "(?i)$($remediationMarkers[$marker])") {
            Fail "First-release CHANGELOG.md entry [$ExpectedVersion] contains prohibited remediation wording: '$marker'."
        }
    }
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

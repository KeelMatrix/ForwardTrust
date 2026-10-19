[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedPackageVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedTag,
    [Parameter(Mandatory = $true)][string]$ExpectedCommit,
    [Parameter(Mandatory = $true)][string]$ExpectedReleaseDate,
    [switch]$RequireTag,
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
    Fail "Release validation requires the package icon at: $rootIcon"
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

if ($ExpectedTag -ne "v$ExpectedVersion") {
    Fail "Tag '$ExpectedTag' does not match v$ExpectedVersion."
}

if ($ExpectedCommit -notmatch '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') {
    Fail 'ExpectedCommit must be an exact Git commit SHA.'
}

if ($ExpectedReleaseDate -notmatch '^\d{4}-\d{2}-\d{2}$') {
    Fail 'ExpectedReleaseDate must be an ISO date in YYYY-MM-DD form.'
}

$parsedReleaseDate = [datetime]::MinValue
if (-not [datetime]::TryParseExact(
        $ExpectedReleaseDate,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$parsedReleaseDate)) {
    Fail "ExpectedReleaseDate '$ExpectedReleaseDate' is not a valid calendar date."
}

$workingTree = (git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0 -or $workingTree) {
    Fail 'Release validation requires a clean working tree.'
}

$actualCommit = (git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $ExpectedCommit) {
    Fail "Current commit '$actualCommit' does not match expected finalization commit '$ExpectedCommit'."
}

$actualCommitDate = (git -C $root show -s --format=%cs $ExpectedCommit).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommitDate -ne $ExpectedReleaseDate) {
    Fail "ExpectedReleaseDate '$ExpectedReleaseDate' does not match commit $ExpectedCommit date '$actualCommitDate'."
}

$tagReference = "refs/tags/$ExpectedTag"
$tagExists = $false
git -C $root show-ref --verify --quiet $tagReference
$tagExists = $LASTEXITCODE -eq 0
if ($RequireTag) {
    if (-not $tagExists) {
        Fail "Tag '$ExpectedTag' does not exist."
    }
    $taggedCommit = (git -C $root rev-list -n 1 $ExpectedTag).Trim()
    if ($LASTEXITCODE -ne 0 -or $taggedCommit -ne $ExpectedCommit) {
        Fail "Tag '$ExpectedTag' must exist and point to exact commit $ExpectedCommit."
    }
} else {
    if ($tagExists) {
        Fail "Tag '$ExpectedTag' already exists; release preparation requires an unused tag."
    }
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
$unreleasedHeadings = [regex]::Matches($changelog, '(?m)^## \[Unreleased\]\s*$')
if ($unreleasedHeadings.Count -ne 1) {
    Fail 'CHANGELOG.md must contain exactly one ## [Unreleased] section.'
}
$unreleasedMatch = [regex]::Match($changelog, "(?ms)^## \[Unreleased\]\s*\r?\n(?<body>.*?)(?=^## |\z)")
if (-not $unreleasedMatch.Success) {
    Fail 'CHANGELOG.md must retain one ## [Unreleased] section.'
}
if (-not [string]::IsNullOrWhiteSpace($unreleasedMatch.Groups['body'].Value)) {
    Fail 'CHANGELOG.md has changes stranded under Unreleased; move release notes into the finalized version entry.'
}

$escapedVersion = [regex]::Escape($ExpectedVersion)
$releaseHeadings = [regex]::Matches($changelog, "(?m)^## \[$escapedVersion\](?:\s|$)")
if ($releaseHeadings.Count -ne 1) {
    Fail "CHANGELOG.md must contain exactly one finalized [$ExpectedVersion] entry."
}
$entryMatch = [regex]::Match($changelog, "(?ms)^## \[$escapedVersion\](?<heading>[^\r\n]*)\r?\n(?<body>.*?)(?=^## |\z)")
if (-not $entryMatch.Success) {
    Fail "CHANGELOG.md has no finalized [$ExpectedVersion] entry."
}

$heading = $entryMatch.Groups['heading'].Value
$body = $entryMatch.Groups['body'].Value
if ($heading -notmatch '^\s*-\s*(?<date>\d{4}-\d{2}-\d{2})\s*$') {
    Fail "CHANGELOG.md entry [$ExpectedVersion] must include an ISO release date."
}
$entryDate = $Matches['date']
if ($entryDate -ne $ExpectedReleaseDate) {
    Fail "CHANGELOG.md release date '$entryDate' does not match the date '$ExpectedReleaseDate' of exact commit $ExpectedCommit."
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

Write-Output "Release contract passed: version=$ExpectedVersion package=$ExpectedPackageVersion tag=$ExpectedTag commit=$ExpectedCommit date=$ExpectedReleaseDate requireTag=$RequireTag"

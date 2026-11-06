[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedPackageVersion,
    [ValidateSet('Auto', 'Unreleased', 'Finalized', 'Tagged')][string]$State = 'Auto',
    [string]$ExpectedTag,
    [string]$ExpectedCommit,
    [string]$ExpectedReleaseDate,
    [switch]$RequireIcon
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$changelog = Get-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Raw
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'ExpectedVersion must be a stable X.Y.Z version.' }
$headings = [regex]::Matches($changelog, '(?m)^## (?<heading>[^\r\n]+)')
if ($headings.Count -eq 0 -or $headings[0].Groups['heading'].Value -cne '[Unreleased]') {
    throw 'Changelog must begin with an Unreleased section.'
}
$seenVersions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$previousVersion = $null
foreach ($heading in $headings | Select-Object -Skip 1) {
    $entry = [regex]::Match($heading.Groups['heading'].Value, '^\[(?<version>\d+\.\d+\.\d+)\] - (?<date>\d{4}-\d{2}-\d{2})$')
    if (-not $entry.Success) { throw 'Each finalized changelog heading must contain a stable version and ISO date.' }
    $version = $entry.Groups['version'].Value
    $date = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($entry.Groups['date'].Value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) {
        throw 'Changelog contains an invalid calendar date.'
    }
    if (-not $seenVersions.Add($version) -or [version]$version -gt [version]$ExpectedVersion -or ($null -ne $previousVersion -and [version]$version -ge $previousVersion)) {
        throw 'Changelog versions must be unique, descending, and no newer than the package version.'
    }
    $previousVersion = [version]$version
}
if ($State -eq 'Auto') {
    if ($seenVersions.Contains($ExpectedVersion)) {
        git -C $root show-ref --verify --quiet "refs/tags/v$ExpectedVersion"
        if ($LASTEXITCODE -notin @(0, 1)) { throw 'Could not determine whether the release tag exists.' }
        $State = if ($LASTEXITCODE -eq 0) { 'Tagged' } else { 'Finalized' }
    } else {
        $State = 'Unreleased'
    }
}

if ($State -eq 'Unreleased') {
    # A candidate for an already tagged version must not fall back to Unreleased.
    if (Test-Path -LiteralPath (Join-Path $root '.git')) {
        git -C $root show-ref --verify --quiet "refs/tags/v$ExpectedVersion"
        if ($LASTEXITCODE -eq 0) { throw 'An Unreleased candidate cannot use an existing release tag.' }
        if ($LASTEXITCODE -ne 1) { throw 'Could not check the candidate tag.' }
    }
    Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'validate-candidate-changelog.ps1') -RepositoryRoot $root -ExpectedVersion $ExpectedVersion -ExpectedPackageVersion $ExpectedPackageVersion
} else {
    if (-not $ExpectedTag) { $ExpectedTag = "v$ExpectedVersion" }
    if (-not $ExpectedCommit) {
        $ExpectedCommit = git -C $root rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the candidate commit.' }
        $ExpectedCommit = $ExpectedCommit.Trim()
    }
    if (-not $ExpectedReleaseDate) {
        $ExpectedReleaseDate = git -C $root show -s --format=%cs HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the candidate commit date.' }
        $ExpectedReleaseDate = $ExpectedReleaseDate.Trim()
    }
    $arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'validate-release.ps1'),
        '-RepositoryRoot', $root, '-ExpectedVersion', $ExpectedVersion, '-ExpectedPackageVersion', $ExpectedPackageVersion,
        '-ExpectedTag', $ExpectedTag, '-ExpectedCommit', $ExpectedCommit, '-ExpectedReleaseDate', $ExpectedReleaseDate)
    if ($State -eq 'Tagged') { $arguments += '-RequireTag' }
    if ($RequireIcon) { $arguments += '-RequireIcon' }
    Invoke-NestedPwsh @arguments
}
if ($LASTEXITCODE -ne 0) { throw "Changelog validation failed for state $State." }
Write-Output "Changelog lifecycle passed: $State"

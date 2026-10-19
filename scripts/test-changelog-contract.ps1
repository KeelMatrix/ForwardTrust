[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedPackageVersion
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path $RepositoryRoot).Path
$candidateValidator = Join-Path $root 'scripts/validate-candidate-changelog.ps1'
$releaseValidator = Join-Path $root 'scripts/validate-release.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("forwardtrust-changelog-contract-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

function Assert-Result([string]$Label, [bool]$ExpectedPass) {
    $passed = $LASTEXITCODE -eq 0
    if ($passed -ne $ExpectedPass) {
        throw "Unexpected changelog contract result for '$Label'. Expected pass=$ExpectedPass, actual pass=$passed."
    }
}

function Invoke-CandidateContract([string]$FixtureRoot, [string]$Version, [bool]$ShouldPass, [string]$Label) {
    Invoke-NestedPwsh -NoProfile -File $candidateValidator -RepositoryRoot $FixtureRoot -ExpectedVersion $Version -ExpectedPackageVersion $Version
    Assert-Result $Label $ShouldPass
}

function Invoke-ReleaseContract(
    [string]$FixtureRoot,
    [string]$Version,
    [string]$PackageVersion,
    [string]$Tag,
    [string]$Commit,
    [string]$ReleaseDate,
    [bool]$RequireTag,
    [bool]$ShouldPass,
    [string]$Label) {
    $arguments = @(
        '-NoProfile',
        '-File', $releaseValidator,
        '-RepositoryRoot', $FixtureRoot,
        '-ExpectedVersion', $Version,
        '-ExpectedPackageVersion', $PackageVersion,
        '-ExpectedTag', $Tag,
        '-ExpectedCommit', $Commit,
        '-ExpectedReleaseDate', $ReleaseDate
    )
    if ($RequireTag) {
        $arguments += '-RequireTag'
    }
    Invoke-NestedPwsh @arguments
    Assert-Result $Label $ShouldPass
}

function Write-Fixture([string]$FixtureRoot, [string]$PackageVersion, [string]$Changelog) {
    New-Item -ItemType Directory -Path $FixtureRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $FixtureRoot 'Directory.Build.props') -Value "<Project><PropertyGroup><Version>$PackageVersion</Version></PropertyGroup></Project>" -Encoding utf8
    Set-Content -LiteralPath (Join-Path $FixtureRoot 'CHANGELOG.md') -Value $Changelog -Encoding utf8
}

function Convert-FixtureLineEndings([string]$Text, [string]$LineEnding) {
    $normalized = [regex]::Replace($Text, "`r`n|`r|`n", "`n")
    if ($LineEnding -eq 'CRLF') {
        return $normalized.Replace("`n", "`r`n")
    }

    return $normalized
}

function Replace-FixtureText(
    [string]$Text,
    [string]$OldText,
    [string]$NewText,
    [string]$Label,
    [string]$LineEnding = 'LF') {
    $normalizedText = [regex]::Replace($Text, "`r`n|`r|`n", "`n")
    $normalizedOldText = [regex]::Replace($OldText, "`r`n|`r|`n", "`n")
    $normalizedNewText = [regex]::Replace($NewText, "`r`n|`r|`n", "`n")
    $matchCount = [regex]::Matches($normalizedText, [regex]::Escape($normalizedOldText)).Count
    if ($matchCount -ne 1) {
        throw "Expected one '$Label' fixture mutation target, found $matchCount."
    }

    $mutatedText = $normalizedText.Replace($normalizedOldText, $normalizedNewText)
    if ($mutatedText -ceq $normalizedText) {
        throw "The '$Label' fixture mutation did not change its source text."
    }

    return (Convert-FixtureLineEndings $mutatedText $LineEnding)
}

function Get-FixtureCommit([string]$FixtureRoot) {
    $commit = (git -C $FixtureRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not read Git fixture commit in '$FixtureRoot'." }
    $releaseDate = (git -C $FixtureRoot show -s --format=%cs $commit).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not read Git fixture commit date in '$FixtureRoot'." }
    return [pscustomobject]@{ Commit = $commit; Date = $releaseDate }
}

function Get-FixtureReleaseVersion([string]$Changelog) {
    $match = [regex]::Match($Changelog, '(?m)^## \[(?<version>\d+\.\d+\.\d+)\] - \d{4}-\d{2}-\d{2}(?=\r?$)')
    if (-not $match.Success) { throw 'Release fixture has no finalized changelog heading.' }
    return $match.Groups['version'].Value
}

function Set-FixtureReleaseDate([string]$FixtureRoot, [string]$Version, [string]$ReleaseDate) {
    $changelogPath = Join-Path $FixtureRoot 'CHANGELOG.md'
    $changelog = [System.IO.File]::ReadAllText($changelogPath)
    $escapedVersion = [regex]::Escape($Version)
    $pattern = "(?m)^(## \[$escapedVersion\] - )\d{4}-\d{2}-\d{2}(?=\r?$)"
    $matches = [regex]::Matches($changelog, $pattern)
    if ($matches.Count -eq 0) { throw "Release fixture has no finalized heading for version '$Version'." }
    $updated = [regex]::Replace($changelog, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{
        param($match)
        return $match.Groups[1].Value + $ReleaseDate
    })
    if ($updated -ceq $changelog) { return $false }
    [System.IO.File]::WriteAllText($changelogPath, $updated, [System.Text.UTF8Encoding]::new($false))
    return $true
}

function Amend-FixtureCommit([string]$FixtureRoot) {
    & git -C $FixtureRoot add CHANGELOG.md
    if ($LASTEXITCODE -ne 0) { throw "Could not stage changelog fixture in '$FixtureRoot'." }
    & git -C $FixtureRoot -c user.name='ForwardTrust Fixture' -c user.email='fixture@example.invalid' commit --quiet --amend --no-edit
    if ($LASTEXITCODE -ne 0) { throw "Could not amend changelog fixture in '$FixtureRoot'." }
}

function Sync-FixtureReleaseDate([string]$FixtureRoot) {
    $changelog = [System.IO.File]::ReadAllText((Join-Path $FixtureRoot 'CHANGELOG.md'))
    $version = Get-FixtureReleaseVersion $changelog
    $escapedVersion = [regex]::Escape($version)
    $pattern = "(?m)^## \[$escapedVersion\] - (?<date>\d{4}-\d{2}-\d{2})(?=\r?$)"

    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $commit = Get-FixtureCommit $FixtureRoot
        $changelog = [System.IO.File]::ReadAllText((Join-Path $FixtureRoot 'CHANGELOG.md'))
        $headings = [regex]::Matches($changelog, $pattern)
        if ($headings.Count -eq 0) { throw "Release fixture has no finalized heading for version '$version'." }
        $datesMatch = $true
        foreach ($heading in $headings) {
            if ($heading.Groups['date'].Value -cne $commit.Date) { $datesMatch = $false; break }
        }
        if ($datesMatch) {
            return [pscustomobject]@{ Commit = $commit.Commit; Date = $commit.Date; Version = $version }
        }
        [void](Set-FixtureReleaseDate $FixtureRoot $version $commit.Date)
        Amend-FixtureCommit $FixtureRoot
    }

    throw "Release fixture '$FixtureRoot' did not stabilize its changelog date against its commit date."
}

function New-ReleaseFixture(
    [string]$Name,
    [string]$PackageVersion,
    [string]$Changelog,
    [string]$Tag = '',
    [bool]$AddTag = $false) {
    $fixtureRoot = Join-Path $tempRoot $Name
    Write-Fixture $fixtureRoot $PackageVersion $Changelog
    & git -C $fixtureRoot init --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize Git fixture '$Name'." }

    & git -C $fixtureRoot add Directory.Build.props CHANGELOG.md
    if ($LASTEXITCODE -ne 0) { throw "Could not stage Git fixture '$Name'." }
    & git -C $fixtureRoot -c user.name='ForwardTrust Fixture' -c user.email='fixture@example.invalid' commit --quiet -m 'release fixture'
    if ($LASTEXITCODE -ne 0) { throw "Could not commit Git fixture '$Name'." }

    $fixtureCommit = Sync-FixtureReleaseDate $fixtureRoot
    if ($AddTag) {
        & git -C $fixtureRoot tag $Tag $fixtureCommit.Commit
        if ($LASTEXITCODE -ne 0) { throw "Could not create Git fixture '$Name' tag." }
    }

    return [pscustomobject]@{
        Root = $fixtureRoot
        Commit = $fixtureCommit.Commit
        Date = $fixtureCommit.Date
    }
}

function Add-SecondCommit([pscustomobject]$Fixture) {
    Set-Content -LiteralPath (Join-Path $Fixture.Root 'fixture.txt') -Value 'second commit' -Encoding utf8
    & git -C $Fixture.Root add fixture.txt
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the second commit fixture.' }
    & git -C $Fixture.Root -c user.name='ForwardTrust Fixture' -c user.email='fixture@example.invalid' commit --quiet -m 'second fixture commit'
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the second commit fixture.' }
    $fixtureCommit = Sync-FixtureReleaseDate $Fixture.Root
    $Fixture.Commit = $fixtureCommit.Commit
    $Fixture.Date = $fixtureCommit.Date
}

try {
    Invoke-CandidateContract $root $ExpectedVersion $true 'repository pre-frontier candidate'

    $firstCandidate = Join-Path $tempRoot 'first-candidate'
    Write-Fixture $firstCandidate '0.1.0' @'
# Changelog

## [Unreleased]

### Added

- Initial consumer-facing package capabilities.
'@
    Invoke-CandidateContract $firstCandidate '0.1.0' $true 'first-release Unreleased candidate'

    $subsequentCandidate = Join-Path $tempRoot 'subsequent-candidate'
    Write-Fixture $subsequentCandidate '0.2.0' @'
# Changelog

## [Unreleased]

### Added

- A future consumer-facing capability.

## [0.1.0] - 2026-10-01

### Added

- The initial package.
'@
    Invoke-CandidateContract $subsequentCandidate '0.2.0' $true 'subsequent-release Unreleased candidate'

    $prematurelyFinalized = Join-Path $tempRoot 'prematurely-finalized-candidate'
    Write-Fixture $prematurelyFinalized '0.1.0' @'
# Changelog

## [Unreleased]

## [0.1.0] - 2026-10-18

### Added

- Initial package.
'@
    Invoke-CandidateContract $prematurelyFinalized '0.1.0' $false 'candidate with a finalized target version'

    $duplicateUnreleasedCandidate = Join-Path $tempRoot 'duplicate-unreleased-candidate'
    Write-Fixture $duplicateUnreleasedCandidate '0.1.0' @'
# Changelog

## [Unreleased]

## [Unreleased]
'@
    Invoke-CandidateContract $duplicateUnreleasedCandidate '0.1.0' $false 'candidate with duplicate Unreleased headings'

    $firstReleaseText = @'
# Changelog

## [Unreleased]

## [0.1.0] - 2026-10-18

### Added

- Executable forwarded-header trust scenarios for ASP.NET Core applications.
'@
    $firstRelease = New-ReleaseFixture 'first-release' '0.1.0' $firstReleaseText
    Invoke-ReleaseContract $firstRelease.Root '0.1.0' '0.1.0' 'v0.1.0' $firstRelease.Commit $firstRelease.Date $false $true 'first-release finalization before tag'
    & git -C $firstRelease.Root tag 'v0.1.0' $firstRelease.Commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not create first-release tag fixture.' }
    Invoke-ReleaseContract $firstRelease.Root '0.1.0' '0.1.0' 'v0.1.0' $firstRelease.Commit $firstRelease.Date $true $true 'first-release exact tag SHA'
    Invoke-ReleaseContract $firstRelease.Root '0.1.0' '0.1.0' 'v0.1.0' $firstRelease.Commit $firstRelease.Date $false $false 'already-created tag on pre-tag path'

    foreach ($lineEnding in @('LF', 'CRLF')) {
        $lineEndingFirstReleaseText = Convert-FixtureLineEndings $firstReleaseText $lineEnding
        $wrongCategoryMutation = @{
            Text = $lineEndingFirstReleaseText
            OldText = "### Added`n`n- Executable forwarded-header trust scenarios for ASP.NET Core applications."
            NewText = "### Added`n`n- Executable forwarded-header trust scenarios for ASP.NET Core applications.`n`n### Fixed`n`n- A remediation note."
            Label = "first-release wrong-category ($lineEnding)"
            LineEnding = $lineEnding
        }
        $wrongCategoryText = Replace-FixtureText @wrongCategoryMutation
        $wrongCategory = New-ReleaseFixture "first-release-wrong-category-$lineEnding" '0.1.0' $wrongCategoryText
        Invoke-ReleaseContract $wrongCategory.Root '0.1.0' '0.1.0' 'v0.1.0' $wrongCategory.Commit $wrongCategory.Date $false $false "first release permits only Added category ($lineEnding)"
    }

    $remediationMarkers = @(
        'this removes',
        'this fixes',
        'now',
        'no longer',
        'previously',
        'formerly',
        'used to',
        'fixed',
        'fixes',
        'corrected',
        'resolved',
        'addressed',
        'changed from'
    )
    for ($index = 0; $index -lt $remediationMarkers.Count; $index++) {
        $markerMutation = @{
            Text = $firstReleaseText
            OldText = 'Executable forwarded-header trust scenarios for ASP.NET Core applications.'
            NewText = "The API $($remediationMarkers[$index]) the earlier behavior."
            Label = "first-release remediation marker '$($remediationMarkers[$index])'"
        }
        $markerText = Replace-FixtureText @markerMutation
        $markerFixture = New-ReleaseFixture "first-release-remediation-$index" '0.1.0' $markerText
        Invoke-ReleaseContract $markerFixture.Root '0.1.0' '0.1.0' 'v0.1.0' $markerFixture.Commit $markerFixture.Date $false $false "first-release rejects remediation marker '$($remediationMarkers[$index])'"
    }

    $duplicateReleaseText = $firstReleaseText + @'

## [0.1.0] - 2026-10-18

### Added

- Duplicate release entry.
'@
    $duplicateRelease = New-ReleaseFixture 'duplicate-finalized-version' '0.1.0' $duplicateReleaseText
    Invoke-ReleaseContract $duplicateRelease.Root '0.1.0' '0.1.0' 'v0.1.0' $duplicateRelease.Commit $duplicateRelease.Date $false $false 'release rejects duplicate target version headings'

    $duplicateUnreleasedMutation = @{
        Text = $firstReleaseText
        OldText = '## [Unreleased]'
        NewText = "## [Unreleased]`n`n## [Unreleased]"
        Label = 'duplicate Unreleased heading'
    }
    $duplicateUnreleasedText = Replace-FixtureText @duplicateUnreleasedMutation
    $duplicateUnreleasedRelease = New-ReleaseFixture 'duplicate-unreleased-release' '0.1.0' $duplicateUnreleasedText
    Invoke-ReleaseContract $duplicateUnreleasedRelease.Root '0.1.0' '0.1.0' 'v0.1.0' $duplicateUnreleasedRelease.Commit $duplicateUnreleasedRelease.Date $false $false 'release rejects duplicate Unreleased headings'

    $subsequentReleaseText = @'
# Changelog

## [Unreleased]

## [0.2.0] - 2026-10-18

### Added

- A new consumer-facing capability.

## [0.1.0] - 2026-10-01

### Added

- The initial package.
'@
    $subsequentRelease = New-ReleaseFixture 'subsequent-release' '0.2.0' $subsequentReleaseText
    Invoke-ReleaseContract $subsequentRelease.Root '0.2.0' '0.2.0' 'v0.2.0' $subsequentRelease.Commit $subsequentRelease.Date $false $true 'subsequent-release finalization before tag'
    & git -C $subsequentRelease.Root tag 'v0.2.0' $subsequentRelease.Commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not create subsequent-release tag fixture.' }
    Invoke-ReleaseContract $subsequentRelease.Root '0.2.0' '0.2.0' 'v0.2.0' $subsequentRelease.Commit $subsequentRelease.Date $true $true 'subsequent-release exact tag SHA'

    $untagged = New-ReleaseFixture 'untagged-release' '0.1.0' $firstReleaseText
    Invoke-ReleaseContract $untagged.Root '0.1.0' '0.1.0' 'v0.1.0' $untagged.Commit $untagged.Date $true $false 'tag mode rejects an untagged finalization candidate'

    $wrongDate = New-ReleaseFixture 'wrong-date-release' '0.1.0' $firstReleaseText
    $wrongChangelogDate = ([datetime]::ParseExact(
        $wrongDate.Date,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture
    )).AddDays(-1).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    if (-not (Set-FixtureReleaseDate $wrongDate.Root '0.1.0' $wrongChangelogDate)) {
        throw 'Wrong-date fixture mutation did not change the changelog date.'
    }
    Amend-FixtureCommit $wrongDate.Root
    $wrongDateCommit = Get-FixtureCommit $wrongDate.Root
    if ($wrongDateCommit.Date -ceq $wrongChangelogDate) {
        $wrongChangelogDate = ([datetime]::ParseExact(
            $wrongDateCommit.Date,
            'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture
        )).AddDays(-1).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        if (-not (Set-FixtureReleaseDate $wrongDate.Root '0.1.0' $wrongChangelogDate)) {
            throw 'Wrong-date fixture retry did not change the changelog date.'
        }
        Amend-FixtureCommit $wrongDate.Root
        $wrongDateCommit = Get-FixtureCommit $wrongDate.Root
    }
    if ($wrongDateCommit.Date -ceq $wrongChangelogDate) { throw 'Wrong-date fixture unexpectedly matches its amended commit date.' }
    $wrongDate.Commit = $wrongDateCommit.Commit
    $wrongDate.Date = $wrongDateCommit.Date
    Invoke-ReleaseContract $wrongDate.Root '0.1.0' '0.1.0' 'v0.1.0' $wrongDate.Commit $wrongDate.Date $false $false 'changelog date must match exact commit date'

    $wrongPackage = New-ReleaseFixture 'wrong-package-version' '0.2.0' $firstReleaseText
    Invoke-ReleaseContract $wrongPackage.Root '0.1.0' '0.1.0' 'v0.1.0' $wrongPackage.Commit $wrongPackage.Date $false $false 'package and changelog version mismatch'

    $wrongTag = New-ReleaseFixture 'wrong-tag-version' '0.1.0' $firstReleaseText 'v0.1.1' $true
    Invoke-ReleaseContract $wrongTag.Root '0.1.0' '0.1.0' 'v0.1.1' $wrongTag.Commit $wrongTag.Date $true $false 'tag version mismatch'

    $wrongTagCommit = New-ReleaseFixture 'wrong-tag-commit' '0.1.0' $firstReleaseText 'v0.1.0' $true
    Add-SecondCommit $wrongTagCommit
    Invoke-ReleaseContract $wrongTagCommit.Root '0.1.0' '0.1.0' 'v0.1.0' $wrongTagCommit.Commit $wrongTagCommit.Date $true $false 'tag must point to exact finalization SHA'

    $strandedText = @'
# Changelog

## [Unreleased]

### Added

- A capability stranded outside the finalized release entry.

## [0.1.0] - 2026-10-18

### Added

- Initial package.
'@
    $stranded = New-ReleaseFixture 'stranded-unreleased-changes' '0.1.0' $strandedText
    Invoke-ReleaseContract $stranded.Root '0.1.0' '0.1.0' 'v0.1.0' $stranded.Commit $stranded.Date $false $false 'release rejects changes stranded under Unreleased'

    Write-Output 'Changelog contract tests passed: candidate/finalized states, heading uniqueness, first-release category and remediation rules, tag/SHA behavior, version/date mismatches, and stranded Unreleased changes.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

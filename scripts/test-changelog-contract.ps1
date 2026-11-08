[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedPackageVersion,
    [switch]$FixturesOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path $RepositoryRoot).Path
$candidateValidator = Join-Path $PSScriptRoot 'validate-candidate-changelog.ps1'
$releaseValidator = Join-Path $PSScriptRoot 'validate-release.ps1'
$packageGate = Join-Path $PSScriptRoot 'package-gate.ps1'
$scratchRoot = if ($env:TMPDIR) { $env:TMPDIR } else { [IO.Path]::GetTempPath() }
$tempRoot = Join-Path $scratchRoot ("forwardtrust-changelog-contract-" + [guid]::NewGuid().ToString('N'))
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
    Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $FixtureRoot -ExpectedVersion $Version -ExpectedPackageVersion $Version -ChangelogState Unreleased
    Assert-Result "package gate: $Label" $ShouldPass
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
    $state = if ($RequireTag) { 'Tagged' } else { 'Finalized' }
    Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $FixtureRoot -ExpectedVersion $Version -ExpectedPackageVersion $PackageVersion -ChangelogState $state -ExpectedTag $Tag -ExpectedCommit $Commit -ExpectedReleaseDate $ReleaseDate
    Assert-Result "package gate: $Label" $ShouldPass
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

function Invoke-Lifecycle([string]$FixtureRoot, [string]$Version, [string]$ExpectedState, [bool]$ShouldPass, [string]$Label, [string]$State = 'Auto') {
    $output = Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $FixtureRoot -ExpectedVersion $Version -ExpectedPackageVersion $Version -ChangelogState $State 2>&1
    Assert-Result $Label $ShouldPass
    if ($ShouldPass -and ($output -join "`n") -notmatch "Changelog lifecycle passed: $ExpectedState") {
        throw "Wrong lifecycle selected for '$Label': $output"
    }
    Write-Output "Lifecycle fixture passed: $Label"
}

function Invoke-WorkflowLifecycle([string]$FixtureRoot, [string]$Version, [bool]$Release, [bool]$ShouldPass, [string]$Label) {
    # Execute the actual workflow command with a fixture root/version, without build or publication.
    $quotedRoot = $FixtureRoot.Replace("'", "''")
    if ($Release) {
        $workflow = Get-Content -LiteralPath (Join-Path $root '.github/workflows/release.yml') -Raw
        $match = [regex]::Match($workflow, '(?ms)- name: Validate release tag and changelog\r?\n\s+shell: pwsh\r?\n\s+run: \|\r?\n(?<command>.*?)(?=^      - name:)')
        $quotedValidator = (Join-Path $PSScriptRoot 'validate-changelog.ps1').Replace("'", "''")
        $command = $match.Groups['command'].Value.Replace('./scripts/validate-changelog.ps1', "& '$quotedValidator' -RepositoryRoot '$quotedRoot'").Replace(' -RequireIcon', '')
    } else {
        $workflow = Get-Content -LiteralPath (Join-Path $root '.github/workflows/ci.yml') -Raw
        $match = [regex]::Match($workflow, '(?m)- name: Run package and repository hygiene gates\r?\n\s+shell: pwsh\r?\n\s+run: (?<command>[^\r\n]+)')
        $quotedGate = $packageGate.Replace("'", "''")
        $command = $match.Groups['command'].Value.Replace('./scripts/package-gate.ps1', "& '$quotedGate' -Stage Changelog -RepositoryRoot '$quotedRoot' -ExpectedVersion $Version -ExpectedPackageVersion $Version")
    }
    if (-not $match.Success -or $command -ceq $match.Groups['command'].Value) { throw 'Could not locate workflow lifecycle entry point.' }
    if ($workflow -notmatch 'fetch-depth: 0') { throw 'Workflow must fetch full history and tags for lifecycle validation.' }
    $output = Invoke-NestedPwsh -NoProfile -Command ("`$ErrorActionPreference = 'Stop'; Set-Location -LiteralPath '$quotedRoot'; `$env:GITHUB_REF_NAME = 'v$Version'; " + $command) 2>&1
    if (($LASTEXITCODE -eq 0) -ne $ShouldPass) { Write-Output $output }
    Assert-Result $Label $ShouldPass
    Write-Output "Workflow lifecycle fixture passed: $Label"
}

try {
    if (-not $FixturesOnly) {
        Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $root -ExpectedVersion $ExpectedVersion -ExpectedPackageVersion $ExpectedPackageVersion
        Assert-Result 'repository lifecycle gate' $true
    }

    $firstCandidate = Join-Path $tempRoot 'first-candidate'
    Write-Fixture $firstCandidate '0.1.0' @'
# Changelog

## [Unreleased]

### Added

- Initial consumer-facing package capabilities.
'@
    Invoke-CandidateContract $firstCandidate '0.1.0' $true 'first-release Unreleased candidate'
    Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $firstCandidate -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
    Assert-Result 'automatic unreleased package gate' $true

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
    Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $firstRelease.Root -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
    Assert-Result 'automatic finalized package gate' $true
    & git -C $firstRelease.Root tag 'v0.1.0' $firstRelease.Commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not create first-release tag fixture.' }
    Invoke-ReleaseContract $firstRelease.Root '0.1.0' '0.1.0' 'v0.1.0' $firstRelease.Commit $firstRelease.Date $true $true 'first-release exact tag SHA'
    Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $firstRelease.Root -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
    Assert-Result 'automatic exact-tag package gate' $true
    Invoke-WorkflowLifecycle $firstRelease.Root '0.1.0' $true $true 'release workflow exact tagged commit'
    Invoke-ReleaseContract $firstRelease.Root '0.1.0-preview' '0.1.0-preview' 'v0.1.0-preview' $firstRelease.Commit $firstRelease.Date $true $false 'invalid stable version'
    Invoke-ReleaseContract $firstRelease.Root '0.1.0' '0.1.0' 'v0.1.0' 'not-a-sha' $firstRelease.Date $true $false 'invalid commit syntax'
    Invoke-ReleaseContract $firstRelease.Root '0.1.0' '0.1.0' 'v0.1.0' ('0' * 40) $firstRelease.Date $true $false 'wrong exact commit'
    Invoke-ReleaseContract $firstRelease.Root '0.1.0' '0.1.0' 'v0.1.0' $firstRelease.Commit '2026-02-30' $true $false 'invalid calendar date'
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

    foreach ($version in @('0.1.0', '0.2.0')) {
        foreach ($lineEnding in @('LF', 'CRLF')) {
            $text = if ($version -eq '0.1.0') { $firstReleaseText } else { $subsequentReleaseText }
            $development = New-ReleaseFixture "development-$version-$lineEnding" $version $text "v$version" $true
            Add-SecondCommit $development
            Invoke-Lifecycle $development.Root $version 'Development' $true "maintenance without new notes ($version/$lineEnding)"
            $publishedText = Get-Content -LiteralPath (Join-Path $development.Root 'CHANGELOG.md') -Raw
            $notes = $publishedText.Replace('## [Unreleased]', "## [Unreleased]`n`n### Fixed`n`n- Source URLs preserve repository paths.")
            Write-Fixture $development.Root $version (Convert-FixtureLineEndings $notes $lineEnding)
            Amend-FixtureCommit $development.Root
            Invoke-Lifecycle $development.Root $version 'Development' $true "maintenance with old tag ($version/$lineEnding)"
            Invoke-Lifecycle $development.Root $version 'Development' $true "explicit development ($version/$lineEnding)" 'Development'
            Invoke-Lifecycle $development.Root $version '' $false "maintenance cannot prepare used release ($version/$lineEnding)" 'Finalized'
            Invoke-Lifecycle $development.Root $version '' $false "maintenance cannot publish used release ($version/$lineEnding)" 'Tagged'
            Invoke-WorkflowLifecycle $development.Root $version $false $true "ordinary CI development ($version/$lineEnding)"
            Invoke-WorkflowLifecycle $development.Root $version $true $false "release CI rejects maintenance ($version/$lineEnding)"
            Write-Fixture $development.Root $version (Convert-FixtureLineEndings ($notes.Replace('### Added', '### Changed')) $lineEnding)
            Invoke-Lifecycle $development.Root $version '' $false "development preserves all published notes ($version/$lineEnding)"
            Write-Fixture $development.Root '9.9.9' $notes
            Invoke-Lifecycle $development.Root $version '' $false "development source version mismatch ($version/$lineEnding)"
        }
    }

    $plannedText = $firstReleaseText.Replace('- Executable forwarded-header trust scenarios for ASP.NET Core applications.', '- Planned package; not yet published.')
    $planned = New-ReleaseFixture 'planned-tagged-release' '0.1.0' $plannedText 'v0.1.0' $true
    Invoke-WorkflowLifecycle $planned.Root '0.1.0' $true $false 'planned target cannot pass publication gate'

    $missingHistory = New-ReleaseFixture 'development-missing-tag' '0.1.0' $firstReleaseText
    Add-SecondCommit $missingHistory
    Invoke-Lifecycle $missingHistory.Root '0.1.0' '' $false 'development requires a published tag' 'Development'
    Invoke-Lifecycle $firstRelease.Root '0.1.0' '' $false 'exact tag cannot claim development' 'Development'
    Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $missingHistory.Root -ChangelogState Development -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.2.0
    Assert-Result 'development rejects package version mismatch' $false

    $mismatchedBaseline = New-ReleaseFixture 'development-baseline-version' '0.2.0' $subsequentReleaseText 'v0.1.0' $true
    Add-SecondCommit $mismatchedBaseline
    Write-Fixture $mismatchedBaseline.Root '0.1.0' $firstReleaseText
    Invoke-Lifecycle $mismatchedBaseline.Root '0.1.0' '' $false 'published tag must carry matching source version'

    $wrongBranch = New-ReleaseFixture 'development-unrelated-tag' '0.1.0' $firstReleaseText 'v0.1.0' $true
    & git -C $wrongBranch.Root checkout --orphan unrelated --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not create unrelated development history.' }
    & git -C $wrongBranch.Root -c user.name='ForwardTrust Fixture' -c user.email='fixture@example.invalid' commit --quiet -m 'unrelated history'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit unrelated history.' }
    Invoke-Lifecycle $wrongBranch.Root '0.1.0' '' $false 'development tag must be an ancestor'

    $developmentCommit = New-ReleaseFixture 'development-expectations' '0.1.0' $firstReleaseText 'v0.1.0' $true
    Add-SecondCommit $developmentCommit
    foreach ($arguments in @(@('-ExpectedTag', 'v0.2.0'), @('-ExpectedCommit', ('0' * 40)), @('-ExpectedReleaseDate', $developmentCommit.Date))) {
        Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $developmentCommit.Root -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0 @arguments
        Assert-Result "development rejects contradictory release expectations: $arguments" $false
    }

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
    Invoke-WorkflowLifecycle $wrongTagCommit.Root '0.1.0' $true $false 'release CI propagates exact commit mismatch'
    Invoke-WorkflowLifecycle $wrongPackage.Root '0.1.0' $true $false 'release CI propagates source version mismatch'
    Invoke-WorkflowLifecycle $wrongTag.Root '0.1.1' $true $false 'release CI propagates tag version mismatch'

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

    # Automatic dispatch must not disguise a malformed finalized state as Unreleased.
    foreach ($heading in @('## [0.1.0]-2026-10-18', '## [0.1.0] - 2026-02-30', '## [0.2.0] - 2026-10-18', '## [0.1.x] - 2026-10-18')) {
        $fixtureRoot = Join-Path $tempRoot ([guid]::NewGuid().ToString('N'))
        Write-Fixture $fixtureRoot '0.1.0' ($firstReleaseText.Replace('## [0.1.0] - 2026-10-18', $heading))
        Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $fixtureRoot -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
        Assert-Result "automatic gate rejects heading $heading" $false
    }
    $taggedUnreleased = New-ReleaseFixture 'tagged-unreleased' '0.1.0' $firstReleaseText 'v0.1.0' $true
    Write-Fixture $taggedUnreleased.Root '0.1.0' "# Changelog`n`n## [Unreleased]`n`n### Added`n`n- Initial package."
    Amend-FixtureCommit $taggedUnreleased.Root
    Invoke-NestedPwsh -NoProfile -File $packageGate -Stage Changelog -RepositoryRoot $taggedUnreleased.Root -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
    Assert-Result 'automatic gate rejects tagged Unreleased version' $false

    $allFailure = Join-Path $tempRoot 'all-stage-invalid-changelog'
    Write-Fixture $allFailure '0.1.0' "# Changelog`n`n## [Unreleased]`n`n## [0.1.0] - invalid-date"
    $artifactSentinel = Join-Path $allFailure 'artifacts/sentinel.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $artifactSentinel) | Out-Null
    Set-Content -LiteralPath $artifactSentinel -Value 'preserve before validation' -Encoding utf8
    Invoke-NestedPwsh -NoProfile -File $packageGate -RepositoryRoot $allFailure -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
    Assert-Result 'default All stage propagates changelog failure before build/cleanup' $false
    if (-not (Test-Path -LiteralPath $artifactSentinel)) { throw 'Invalid changelog reached artifact cleanup.' }

    Write-Output 'Changelog contract tests passed: unreleased/development/finalized/tagged selection, LF/CRLF published-history preservation, actual CI/release lifecycle commands, planned-target rejection, heading/category/remediation rules, version/date/tag/SHA mismatches, and failure propagation.'
}
finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($tempRoot).StartsWith($resolvedScratch, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Changelog fixture cleanup path is outside the temporary directory.'
    }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

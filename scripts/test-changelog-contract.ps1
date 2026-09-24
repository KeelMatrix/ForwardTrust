[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedPackageVersion,
    [string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $RepositoryRoot).Path
$validator = Join-Path $root 'scripts/validate-release.ps1'

function Invoke-Contract([string]$Fixture, [bool]$ShouldPass) {
    & pwsh -NoProfile -File $validator -RepositoryRoot $Fixture -ExpectedVersion $ExpectedVersion -ExpectedPackageVersion $ExpectedPackageVersion
    $passed = $LASTEXITCODE -eq 0
    if ($passed -ne $ShouldPass) {
        throw "Unexpected changelog contract result for fixture '$Fixture'. Expected pass=$ShouldPass, actual pass=$passed."
    }
}

Invoke-Contract $root $true

$temp = Join-Path ([IO.Path]::GetTempPath()) ("forwardtrust-release-contract-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    Set-Content -Path (Join-Path $temp 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>' -Encoding utf8
    Set-Content -Path (Join-Path $temp 'CHANGELOG.md') -Value "# Changelog`n`n## [Unreleased] - Planned`n`nWaiting." -Encoding utf8
    Invoke-Contract $temp $false

    Set-Content -Path (Join-Path $temp 'CHANGELOG.md') -Value "# Changelog`n`n## [0.1.0] - 2026-09-24`n`n### Added`n`n- Initial package." -Encoding utf8
    Invoke-Contract $temp $true

    Set-Content -Path (Join-Path $temp 'CHANGELOG.md') -Value "# Changelog`n`n## [0.1.0] - 2026-09-24`n`n### Added`n`n- Initial package.`n`n### Fixed`n`n- A remediation note." -Encoding utf8
    Invoke-Contract $temp $false

    $remediationMarkers = @(
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
        'this removes',
        'this fixes',
        'changed from'
    )
    foreach ($marker in $remediationMarkers) {
        Set-Content -Path (Join-Path $temp 'CHANGELOG.md') -Value "# Changelog`n`n## [0.1.0] - 2026-09-24`n`n### Added`n`n- This entry contains $marker wording." -Encoding utf8
        Invoke-Contract $temp $false
    }

    Set-Content -Path (Join-Path $temp 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>0.2.0</Version></PropertyGroup></Project>' -Encoding utf8
    Invoke-Contract $temp $false
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'Changelog contract tests passed: finalized entry, planned rejection, non-Added category rejection, all remediation-marker rejections, and version mismatch rejection.'

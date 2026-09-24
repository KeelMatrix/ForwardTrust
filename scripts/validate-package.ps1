[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [switch]$RequireIcon
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $RepositoryRoot).Path
$packageDirectoryPath = if ([IO.Path]::IsPathRooted($PackageDirectory)) {
    (Resolve-Path $PackageDirectory).Path
} else {
    (Resolve-Path (Join-Path $root $PackageDirectory)).Path
}
$packageId = 'KeelMatrix.ForwardTrust'
$description = 'Verify ASP.NET Core forwarded-header trust behavior with executable integration-test scenarios.'
$nupkgName = "$packageId.$ExpectedVersion.nupkg"
$snupkgName = "$packageId.$ExpectedVersion.snupkg"

function Fail([string]$Message) {
    throw $Message
}

if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
    Fail "ExpectedVersion must be a stable X.Y.Z version."
}

$rootIcon = Join-Path $root 'icon.png'
if ($RequireIcon -and -not (Test-Path -LiteralPath $rootIcon -PathType Leaf)) {
    Fail "Release validation requires the founder-owned icon at: $rootIcon"
}

$artifacts = @(Get-ChildItem -LiteralPath $packageDirectoryPath -File)
$expectedArtifactNames = @($nupkgName, $snupkgName)
$unexpectedArtifacts = @($artifacts | Where-Object { $_.Name -notin $expectedArtifactNames })
if ($unexpectedArtifacts.Count -gt 0 -or $artifacts.Count -ne 2) {
    $actual = ($artifacts.Name -join ', ')
    Fail "Package directory must contain exactly $nupkgName and $snupkgName; found: $actual"
}

$nupkgPath = Join-Path $packageDirectoryPath $nupkgName
$snupkgPath = Join-Path $packageDirectoryPath $snupkgName
if (-not (Test-Path -LiteralPath $nupkgPath -PathType Leaf) -or -not (Test-Path -LiteralPath $snupkgPath -PathType Leaf)) {
    Fail "Expected package artifacts were not produced."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-Nuspec($archive, [string]$expectedName) {
    $nuspecEntries = @($archive.Entries | Where-Object FullName -eq $expectedName)
    if ($nuspecEntries.Count -ne 1) {
        Fail "Archive must contain exactly $expectedName."
    }

    $reader = New-Object IO.StreamReader($nuspecEntries[0].Open())
    try {
        return [xml]$reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function Assert-NoSensitiveEntries([string[]]$names) {
    if ($names -match '(?i)(^|/)(\.env(\..*)?|keelmatrix\.telemetry\.json|.*\.(pfx|p12|pem|key|snk))$') {
        Fail 'Package contains a sensitive file.'
    }
}

function Assert-Nupkg($path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        Assert-NoSensitiveEntries $names
        $allowed = @(
            '_rels/.rels',
            '[Content_Types].xml',
            "$packageId.nuspec",
            "lib/net8.0/$packageId.dll",
            "lib/net8.0/$packageId.xml",
            'README.md'
        )
        $allowed += @($names | Where-Object { $_ -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
        if ($RequireIcon) {
            $allowed += 'icon.png'
        }

        $unexpected = @($names | Where-Object { $_ -notin $allowed })
        if ($unexpected.Count -gt 0) {
            Fail "Unexpected .nupkg entries: $($unexpected -join ', ')"
        }

        $required = @('_rels/.rels', '[Content_Types].xml', "$packageId.nuspec", "lib/net8.0/$packageId.dll", "lib/net8.0/$packageId.xml", 'README.md')
        if ($RequireIcon) { $required += 'icon.png' }
        foreach ($entry in $required) {
            if ($entry -notin $names) { Fail "Required .nupkg entry is missing: $entry" }
        }

        $nuspecXml = Read-Nuspec $archive "$packageId.nuspec"
        $metadata = $nuspecXml.package.metadata
        if ($metadata.id -ne $packageId -or $metadata.version -ne $ExpectedVersion) { Fail 'Package identity/version mismatch.' }
        if ($metadata.description -ne $description) { Fail 'Package description mismatch.' }
        if ($metadata.readme -ne 'README.md') { Fail 'Package README metadata is missing or incorrect.' }
        if ($metadata.license.type -ne 'expression' -or $metadata.license.'#text' -ne 'MIT') { Fail 'Package MIT license metadata is missing or incorrect.' }
        if ($RequireIcon -and $metadata.icon -ne 'icon.png') { Fail 'Required package icon metadata is missing or incorrect.' }
        $tfmEntries = @($names | Where-Object { $_ -match '^lib/([^/]+)/' } | ForEach-Object { ($_ -split '/')[1] } | Select-Object -Unique)
        if ($tfmEntries.Count -ne 1 -or $tfmEntries[0] -ne 'net8.0') { Fail "Package target framework entries are not exactly net8.0: $($tfmEntries -join ', ')" }
        if (-not (Test-Path -LiteralPath $rootIcon -PathType Leaf) -and 'icon.png' -in $names) { Fail 'Package contains an icon without the required repository-root icon.' }
        return $names.Count
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-Snupkg($path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        Assert-NoSensitiveEntries $names
        $allowed = @('_rels/.rels', '[Content_Types].xml', "$packageId.nuspec", "lib/net8.0/$packageId.pdb")
        $allowed += @($names | Where-Object { $_ -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
        $unexpected = @($names | Where-Object { $_ -notin $allowed })
        if ($unexpected.Count -gt 0) { Fail "Unexpected .snupkg entries: $($unexpected -join ', ')" }
        foreach ($entry in @('_rels/.rels', '[Content_Types].xml', "$packageId.nuspec", "lib/net8.0/$packageId.pdb")) {
            if ($entry -notin $names) { Fail "Required .snupkg entry is missing: $entry" }
        }
        $nuspecXml = Read-Nuspec $archive "$packageId.nuspec"
        if ($nuspecXml.package.metadata.id -ne $packageId -or $nuspecXml.package.metadata.version -ne $ExpectedVersion) { Fail '.snupkg identity/version mismatch.' }
        return $names.Count
    }
    finally {
        $archive.Dispose()
    }
}

$nupkgEntries = Assert-Nupkg $nupkgPath
$snupkgEntries = Assert-Snupkg $snupkgPath
Write-Output "Package archive validation passed: nupkg=$nupkgEntries entries; snupkg=$snupkgEntries entries; id=$packageId; version=$ExpectedVersion; tfm=net8.0; iconRequired=$RequireIcon"

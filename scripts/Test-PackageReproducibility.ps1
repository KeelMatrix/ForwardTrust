[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PackageDirectory = 'artifacts/packages',
    [string]$ExpectedVersion = '0.1.0'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path $RepositoryRoot).Path
$packageDirectoryPath = if ([IO.Path]::IsPathRooted($PackageDirectory)) { (Resolve-Path $PackageDirectory).Path } else { (Resolve-Path (Join-Path $root $PackageDirectory)).Path }
$repeatDirectory = Join-Path $root 'artifacts/repeat-pack'
if (Test-Path -LiteralPath $repeatDirectory) { Remove-Item -LiteralPath $repeatDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $repeatDirectory | Out-Null

Set-Location $root
Write-Output '> dotnet pack src/KeelMatrix.ForwardTrust/KeelMatrix.ForwardTrust.csproj -c Release --no-build -o artifacts/repeat-pack'
dotnet pack src/KeelMatrix.ForwardTrust/KeelMatrix.ForwardTrust.csproj -c Release --no-build -o $repeatDirectory
if ($LASTEXITCODE -ne 0) { throw 'The repeated package build failed.' }

Invoke-NestedPwsh -NoProfile -File (Join-Path $root 'scripts/Normalize-PackageArchives.ps1') -PackageDirectory $repeatDirectory
if ($LASTEXITCODE -ne 0) { throw 'The repeated package normalization failed.' }

foreach ($extension in @('nupkg', 'snupkg')) {
    $name = "KeelMatrix.ForwardTrust.$ExpectedVersion.$extension"
    $first = Join-Path $packageDirectoryPath $name
    $second = Join-Path $repeatDirectory $name
    if (-not (Test-Path -LiteralPath $first) -or -not (Test-Path -LiteralPath $second)) {
        throw "Expected repeated package artifact is missing: $name"
    }

    $firstHash = (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash
    $secondHash = (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash
    if ($firstHash -ne $secondHash) {
        throw "Repeated normalized $extension output is not byte-identical: $firstHash versus $secondHash"
    }
}

Write-Output "Package reproducibility passed: repeated normalized nupkg and snupkg match for version $ExpectedVersion."

[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path $RepositoryRoot).Path
$launchGuard = Join-Path $root 'build/Test-NestedPwshLaunch.ps1'
& $launchGuard -SelfTest
if ($LASTEXITCODE -ne 0) { throw 'Nested PowerShell launch guard self-test failed.' }
& $launchGuard
if ($LASTEXITCODE -ne 0) { throw 'Nested PowerShell launch guard failed.' }
Set-Location $root
$artifactRoot = Join-Path $root 'artifacts'
$packages = Join-Path $artifactRoot 'packages'
$smokeFeed = Join-Path $artifactRoot 'smoke-feed'
$consumerPackages = Join-Path $artifactRoot 'consumer-packages'

if (Test-Path $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
New-Item -ItemType Directory -Path $packages, $smokeFeed, $consumerPackages | Out-Null

function Invoke-Step([string]$Command, [scriptblock]$Action) {
    Write-Output "`n> $Command"
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code ${LASTEXITCODE}: $Command" }
}

Invoke-Step 'dotnet restore KeelMatrix.ForwardTrust.sln --configfile NuGet.config --force' {
    dotnet restore KeelMatrix.ForwardTrust.sln --configfile NuGet.config --force
}
Invoke-Step 'dotnet build KeelMatrix.ForwardTrust.sln -c Release --no-restore' {
    dotnet build KeelMatrix.ForwardTrust.sln -c Release --no-restore
}
Invoke-Step 'dotnet test KeelMatrix.ForwardTrust.sln -c Release --no-build --logger "console;verbosity=minimal"' {
    dotnet test KeelMatrix.ForwardTrust.sln -c Release --no-build --logger 'console;verbosity=minimal'
}
Invoke-Step 'dotnet format KeelMatrix.ForwardTrust.sln --verify-no-changes --no-restore' {
    dotnet format KeelMatrix.ForwardTrust.sln --verify-no-changes --no-restore
}
Invoke-Step 'dotnet pack src/KeelMatrix.ForwardTrust/KeelMatrix.ForwardTrust.csproj -c Release --no-build -o artifacts/packages' {
    dotnet pack src/KeelMatrix.ForwardTrust/KeelMatrix.ForwardTrust.csproj -c Release --no-build -o $packages
}
Invoke-Step 'pwsh -NoProfile -File scripts/Normalize-PackageArchives.ps1 -PackageDirectory artifacts/packages' {
    Invoke-NestedPwsh -NoProfile -File scripts/Normalize-PackageArchives.ps1 -PackageDirectory $packages
}
Invoke-Step 'pwsh -NoProfile -File scripts/Test-VulnerabilityAudit.ps1' {
    Invoke-NestedPwsh -NoProfile -File scripts/Test-VulnerabilityAudit.ps1
}
Invoke-Step 'pwsh -NoProfile -File scripts/Invoke-VulnerabilityAudit.ps1 -ReportPath artifacts/vulnerability-audit.json' {
    Invoke-NestedPwsh -NoProfile -File scripts/Invoke-VulnerabilityAudit.ps1 -ReportPath (Join-Path $artifactRoot 'vulnerability-audit.json')
}
Invoke-Step 'pwsh -NoProfile -File scripts/Test-PackageReproducibility.ps1 -PackageDirectory artifacts/packages -ExpectedVersion 0.1.0' {
    Invoke-NestedPwsh -NoProfile -File scripts/Test-PackageReproducibility.ps1 -PackageDirectory $packages -ExpectedVersion '0.1.0'
}
Invoke-Step 'pwsh -NoProfile -File scripts/test-changelog-contract.ps1 -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0' {
    Invoke-NestedPwsh -NoProfile -File scripts/test-changelog-contract.ps1 -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
}

$nupkg = Join-Path $packages 'KeelMatrix.ForwardTrust.0.1.0.nupkg'
$snupkg = Join-Path $packages 'KeelMatrix.ForwardTrust.0.1.0.snupkg'
Invoke-Step 'pwsh -NoProfile -File scripts/validate-package.ps1 -PackageDirectory artifacts/packages -ExpectedVersion 0.1.0' {
    Invoke-NestedPwsh -NoProfile -File scripts/validate-package.ps1 -PackageDirectory $packages -ExpectedVersion 0.1.0
}

Invoke-Step 'pwsh -NoProfile -File scripts/validate-quickstart.ps1 -PackagePath artifacts/packages/KeelMatrix.ForwardTrust.0.1.0.nupkg -ExpectedVersion 0.1.0' {
    Invoke-NestedPwsh -NoProfile -File scripts/validate-quickstart.ps1 -PackagePath $nupkg -ExpectedVersion 0.1.0
}

Copy-Item -LiteralPath $nupkg -Destination $smokeFeed
if ((Get-Content 'smoke/ForwardTrust.Consumer/ForwardTrust.Consumer.csproj' -Raw) -match '<ProjectReference') { throw 'Consumer smoke must use PackageReference, not ProjectReference.' }
$env:NUGET_PACKAGES = $consumerPackages
Invoke-Step 'dotnet restore smoke/ForwardTrust.Consumer/ForwardTrust.Consumer.csproj --configfile smoke/NuGet.config --force --no-cache' {
    dotnet restore smoke/ForwardTrust.Consumer/ForwardTrust.Consumer.csproj --configfile smoke/NuGet.config --force --no-cache
}
Invoke-Step 'dotnet run --project smoke/ForwardTrust.Consumer/ForwardTrust.Consumer.csproj -c Release --no-restore' {
    dotnet run --project smoke/ForwardTrust.Consumer/ForwardTrust.Consumer.csproj -c Release --no-restore
}

$icon = Join-Path $root 'icon.png'
if (Test-Path $icon) {
    Write-Output "Icon path present: $icon"
} else {
    Write-Warning "Founder-owned icon is not present at required path: $icon"
}
Write-Output "Package gate passed. Hashes:"
Get-FileHash $nupkg, $snupkg -Algorithm SHA256 | Format-Table -AutoSize

[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PackageDirectory = 'artifacts/packages',
    [string]$ExpectedVersion = '0.1.0'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$packages = if ([IO.Path]::IsPathRooted($PackageDirectory)) { $PackageDirectory } else { Join-Path $root $PackageDirectory }
$checker = Join-Path $root 'tools/SourceLinkCheck/SourceLinkCheck.csproj'
$product = Join-Path $root 'src/KeelMatrix.ForwardTrust'
$project = Join-Path $product 'KeelMatrix.ForwardTrust.csproj'
$name = "KeelMatrix.ForwardTrust.$ExpectedVersion"

dotnet run --project $checker -c Release --no-build -- (Join-Path $packages "$name.nupkg") (Join-Path $packages "$name.snupkg") $root --self-test
if ($LASTEXITCODE -ne 0) { throw 'SourceLink artifact positive and mutation controls failed.' }

foreach ($fixture in @('project-path-map', 'missing-generated-sources')) {
    $directory = Join-Path $root "artifacts/sourcelink-controls/$fixture"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $buildArguments = @('pack', $project, '-c', 'Release', '--no-restore', '-o', $directory,
        "-p:IntermediateOutputPath=obj/sourcelink-controls/$fixture/", "-p:OutputPath=$directory/bin/")
    if ($fixture -eq 'project-path-map') {
        $buildArguments += "-p:PathMap=$product=/_/"
    } else {
        $buildArguments += '-p:EmbedUntrackedSources=false'
    }
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Could not build SourceLink negative control: $fixture" }
    Invoke-NestedPwsh -NoProfile -File (Join-Path $root 'scripts/Normalize-PackageArchives.ps1') -PackageDirectory $directory
    if ($LASTEXITCODE -ne 0) { throw "Could not normalize SourceLink negative control: $fixture" }
    # Exercise the shared local/release entry point, including its child-process failure propagation.
    $output = Invoke-NestedPwsh -NoProfile -File (Join-Path $root 'scripts/validate-package.ps1') -RepositoryRoot $root -PackageDirectory $directory -ExpectedVersion $ExpectedVersion 2>&1
    $code = $LASTEXITCODE
    Write-Output $output
    $reason = if ($fixture -eq 'project-path-map') { 'Document is neither a tracked source nor a generated source' } else { 'Generated source must have one embedded source record' }
    if ($code -eq 0 -or ($output -join "`n") -notmatch [regex]::Escape($reason)) {
        throw "SourceLink negative control did not fail for the intended reason: $fixture"
    }
    Write-Output "SourceLink build negative control passed: $fixture"
}
$global:LASTEXITCODE = 0

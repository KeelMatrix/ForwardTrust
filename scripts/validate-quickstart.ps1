[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $RepositoryRoot).Path
$package = (Resolve-Path $PackagePath).Path

function Fail([string]$Message) {
    throw $Message
}

function Write-Utf8([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Extract-QuickStart([string]$Readme, [string]$Source) {
    $match = [regex]::Match($Readme, '(?ms)^## Quick start\s*\r?\n.*?```csharp\r?\n(?<code>.*?)\r?\n```')
    if (-not $match.Success) {
        Fail "Could not extract the Quick start C# code fence from $Source."
    }

    return $match.Groups['code'].Value
}

function Read-ArchiveText([IO.Compression.ZipArchive]$Archive, [string]$EntryName) {
    $entries = @($Archive.Entries | Where-Object FullName -eq $EntryName)
    if ($entries.Count -ne 1) {
        Fail "Archive must contain exactly $EntryName."
    }

    $reader = New-Object IO.StreamReader($entries[0].Open())
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

if ((Split-Path -Leaf $package) -ne "KeelMatrix.ForwardTrust.$ExpectedVersion.nupkg") {
    Fail "Package path must name KeelMatrix.ForwardTrust.$ExpectedVersion.nupkg."
}

$rootSnippet = Extract-QuickStart (Get-Content -Raw (Join-Path $root 'README.md')) 'README.md'
$projectReadme = Join-Path $root 'src/KeelMatrix.ForwardTrust/README.md'
$projectSnippet = Extract-QuickStart (Get-Content -Raw $projectReadme) $projectReadme
if ($rootSnippet -cne $projectSnippet) {
    Fail 'The root and packable project README Quick start snippets differ.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $packedReadme = Read-ArchiveText $archive 'README.md'
}
finally {
    $archive.Dispose()
}

$packedSnippet = Extract-QuickStart $packedReadme "$package!README.md"
if ($packedSnippet -cne $projectSnippet) {
    Fail 'The packed README Quick start snippet differs from the packable project README.'
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("forwardtrust-quickstart-" + [guid]::NewGuid().ToString('N'))
$consumer = Join-Path $temp 'consumer'
$cache = Join-Path $temp 'packages'
New-Item -ItemType Directory -Path $consumer, $cache | Out-Null
$previousNugetPackages = $env:NUGET_PACKAGES
try {
    $feed = [Security.SecurityElement]::Escape((Split-Path -Parent $package))
    Write-Utf8 (Join-Path $consumer 'QuickStartConsumer.csproj') @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="KeelMatrix.ForwardTrust" Version="$ExpectedVersion" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="8.0.31" />
    <PackageReference Include="xunit" Version="2.9.3" />
  </ItemGroup>
</Project>
"@
    Write-Utf8 (Join-Path $consumer 'Program.cs') $packedSnippet
    Write-Utf8 (Join-Path $consumer 'NuGet.config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-forwardtrust" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-forwardtrust">
      <package pattern="KeelMatrix.ForwardTrust" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@

    $env:NUGET_PACKAGES = $cache
    $restore = Join-Path $consumer 'QuickStartConsumer.csproj'
    Write-Output "> dotnet restore $restore --configfile $(Join-Path $consumer 'NuGet.config') --force --no-cache"
    & dotnet restore $restore --configfile (Join-Path $consumer 'NuGet.config') --force --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Quick start consumer restore failed with exit code $LASTEXITCODE." }

    Write-Output "> dotnet build $restore -c Release --no-restore"
    & dotnet build $restore -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Quick start consumer build failed with exit code $LASTEXITCODE." }
}
finally {
    if ($null -eq $previousNugetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $previousNugetPackages
    }

    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'Published Quick start snippet compiled successfully in an isolated PackageReference consumer.'

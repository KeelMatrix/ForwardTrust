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

$installPattern = '(?ms)^## Install\s*\r?\n\s*```bash\r?\n(?<commands>.*?)\r?\n```'
$packedInstall = [regex]::Match($packedReadme, $installPattern)
if (-not $packedInstall.Success) { Fail 'Packed README has no executable Install commands.' }
foreach ($readme in @((Get-Content -Raw (Join-Path $root 'README.md')), (Get-Content -Raw $projectReadme))) {
    $install = [regex]::Match($readme, $installPattern)
    if (-not $install.Success -or $install.Groups['commands'].Value -cne $packedInstall.Groups['commands'].Value) {
        Fail 'Root, project and packed README installation commands must agree.'
    }
}

$tempBase = if ($env:TMPDIR) { $env:TMPDIR } else { [IO.Path]::GetTempPath() }
$temp = Join-Path $tempBase ("forwardtrust-quickstart-" + [guid]::NewGuid().ToString('N'))
$consumer = Join-Path $temp 'ForwardTrust.Tests'
$cache = Join-Path $temp 'packages'
New-Item -ItemType Directory -Path $temp, $cache | Out-Null
$previousNugetPackages = $env:NUGET_PACKAGES
$previousLocation = Get-Location
try {
    $feed = [Security.SecurityElement]::Escape((Split-Path -Parent $package))
    Write-Utf8 (Join-Path $temp 'global.json') (Get-Content -Raw (Join-Path $root 'global.json'))
    Write-Utf8 (Join-Path $temp 'NuGet.config') @"
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
    Set-Location $temp
    foreach ($line in ($packedInstall.Groups['commands'].Value -split '\r?\n')) {
        if ($line -ceq 'cd ForwardTrust.Tests') { Set-Location $consumer; continue }
        # Pass argument arrays directly; do not evaluate README text as shell code.
        if ($line -notmatch '^dotnet (new xunit --framework net8\.0 --name ForwardTrust\.Tests|add package [A-Za-z0-9.]+(?: --version [0-9.]+)?)$') {
            Fail "Unsupported installation command: $line"
        }
        $parts = $line -split ' '
        $arguments = $parts[1..($parts.Count - 1)]
        Write-Output "> $line"
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { Fail "Published installation command failed: $line" }
    }
    $project = Join-Path $consumer 'ForwardTrust.Tests.csproj'
    [xml]$projectXml = Get-Content -Raw $project
    $productReference = @($projectXml.Project.ItemGroup.PackageReference | Where-Object Include -eq 'KeelMatrix.ForwardTrust')
    if ($productReference.Count -ne 1 -or $productReference[0].Version -cne $ExpectedVersion) {
        Fail 'The published installation did not select the exact built product version.'
    }
    if ($projectXml.Project.PropertyGroup.TargetFramework -cne 'net8.0') { Fail 'Quick start must target net8.0.' }
    Write-Utf8 (Join-Path $consumer 'UnitTest1.cs') $packedSnippet
    $results = Join-Path $consumer 'TestResults'
    & dotnet test $project -c Release --filter FullyQualifiedName~ForwardTrustQuickStartTests --logger 'trx;LogFileName=quickstart.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { Fail 'Published Quick start test failed.' }
    [xml]$trx = Get-Content -Raw (Join-Path $results 'quickstart.trx')
    $counts = $trx.TestRun.ResultSummary.Counters
    if ($counts.total -ne '1' -or $counts.executed -ne '1' -or $counts.passed -ne '1' -or $counts.failed -ne '0') {
        Fail 'Exactly one published Quick start test must execute and pass.'
    }
}
finally {
    Set-Location $previousLocation
    if ($null -eq $previousNugetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $previousNugetPackages
    }

    $resolvedBase = [IO.Path]::GetFullPath($tempBase).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($temp).StartsWith($resolvedBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Quick start cleanup path is outside the temporary directory.'
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'Published installation and Quick start test passed in a pristine net8.0 PackageReference project (trusted and untrusted scenarios).'

[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$ProjectSelectionOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$validator = Join-Path $PSScriptRoot 'validate-website-metadata.ps1'
$scratchParent = Join-Path $root 'artifacts'
$fixture = Join-Path $scratchParent ('website-metadata-' + [guid]::NewGuid().ToString('N'))
$projectDirectory = Join-Path $fixture 'src/KeelMatrix.ForwardTrust'
$project = Join-Path $projectDirectory 'KeelMatrix.ForwardTrust.csproj'
$props = Join-Path $fixture 'Directory.Build.props'
$targets = Join-Path $fixture 'Directory.Build.targets'
$manifestPath = Join-Path $fixture 'keelmatrix.website.json'
$archivePath = Join-Path $fixture 'KeelMatrix.ForwardTrust.0.1.1.nupkg'
$tags = 'testing;keelmatrix-public-product;keelmatrix-primary'
$url = 'https://github.com/KeelMatrix/ForwardTrust'
$manifest = '{"schemaVersion":1,"packages":{"KeelMatrix.ForwardTrust":{"visibility":"public-product","role":"primary"}}}'
$source = "<Project><PropertyGroup><IsPackable>true</IsPackable><PackageId>KeelMatrix.ForwardTrust</PackageId><PackageTags>$tags</PackageTags><PackageProjectUrl>$url</PackageProjectUrl><RepositoryUrl>$url</RepositoryUrl><RepositoryType>git</RepositoryType></PropertyGroup></Project>"
$passed = 0
New-Item -ItemType Directory -Path $projectDirectory -Force | Out-Null

function Write-Fixture([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Reset-Fixture {
    if (Test-Path -LiteralPath $targets) { Remove-Item -LiteralPath $targets }
    Write-Fixture $manifestPath $manifest
    Write-Fixture $props $source
    Write-Fixture $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
}

function Assert-Result([string]$Name, [int]$Code, $Output, [bool]$ShouldPass, [string]$Message) {
    if (($Code -eq 0) -ne $ShouldPass -or (-not $ShouldPass -and "$Output" -notlike "*$Message*")) {
        throw "Fixture '$Name' returned $Code, expected pass=$ShouldPass and diagnostic '$Message': $Output"
    }
    $script:passed++
    Write-Output "PASS: $Name"
}

function Test-Fixture([string]$Name, [bool]$ShouldPass, [string]$Message, [string]$Archive) {
    $command = "& '$($validator.Replace("'", "''"))' -RepositoryRoot '$($fixture.Replace("'", "''"))'"
    if ($Archive) { $command += " -PackagePath '$($Archive.Replace("'", "''"))'" }
    $output = Invoke-FixtureCommand $command
    Assert-Result $Name $LASTEXITCODE $output $ShouldPass $Message
}

function Invoke-FixtureCommand([string]$Command) {
    # Capture the exception itself; rendered PowerShell errors depend on terminal width and OS.
    $wrapper = 'try { ' + $Command + ' } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }'
    Invoke-NestedPwsh -NoProfile -Command $wrapper 2>&1
}

function Write-Archive([string]$Content, [string]$Path = $archivePath) {
    $stream = [IO.File]::Create($Path)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('KeelMatrix.ForwardTrust.nuspec').Open())
        try { $writer.Write($Content) } finally { $writer.Dispose() }
    } finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

function Test-EntryPoint([string]$Name, [string]$Command, [string]$Message) {
    $output = Invoke-FixtureCommand $Command
    Assert-Result $Name $LASTEXITCODE $output $false $Message
}

try {
    # Real SDK evaluations: location and language must not hide an effective packable helper.
    foreach ($directory in @('', 'src/Helper', 'tests/Helper', 'smoke/Helper', 'examples/Helper', 'tools/Helper')) {
        foreach ($extension in @('csproj', 'vbproj', 'fsproj')) {
            Reset-Fixture
            $helperDirectory = if ($directory) { Join-Path $fixture $directory } else { $fixture }
            New-Item -ItemType Directory -Path $helperDirectory -Force | Out-Null
            $helper = Join-Path $helperDirectory "Helper.$extension"
            Write-Fixture $helper '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.Helper</PackageId><PackageTags>testing</PackageTags></PropertyGroup></Project>'
            try { Test-Fixture "packable $directory/Helper.$extension" $false 'Unexpected packable project' }
            finally { Remove-Item -LiteralPath $helper }
        }
    }
    foreach ($extension in @('csproj', 'vbproj', 'fsproj')) {
        $helperDirectory = Join-Path $fixture 'tests/Helper'
        $helper = Join-Path $helperDirectory "Helper.$extension"
        $cases = @(
            @('true', '<IsPackable>true</IsPackable>', '', $false),
            @('True', '<IsPackable>True</IsPackable>', '', $false),
            @('TRUE', '<IsPackable>TRUE</IsPackable>', '', $false),
            @('false', '<IsPackable>false</IsPackable>', '', $true),
            @('False', '<IsPackable>False</IsPackable>', '', $true),
            @('FALSE', '<IsPackable>FALSE</IsPackable>', '', $true),
            @('SDK default', '', '', $false),
            @('Release enabled', '<IsPackable>false</IsPackable><IsPackable Condition="''$(Configuration)'' == ''Release''">True</IsPackable>', '', $false),
            @('Release disabled', '<IsPackable>true</IsPackable><IsPackable Condition="''$(Configuration)'' == ''Release''">FALSE</IsPackable>', '', $true),
            @('targets enabled', '<IsPackable>false</IsPackable>', 'True', $false),
            @('targets disabled', '<IsPackable>true</IsPackable>', 'FALSE', $true),
            @('inherited Release enabled', '', '', $false),
            @('inherited Release disabled', '', '', $true),
            @('project overrides inherited false', '<IsPackable>True</IsPackable>', '', $false)
        )
        foreach ($case in $cases) {
            Reset-Fixture
            if ($case[0] -eq 'SDK default') { Write-Fixture $props ($source.Replace('<IsPackable>true</IsPackable>', '')) }
            if ($case[0] -like 'inherited Release*') {
                $releaseValue = if ($case[3]) { 'FALSE' } else { 'True' }
                $inherited = '<PropertyGroup Condition="''$(MSBuildProjectName)'' == ''Helper''"><IsPackable>false</IsPackable><IsPackable Condition="''$(Configuration)'' == ''Release''">' + $releaseValue + '</IsPackable></PropertyGroup>'
                Write-Fixture $props ($source.Replace('</Project>', "$inherited</Project>"))
            }
            if ($case[0] -eq 'project overrides inherited false') {
                Write-Fixture $props ($source.Replace('<IsPackable>true</IsPackable>', '<IsPackable>FALSE</IsPackable>'))
                Write-Fixture $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsPackable>true</IsPackable></PropertyGroup></Project>'
            }
            Write-Fixture $helper ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.Helper</PackageId><PackageTags>testing</PackageTags>' + $case[1] + '</PropertyGroup></Project>')
            if ($case[2]) {
                Write-Fixture $targets ('<Project><PropertyGroup Condition="''$(MSBuildProjectName)'' == ''Helper''"><IsPackable>' + $case[2] + '</IsPackable></PropertyGroup></Project>')
            }
            try { Test-Fixture "$extension $($case[0])" $case[3] 'Unexpected packable project' }
            finally { Remove-Item -LiteralPath $helper }
        }
        Reset-Fixture
        $upperHelper = Join-Path $helperDirectory ('Helper.' + $extension.ToUpperInvariant())
        Write-Fixture $upperHelper '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.Helper</PackageId></PropertyGroup></Project>'
        try { Test-Fixture "$extension uppercase extension" $false 'Unexpected packable project' }
        finally { Remove-Item -LiteralPath $upperHelper }

        # Nonpackable helpers in every source location may omit all package metadata.
        $helpers = @()
        foreach ($directory in @('', 'src/Helper', 'tests/Helper', 'smoke/Helper', 'examples/Helper', 'tools/Helper')) {
            $helper = Join-Path $fixture "$directory/Helper.$extension"
            Write-Fixture $helper '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsPackable>FALSE</IsPackable><PackageId>Unclassified</PackageId><PackageTags /></PropertyGroup></Project>'
            $helpers += $helper
        }
        try { Test-Fixture "$extension nonpackable helpers in every directory" $true }
        finally { $helpers | ForEach-Object { Remove-Item -LiteralPath $_ } }

        foreach ($invalidProject in @('<Project', '<Project Sdk="Microsoft.NET.Sdk"><Import Project="missing-required.props" /></Project>')) {
            Write-Fixture $helper $invalidProject
            try { Test-Fixture "$extension evaluation failure" $false 'source evaluation failed' }
            finally { Remove-Item -LiteralPath $helper }
        }
        Write-Fixture $helper '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
        $quotedGate = (Join-Path $PSScriptRoot 'package-gate.ps1').Replace("'", "''")
        $quotedRoot = $fixture.Replace("'", "''")
        try { Test-EntryPoint "$extension native metadata stage rejects helper with matching package ID" "& '$quotedGate' -RepositoryRoot '$quotedRoot' -Stage WebsiteMetadata" 'Website metadata source gate failed' }
        finally { Remove-Item -LiteralPath $helper }
    }
    Reset-Fixture
    $excluded = @()
    foreach ($directory in @('.git', 'bin', 'obj', 'artifacts', 'tests/Helper/bin', 'tools/Helper/obj', 'examples/Helper/artifacts')) {
        $excludedDirectory = Join-Path $fixture $directory
        New-Item -ItemType Directory -Path $excludedDirectory -Force | Out-Null
        foreach ($extension in @('csproj', 'vbproj', 'fsproj')) {
            $excludedProject = Join-Path $excludedDirectory "Invalid.$extension"
            Write-Fixture $excludedProject '<Project'
            $excluded += $excludedProject
        }
    }
    try { Test-Fixture 'generated and git directories are pruned' $true }
    finally { $excluded | ForEach-Object { Remove-Item -LiteralPath $_ } }

    Reset-Fixture
    $replacement = Join-Path $fixture 'tests/Helper/Helper.csproj'
    Write-Fixture $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup></Project>'
    Write-Fixture $replacement '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    try { Test-Fixture 'matching-ID helper cannot replace disabled shipping project' $false 'Unexpected packable project' }
    finally { Remove-Item -LiteralPath $replacement }
    foreach ($value in @('True', 'TRUE')) {
        Reset-Fixture
        Write-Fixture $props ($source.Replace('<IsPackable>true</IsPackable>', "<IsPackable>$value</IsPackable>"))
        Test-Fixture "shipping packability $value" $true
    }
    foreach ($case in @(
            @('PackageTags', 'testing', 'exactly one visibility'),
            @('PackageId', 'KeelMatrix.Other', 'Source package ID'),
            @('RepositoryUrl', 'https://example.org/Other', 'canonical project/repository URL'),
            @('IsPackable', 'FALSE', 'exactly one shipping source'))) {
        Reset-Fixture
        Write-Fixture $targets ('<Project><PropertyGroup Condition="''$(Configuration)'' == ''Release''"><' + $case[0] + '>' + $case[1] + '</' + $case[0] + '></PropertyGroup></Project>')
        Test-Fixture "shipping Release targets override $($case[0])" $false $case[2]
    }
    if ($ProjectSelectionOnly) {
        Write-Output "Website project selection passed: $passed fixtures."
        return
    }
    $longDiagnostic = 'Manifest classification contains an unknown or incorrectly cased property. This complete diagnostic must survive a narrow terminal without presentation markup.'
    $output = Invoke-FixtureCommand "throw '$longDiagnostic'"
    if ($LASTEXITCODE -ne 1 -or "$output" -cne $longDiagnostic) { throw 'Raw exception diagnostic was changed by error rendering.' }
    $passed++
    Write-Output 'PASS: raw child exception diagnostic preserves long messages'
    Reset-Fixture
    Test-Fixture 'valid inherited source metadata' $true
    Write-Fixture $props ($source.Replace($tags, 'testing; keelmatrix-public-product keelmatrix-primary'))
    Test-Fixture 'NuGet whitespace and semicolon tag separators' $true
    Reset-Fixture
    Remove-Item -LiteralPath $manifestPath
    Test-Fixture 'missing manifest' $false 'Missing repository website manifest'

    $manifestCases = @(
        @('invalid JSON', '{', 'JSON'),
        @('root array', '[]', 'must be an object'),
        @('missing schema', $manifest.Replace('"schemaVersion":1,', ''), 'missing a required property'),
        @('wrong-case schema', $manifest.Replace('schemaVersion', 'SchemaVersion'), 'incorrectly cased property'),
        @('unknown schema', $manifest.Replace(':1,', ':2,'), 'schemaVersion must be 1'),
        @('string schema', $manifest.Replace(':1,', ':"1",'), 'schemaVersion must be 1'),
        @('fractional schema', $manifest.Replace(':1,', ':1.0,'), 'schemaVersion must be 1'),
        @('duplicate schema', $manifest.Replace(':1,', ':1,"schemaVersion":1,'), 'duplicate property'),
        @('conflicting schema', $manifest.Replace(':1,', ':1,"schemaVersion":2,'), 'duplicate property'),
        @('extra root data', $manifest.Replace('{"schemaVersion"', '{"version":"0.1.1","schemaVersion"'), 'unknown or incorrectly cased property'),
        @('missing package', $manifest.Replace('"KeelMatrix.ForwardTrust":{"visibility":"public-product","role":"primary"}', ''), 'missing a required property'),
        @('wrong-case ID', $manifest.Replace('KeelMatrix.ForwardTrust', 'keelmatrix.forwardtrust'), 'incorrectly cased property'),
        @('unknown ID', $manifest.Replace('KeelMatrix.ForwardTrust', 'KeelMatrix.Other'), 'unknown or incorrectly cased property'),
        @('duplicate ID', $manifest.Replace('"packages":{', '"packages":{"KeelMatrix.ForwardTrust":{},'), 'duplicate property'),
        @('extra primary', $manifest.Replace('"packages":{', '"packages":{"KeelMatrix.Other":{"visibility":"public-product","role":"primary"},'), 'unknown or incorrectly cased property'),
        @('missing role', $manifest.Replace(',"role":"primary"', ''), 'missing a required property'),
        @('duplicate role', $manifest.Replace('"role":"primary"', '"role":"primary","role":"component"'), 'duplicate property'),
        @('unknown role', $manifest.Replace('"primary"', '"main"'), 'Invalid manifest role'),
        @('wrong-case role', $manifest.Replace('"primary"', '"Primary"'), 'Invalid manifest role'),
        @('no primary', $manifest.Replace('"primary"', '"component"'), 'exactly one public-product primary'),
        @('unknown visibility', $manifest.Replace('public-product', 'public'), 'Invalid manifest visibility'),
        @('wrong-case visibility', $manifest.Replace('public-product', 'Public-Product'), 'Invalid manifest visibility'),
        @('non-string visibility', $manifest.Replace('"public-product"', 'true'), 'visibility must be a string'),
        @('internal reclassification', $manifest.Replace('public-product', 'internal-package'), 'exactly one public-product primary'),
        @('unknown classification field', $manifest.Replace('"role":', '"url":"https://example.org","role":'), 'unknown or incorrectly cased property'),
        @('null classification', $manifest.Replace('{"visibility":"public-product","role":"primary"}', 'null'), 'must be an object')
    )
    foreach ($case in $manifestCases) {
        Reset-Fixture
        Write-Fixture $manifestPath $case[1]
        Test-Fixture $case[0] $false $case[2]
    }

    $nuspec = "<package xmlns='http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'><metadata><id>KeelMatrix.ForwardTrust</id><tags>$tags</tags><projectUrl>$url</projectUrl><repository type='git' url='$url' /></metadata></package>"
    $tagCases = @(
        @('missing visibility', 'testing;keelmatrix-primary', 'exactly one visibility'),
        @('missing role', 'testing;keelmatrix-public-product', 'exactly one visibility'),
        @('duplicate visibility', "$tags;keelmatrix-public-product", 'exactly one visibility'),
        @('duplicate role', "$tags;keelmatrix-primary", 'exactly one visibility'),
        @('conflicting visibility', "$tags;keelmatrix-internal-package", 'exactly one visibility'),
        @('conflicting role', "$tags;keelmatrix-component", 'exactly one visibility'),
        @('wrong-case visibility', $tags.Replace('public-product', 'Public-Product'), 'incorrectly cased sentinel'),
        @('wrong-case role', $tags.Replace('keelmatrix-primary', 'KeelMatrix-Primary'), 'incorrectly cased sentinel'),
        @('valid plus wrong-case duplicate', "$tags;KEELMATRIX-PRIMARY", 'incorrectly cased sentinel'),
        @('unknown sentinel', "$tags;keelmatrix-main", 'incorrectly cased sentinel'),
        @('visibility disagreement', $tags.Replace('public-product', 'internal-package'), 'disagree with the manifest'),
        @('role disagreement', $tags.Replace('primary', 'component'), 'disagree with the manifest')
    )
    foreach ($case in $tagCases) {
        Reset-Fixture
        Write-Fixture $props ($source.Replace($tags, $case[1]))
        Test-Fixture "source $($case[0])" $false $case[2]
        Reset-Fixture
        Write-Archive ($nuspec.Replace($tags, $case[1]))
        Test-Fixture "built $($case[0])" $false $case[2] $archivePath
    }

    foreach ($badUrl in @('http://github.com/KeelMatrix/ForwardTrust', 'https://example.org/KeelMatrix/ForwardTrust',
            'https://github.com/Other/ForwardTrust', 'https://github.com/KeelMatrix/Other',
            "$url#readme", "$url/", "$url.git", "${url}?tab=readme", 'https://github.com/keelmatrix/ForwardTrust')) {
        foreach ($field in @('PackageProjectUrl', 'RepositoryUrl')) {
            Reset-Fixture
            Write-Fixture $props ($source.Replace("<$field>$url</$field>", "<$field>$badUrl</$field>"))
            Test-Fixture "source $field $badUrl" $false 'canonical project/repository URL'
            Reset-Fixture
            $badNuspec = if ($field -eq 'PackageProjectUrl') { $nuspec.Replace("<projectUrl>$url</projectUrl>", "<projectUrl>$badUrl</projectUrl>") } else { $nuspec.Replace("url='$url'", "url='$badUrl'") }
            Write-Archive $badNuspec
            Test-Fixture "built $field $badUrl" $false 'canonical project/repository URL' $archivePath
        }
    }
    foreach ($case in @(
            @('source ID', 'KeelMatrix.ForwardTrust', 'KeelMatrix.Other', 'Source package ID'),
            @('source repository type', '>git<', '>svn<', 'canonical project/repository URL'),
            @('no shipping source', '>true<', '>false<', 'exactly one shipping source'))) {
        Reset-Fixture
        Write-Fixture $props ($source.Replace($case[1], $case[2]))
        Test-Fixture $case[0] $false $case[3]
    }
    Reset-Fixture
    Write-Fixture $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageTags>testing</PackageTags></PropertyGroup></Project>'
    Test-Fixture 'project override of inherited tags' $false 'exactly one visibility'
    Reset-Fixture
    $sibling = Join-Path $projectDirectory 'Sibling.csproj'
    Write-Fixture $sibling '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    Test-Fixture 'duplicate shipping source ID' $false 'Unexpected packable project'
    Remove-Item -LiteralPath $sibling

    Reset-Fixture
    Write-Archive $nuspec
    Test-Fixture 'valid namespaced built package' $true '' $archivePath
    $symbolPath = Join-Path $fixture 'KeelMatrix.ForwardTrust.0.1.1.snupkg'
    Write-Archive ($nuspec.Replace($tags, 'testing')) $symbolPath
    Test-Fixture 'symbol archive classification' $false 'exactly one visibility' $symbolPath
    foreach ($case in @(
            @('built ID', $nuspec.Replace('<id>KeelMatrix.ForwardTrust</id>', '<id>keelmatrix.forwardtrust</id>'), 'Built package ID'),
            @('built duplicate tags field', $nuspec.Replace('</tags>', '</tags><tags>testing</tags>'), 'exactly one tags element'),
            @('built missing repository', $nuspec.Replace("<repository type='git' url='$url' />", ''), 'exactly one repository element'),
            @('built repository type', $nuspec.Replace("type='git'", "type='svn'"), 'canonical project/repository URL'),
            @('built non-sentinel drift', $nuspec.Replace('testing;', 'changed;'), 'disagree with evaluated source'))) {
        Write-Archive $case[1]
        Test-Fixture $case[0] $false $case[2] $archivePath
    }

    # Exercise the commands used by local development and both workflows, with real child exits.
    Reset-Fixture
    $helper = Join-Path $fixture 'tests/Helper/Helper.vbproj'
    Write-Fixture $helper '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.Helper</PackageId><PackageTags>testing</PackageTags></PropertyGroup></Project>'
    $quotedRoot = $fixture.Replace("'", "''")
    $quotedGate = (Join-Path $PSScriptRoot 'package-gate.ps1').Replace("'", "''")
    Test-EntryPoint 'local full gate stops before restore' "& '$quotedGate' -RepositoryRoot '$quotedRoot'" 'Website metadata source gate failed'
    foreach ($entry in @(
            @('.github/workflows/ci.yml', 'Run package and repository hygiene gates', './scripts/package-gate.ps1', 'Website metadata source gate failed'),
            @('.github/workflows/release.yml', 'Validate website metadata', './scripts/validate-website-metadata.ps1', 'Unexpected packable project'))) {
        $workflow = [IO.File]::ReadAllText((Join-Path $root $entry[0]))
        $pattern = '(?m)^      - name: ' + [regex]::Escape($entry[1]) + '\r?\n        shell: pwsh\r?\n        run: (?<command>[^\r\n]+)'
        $match = [regex]::Match($workflow, $pattern)
        if (-not $match.Success) { throw "Missing executable workflow metadata entry: $($entry[1])" }
        $scriptPath = (Join-Path $root $entry[2].Substring(2)).Replace("'", "''")
        $command = $match.Groups['command'].Value.Replace($entry[2], "& '$scriptPath' -RepositoryRoot '$quotedRoot'")
        Test-EntryPoint $entry[1] $command $entry[3]
    }
    $quotedPackageValidator = (Join-Path $PSScriptRoot 'validate-package.ps1').Replace("'", "''")
    Test-EntryPoint 'shared archive gate rejects packable helper before archive inspection' "& '$quotedPackageValidator' -RepositoryRoot '$quotedRoot' -PackageDirectory '$quotedRoot' -ExpectedVersion 0.1.1" 'Unexpected packable project'
    Remove-Item -LiteralPath $helper
    Reset-Fixture
    Write-Archive ($nuspec.Replace($tags, 'testing'))
    $quotedPackageValidator = (Join-Path $PSScriptRoot 'validate-package.ps1').Replace("'", "''")
    Test-EntryPoint 'shared local/CI/release archive validation propagates failure' "& '$quotedPackageValidator' -RepositoryRoot '$quotedRoot' -PackageDirectory '$quotedRoot' -ExpectedVersion 0.1.1" 'exactly one visibility'
    Reset-Fixture
    $output = Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'package-gate.ps1') -RepositoryRoot $fixture -Stage WebsiteMetadata 2>&1
    Assert-Result 'local metadata stage accepts valid inherited metadata' $LASTEXITCODE $output $true ''
    Write-Output "Website metadata contract passed: $passed fixtures."
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $resolvedParent = [IO.Path]::GetFullPath($scratchParent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped artifacts.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}

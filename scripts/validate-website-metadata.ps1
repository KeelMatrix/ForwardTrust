[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string[]]$PackagePath = @()
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$packageId = 'KeelMatrix.ForwardTrust'
$repositoryUrl = 'https://github.com/KeelMatrix/ForwardTrust'

function Read-WebsiteObject($Element, [string[]]$Fields, [string]$Context) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw "$Context must be an object." }
    $values = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $values.TryAdd($property.Name, $property.Value)) { throw "$Context contains a duplicate property." }
        if ($Fields -cnotcontains $property.Name) { throw "$Context contains an unknown or incorrectly cased property." }
    }
    if ($values.Count -ne $Fields.Count) { throw "$Context is missing a required property." }
    return $values
}

function Assert-WebsiteTags([string]$Tags, [string]$Visibility, [string]$Role, [string]$Context) {
    $tokens = @($Tags -split '[;\s]+' | Where-Object { $_ })
    $visibilityTags = @('keelmatrix-public-product', 'keelmatrix-internal-package')
    $roleTags = @('keelmatrix-primary', 'keelmatrix-component')
    foreach ($token in $tokens) {
        if ($token.StartsWith('keelmatrix-', [StringComparison]::OrdinalIgnoreCase) -and
            ($visibilityTags + $roleTags) -cnotcontains $token) {
            throw "$Context contains an unknown or incorrectly cased sentinel."
        }
    }
    if (@($tokens | Where-Object { $visibilityTags -ccontains $_ }).Count -ne 1 -or
        @($tokens | Where-Object { $roleTags -ccontains $_ }).Count -ne 1) {
        throw "$Context must contain exactly one visibility sentinel and one role sentinel."
    }
    if ($tokens -cnotcontains "keelmatrix-$Visibility" -or $tokens -cnotcontains "keelmatrix-$Role") {
        throw "$Context sentinels disagree with the manifest."
    }
    return ,$tokens
}

function Assert-WebsiteUrls([string]$ProjectUrl, [string]$SourceUrl, [string]$SourceType, [string]$Context) {
    if ($ProjectUrl -cne $repositoryUrl -or $SourceUrl -cne $repositoryUrl -or $SourceType -cne 'git') {
        throw "$Context must use the canonical project/repository URL $repositoryUrl and repository type git."
    }
}

$manifestPath = Join-Path $root 'keelmatrix.website.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Missing repository website manifest.' }
$document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($manifestPath))
try {
    $manifest = Read-WebsiteObject $document.RootElement @('schemaVersion', 'packages') 'Manifest'
    if ($manifest['schemaVersion'].ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        $manifest['schemaVersion'].GetRawText() -cne '1') { throw 'Manifest schemaVersion must be 1.' }
    $packages = Read-WebsiteObject $manifest['packages'] @($packageId) 'Manifest packages'
    $classification = Read-WebsiteObject $packages[$packageId] @('visibility', 'role') 'Manifest classification'
    foreach ($field in @('visibility', 'role')) {
        if ($classification[$field].ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw "Manifest $field must be a string."
        }
    }
    $visibility = $classification['visibility'].GetString()
    $role = $classification['role'].GetString()
    if (@('public-product', 'internal-package') -cnotcontains $visibility) { throw 'Invalid manifest visibility.' }
    if (@('primary', 'component') -cnotcontains $role) { throw 'Invalid manifest role.' }
    # This repository owns one public product and exactly one primary artifact.
    if ($visibility -cne 'public-product' -or $role -cne 'primary') {
        throw 'ForwardTrust must have exactly one public-product primary package.'
    }
} finally {
    $document.Dispose()
}

$sourcePackages = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$projects = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter '*.csproj' -File |
    Where-Object { $_.FullName -notmatch '[/\\](bin|obj)[/\\]' })
foreach ($project in $projects) {
    $evaluation = & dotnet msbuild $project.FullName -nologo -property:Configuration=Release '-getProperty:IsPackable,PackageId,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType'
    if ($LASTEXITCODE -ne 0) { throw 'Website metadata source evaluation failed.' }
    $properties = (($evaluation -join "`n") | ConvertFrom-Json -AsHashtable).Properties
    if ($properties.IsPackable -ne 'true') { continue }
    if ($properties.PackageId -cne $packageId) { throw 'Source package ID does not match the manifest.' }
    if (-not $sourcePackages.TryAdd($properties.PackageId, $properties)) { throw 'Duplicate source package ID.' }
    $sourceTags = Assert-WebsiteTags $properties.PackageTags $visibility $role 'Source PackageTags'
    Assert-WebsiteUrls $properties.PackageProjectUrl $properties.RepositoryUrl $properties.RepositoryType 'Source metadata'
}
if ($sourcePackages.Count -ne 1) { throw 'Manifest must match exactly one shipping source package.' }

foreach ($path in $PackagePath) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $path).Path)
    try {
        $nuspecs = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
        if ($nuspecs.Count -ne 1) { throw 'Package must contain exactly one nuspec.' }
        $reader = [IO.StreamReader]::new($nuspecs[0].Open())
        try { $xml = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = @($xml.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]'))
        if ($metadata.Count -ne 1) { throw 'Package must contain exactly one metadata element.' }
        $fields = @{}
        foreach ($field in @('id', 'tags', 'projectUrl', 'repository')) {
            $nodes = @($metadata[0].SelectNodes("*[local-name()='$field']"))
            if ($nodes.Count -ne 1) { throw "Package must contain exactly one $field element." }
            $fields[$field] = $nodes[0]
        }
        if ($fields.id.InnerText -cne $packageId) { throw 'Built package ID does not match the manifest/source.' }
        $builtTags = Assert-WebsiteTags $fields.tags.InnerText $visibility $role 'Built PackageTags'
        if ([string]::Join(';', $builtTags) -cne [string]::Join(';', $sourceTags)) {
            throw 'Built tags disagree with evaluated source PackageTags.'
        }
        Assert-WebsiteUrls $fields.projectUrl.InnerText $fields.repository.GetAttribute('url') $fields.repository.GetAttribute('type') 'Built metadata'
    } finally {
        $archive.Dispose()
    }
}
Write-Output "Website metadata passed: $packageId public-product/primary; archives=$($PackagePath.Count)."

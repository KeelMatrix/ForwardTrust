[CmdletBinding()]
param(
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path $PackageDirectory).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem

$fixedCorePropertiesName = 'package/services/metadata/core-properties/nuget.psmdcp'
$archivePaths = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' })
if ($archivePaths.Count -eq 0) {
    throw "No NuGet archives were found in $directory."
}

foreach ($archivePath in $archivePaths) {
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath.FullName)
    $temporaryPath = "$($archivePath.FullName).normalized"
    try {
        $coreEntries = @($archive.Entries | Where-Object { $_.FullName -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
        if ($coreEntries.Count -ne 1) {
            throw "Archive $($archivePath.Name) must contain exactly one core-properties entry."
        }

        $oldCoreName = $coreEntries[0].FullName
        $oldCoreId = [IO.Path]::GetFileNameWithoutExtension($oldCoreName)
        $outputStream = [IO.File]::Open($temporaryPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $normalized = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($entry in ($archive.Entries | Sort-Object FullName)) {
                $newName = if ($entry.FullName -eq $oldCoreName) { $fixedCorePropertiesName } else { $entry.FullName }
                $newEntry = $normalized.CreateEntry($newName, [IO.Compression.CompressionLevel]::Optimal)
                $newEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

                $inputStream = $entry.Open()
                $memory = [IO.MemoryStream]::new()
                try {
                    $inputStream.CopyTo($memory)
                    $bytes = $memory.ToArray()
                    if ($entry.FullName -match '\.(xml|rels)$' -or $entry.FullName -eq '[Content_Types].xml') {
                        $text = [Text.Encoding]::UTF8.GetString($bytes).Replace($oldCoreId, 'nuget')
                        if ($entry.FullName -eq '_rels/.rels') {
                            $text = [regex]::Replace(
                                $text,
                                '(?i)(<Relationship\b(?=[^>]*\bType="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties")(?=[^>]*\bId="[^"]+")[^>]*\bId=")R[0-9A-F]+(")',
                                '$1RcoreProperties$2')
                        }
                        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
                    }

                    $outputEntryStream = $newEntry.Open()
                    try { $outputEntryStream.Write($bytes, 0, $bytes.Length) }
                    finally { $outputEntryStream.Dispose() }
                }
                finally {
                    $inputStream.Dispose()
                    $memory.Dispose()
                }
            }
        }
        finally {
            $normalized.Dispose()
            $outputStream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    Move-Item -LiteralPath $temporaryPath -Destination $archivePath.FullName -Force
    Write-Output "Normalized deterministic archive: $($archivePath.Name)"
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

try
{
    if (args.Length < 3 || args.Skip(3).Any(a => a is not "--resolve-urls" and not "--self-test"))
    {
        throw new InvalidDataException("Usage: SourceLinkCheck <nupkg> <snupkg> <repository-root> [--resolve-urls] [--self-test]");
    }

    await CheckPackageAsync(args[0], args[1], Path.GetFullPath(args[2]),
        args.Contains("--resolve-urls", StringComparer.Ordinal), args.Contains("--self-test", StringComparer.Ordinal));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"SourceLink validation failed: {ex.Message}");
    return 1;
}

static byte[] ReadEntry(ZipArchive archive, string name)
{
    using Stream source = archive.Entries.Single(e => e.FullName == name).Open();
    using var buffer = new MemoryStream();
    source.CopyTo(buffer);
    return buffer.ToArray();
}

static byte[] Git(string root, params string[] arguments)
{
    var start = new ProcessStartInfo("git")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
    using var output = new MemoryStream();
    var error = process.StandardError.ReadToEndAsync();
    process.StandardOutput.BaseStream.CopyTo(output);
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException($"Git source inspection failed: {error.GetAwaiter().GetResult()}");
    }
    return output.ToArray();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidDataException(message);
    }
}

static void CheckHash(byte[] source, MetadataReader reader, Document document, string description)
{
    Require(reader.GetGuid(document.HashAlgorithm) == new Guid("8829d00f-11b8-4213-878b-770e8597ac16"), $"Expected SHA256 document checksum: {description}");
    Require(SHA256.HashData(source).AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash)), $"Document checksum mismatch: {description}");
}

static void RejectMutation(Action action, string expected)
{
    try
    {
        action();
    }
    catch (InvalidDataException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal))
    {
        return;
    }
    throw new InvalidDataException($"Negative control did not reject {expected}.");
}

static void CheckIdentity(byte[] assembly, byte[] pdb)
{
    using var pe = new PEReader(new MemoryStream(assembly));
    using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb));
    MetadataReader reader = provider.GetMetadataReader();
    DebugMetadataHeader header = reader.DebugMetadataHeader ?? throw new InvalidDataException("Missing portable PDB header.");
    var pdbId = new BlobContentId(header.Id);
    var entries = pe.ReadDebugDirectory();
    DebugDirectoryEntry codeViewEntry = entries.Single(e => e.Type == DebugDirectoryEntryType.CodeView);
    CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntry);
    Require(codeView.Guid == pdbId.Guid && codeViewEntry.Stamp == pdbId.Stamp && codeView.Age == 1, "DLL/PDB identity mismatch.");
    PdbChecksumDebugDirectoryData checksum = pe.ReadPdbChecksumDebugDirectoryData(entries.Single(e => e.Type == DebugDirectoryEntryType.PdbChecksum));
    Require(checksum.AlgorithmName == "SHA256", "Expected SHA256 PDB checksum.");
    byte[] zeroedPdb = (byte[])pdb.Clone();
    zeroedPdb.AsSpan(header.IdStartOffset, 20).Clear();
    Require(SHA256.HashData(zeroedPdb).AsSpan().SequenceEqual(checksum.Checksum.AsSpan()), "DLL/PDB checksum mismatch.");
}

static void CheckSource(byte[] source, MetadataReader reader, Document document, string name, bool selfTest)
{
    CheckHash(source, reader, document, name);
    if (selfTest)
    {
        byte[] changed = (byte[])source.Clone();
        Require(changed.Length > 0, $"Empty source in negative control: {name}");
        changed[0] ^= 1;
        RejectMutation(() => CheckHash(changed, reader, document, name), "Document checksum mismatch");
    }
}

static async Task CheckPackageAsync(string packagePath, string symbolsPath, string root, bool resolveUrls, bool selfTest)
{
    using var package = ZipFile.OpenRead(packagePath);
    using var symbols = ZipFile.OpenRead(symbolsPath);
    const string id = "KeelMatrix.ForwardTrust";
    var nuspec = XDocument.Load(new MemoryStream(ReadEntry(package, $"{id}.nuspec")));
    var repository = nuspec.Descendants().Single(e => e.Name.LocalName == "repository");
    string commit = repository.Attribute("commit")?.Value ?? "";
    string head = System.Text.Encoding.UTF8.GetString(Git(root, "rev-parse", "HEAD")).Trim();
    Require(commit == head, $"Package repository commit {commit} differs from checkout {head}.");
    Require(repository.Attribute("url")?.Value == "https://github.com/KeelMatrix/ForwardTrust", "Unexpected package repository URL.");
    string urlRoot = $"https://raw.githubusercontent.com/KeelMatrix/ForwardTrust/{commit}/";
    byte[] assembly = ReadEntry(package, $"lib/net8.0/{id}.dll");
    byte[] pdb = ReadEntry(symbols, $"lib/net8.0/{id}.pdb");
    CheckIdentity(assembly, pdb);
    using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb));
    MetadataReader reader = provider.GetMetadataReader();
    DebugMetadataHeader header = reader.DebugMetadataHeader ?? throw new InvalidDataException("Missing portable PDB header.");
    if (selfTest)
    {
        foreach (int offset in new[] { header.IdStartOffset, header.IdStartOffset + 16 })
        {
            byte[] changed = (byte[])pdb.Clone();
            changed[offset] ^= 1;
            RejectMutation(() => CheckIdentity(assembly, changed), "DLL/PDB identity mismatch");
        }
        byte[] corrupt = (byte[])pdb.Clone();
        corrupt[^1] ^= 1;
        RejectMutation(() => CheckIdentity(assembly, corrupt), "DLL/PDB checksum mismatch");
        using var pe = new PEReader(new MemoryStream(assembly));
        DebugDirectoryEntry codeViewEntry = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
        byte[] changedAge = (byte[])assembly.Clone();
        changedAge[codeViewEntry.DataPointer + 20] ^= 1;
        RejectMutation(() => CheckIdentity(changedAge, pdb), "DLL/PDB identity mismatch");
    }
    Console.WriteLine($"DLL/PDB identity and SHA256 checksum passed: {Convert.ToHexString(header.Id.AsSpan())}");

    Guid sourceLinkKind = new("cc110556-a091-4d38-9fec-25ab9a351a6a");
    Guid embeddedKind = new("0e8a571b-6926-466e-b4ad-8ab04611f5fe");
    CustomDebugInformation sourceLink = reader.CustomDebugInformation.Select(reader.GetCustomDebugInformation)
        .Single(info => reader.GetGuid(info.Kind) == sourceLinkKind);
    using var json = JsonDocument.Parse(reader.GetBlobBytes(sourceLink.Value));
    JsonProperty[] mappings = json.RootElement.GetProperty("documents").EnumerateObject().ToArray();
    string[] tracked = System.Text.Encoding.UTF8.GetString(Git(root, "ls-files", "-z", "--", "src/KeelMatrix.ForwardTrust/*.cs"))
        .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    var remaining = new HashSet<string>(tracked, StringComparer.Ordinal);
    int generated = 0;
    int resolved = 0;
    var errors = new List<string>();
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    foreach (DocumentHandle handle in reader.Documents)
    {
        Document document = reader.GetDocument(handle);
        string name = reader.GetString(document.Name);
        try
        {
            Require(name.StartsWith("/_/", StringComparison.Ordinal) && !name.Contains('\\'), $"Non-repository-root document path: {name}");
            string relative = name[3..];
            Require(!relative.Split('/').Any(p => p is ".." or "." or ""), $"Invalid document path: {name}");
            if (remaining.Remove(relative))
            {
                JsonProperty mapping = mappings.Single(m => m.Name.EndsWith('*') && name.StartsWith(m.Name[..^1], StringComparison.Ordinal));
                string template = mapping.Value.GetString() ?? "";
                Require(template.EndsWith('*'), $"Invalid source URL template: {name}");
                string url = template[..^1] + name[(mapping.Name.Length - 1)..];
                Require(url == urlRoot + relative, $"SourceLink URL is not the exact repository path: {name} -> {url}");
                CheckSource(Git(root, "show", $"{commit}:{relative}"), reader, document, relative, selfTest);
                // Also reject stale or locally modified build inputs, including checkout line-ending drift.
                CheckHash(File.ReadAllBytes(Path.Combine(root, relative)), reader, document, relative);
                if (resolveUrls)
                {
                    using HttpResponseMessage response = await client.GetAsync(url);
                    Require(response.IsSuccessStatusCode, $"Source URL returned {(int)response.StatusCode}: {url}");
                    CheckHash(await response.Content.ReadAsByteArrayAsync(), reader, document, url);
                    resolved++;
                }
                Console.WriteLine($"Tracked source passed: {name} -> {url}; SHA256={Convert.ToHexString(reader.GetBlobBytes(document.Hash))}");
            }
            else
            {
                Require(relative.StartsWith("src/KeelMatrix.ForwardTrust/obj/", StringComparison.Ordinal), $"Document is neither a tracked source nor a generated source: {name}");
                CustomDebugInformation[] embeddedRecords = reader.GetCustomDebugInformation(handle).Select(reader.GetCustomDebugInformation)
                    .Where(info => reader.GetGuid(info.Kind) == embeddedKind).ToArray();
                Require(embeddedRecords.Length == 1, $"Generated source must have one embedded source record: {name}");
                CustomDebugInformation embedded = embeddedRecords[0];
                byte[] blob = reader.GetBlobBytes(embedded.Value);
                Require(blob.Length >= 4, $"Incomplete embedded source: {name}");
                int length = BinaryPrimitives.ReadInt32LittleEndian(blob);
                byte[] source;
                if (length == 0)
                {
                    source = blob[4..];
                }
                else
                {
                    Require(length > 0, $"Invalid embedded source size: {name}");
                    using var compressed = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    compressed.CopyTo(output);
                    source = output.ToArray();
                    Require(source.Length == length, $"Embedded source size mismatch: {name}");
                }
                CheckSource(source, reader, document, name, selfTest);
                generated++;
                Console.WriteLine($"Embedded generated source passed: {name}; encoding={(length == 0 ? "raw" : "deflate")}; bytes={source.Length}; SHA256={Convert.ToHexString(reader.GetBlobBytes(document.Hash))}");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{name}: {ex.Message}");
        }
    }
    Require(errors.Count == 0 && remaining.Count == 0 && tracked.Length > 0 && generated > 0,
        $"Source document coverage failed. Missing tracked documents: {string.Join(", ", remaining)}\n{string.Join("\n", errors)}");
    Console.WriteLine($"SourceLink passed: tracked={tracked.Length}; embedded={generated}; HTTP/checksum verified={resolved}; commit={commit}");
    if (selfTest)
    {
        Console.WriteLine($"Negative controls passed: GUID, stamp, age, PDB checksum, and all {tracked.Length + generated} document checksums.");
    }
}

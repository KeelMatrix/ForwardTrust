# ForwardTrust Development

## Prerequisites

Use the .NET SDK selected by `global.json`. The shipping library uses the ASP.NET Core 8 shared framework; tests use the .NET 8 `Microsoft.AspNetCore.TestHost` package.

## Local validation

Run from the repository root:

```powershell
dotnet restore KeelMatrix.ForwardTrust.sln --configfile NuGet.config --force
dotnet build KeelMatrix.ForwardTrust.sln -c Release --no-restore
dotnet test KeelMatrix.ForwardTrust.sln -c Release --no-build --logger "console;verbosity=minimal"
dotnet format KeelMatrix.ForwardTrust.sln --verify-no-changes --no-restore
```

The full package gate runs the same restore/build/test/format path, packs the exact product, normalizes the generated NuGet core-properties entry, inspects both archives and their source/symbol contract, runs the fail-closed JSON vulnerability audit and its synthetic clean/vulnerable fixtures, checks repeated pack byte identity, exercises the changelog and SourceLink contract tests, and restores the consumer from an isolated local feed:

```powershell
pwsh -NoProfile -File scripts/package-gate.ps1
```

The package consumer intentionally uses `PackageReference`, not `ProjectReference`. It captures remaining XFF alongside the identity and prints trusted, untrusted and coincident-address passes, an identity-only coincident-address failure, the executed-request sender contract, and the structured `UntrustedHeaderAccepted` failure from an unsafe boundary.

The package gate executes the packed README installation commands in a pristine `net8.0` xUnit project with an isolated cache and package-source mapping, then executes its published test for trusted and untrusted scenarios.

The package gate checks that nested test, shipping, and smoke-project `Properties/launchSettings.json` files are ignored. Its self-test also removes the rule in an isolated temporary repository and confirms the guard fails closed.

The vulnerability audit has no build/test-only exception path: any package advisory in the JSON report fails the gate. The repeat-pack check compares normalized `.nupkg` and `.snupkg` hashes from the same Release inputs.

## Source and symbol validation

`scripts/validate-package.ps1` is the shared package-validation entry point for local development, three-OS CI, and the Linux release workflow. Its non-packable `tools/SourceLinkCheck` helper reads the DLL and portable PDB directly from the archives. Every tracked shipping C# document must retain its repository-relative path, resolve through SourceLink to the exact package commit, and match both the Git blob and local build input by SHA256. Every generated PDB document must have an embedded source whose decoded bytes match its checksum. The DLL's CodeView GUID, stamp and age must match the PDB, and its PDB checksum must match the complete PDB with the ID zeroed.

The SDK derives deterministic compiler paths and SourceLink paths from the same repository source roots. Do not override `PathMap` with a project-directory mapping: it removes the repository subdirectory from source URLs. Keep tracked source line endings consistent with Git through `.gitattributes`.

After the solution build and pack, run focused checks with:

```powershell
pwsh -NoProfile -File scripts/validate-package.ps1 -PackageDirectory artifacts/packages -ExpectedVersion 0.1.1
pwsh -NoProfile -File scripts/Test-SourceLinkContract.ps1
```

The regression builds separate negative artifacts with the original project-relative mapping and with generated-source embedding disabled. Both must fail the shared package validator, including its child-process exit propagation. Mutation controls reject altered GUIDs, stamps, ages, PDB checksums, and the content of every tracked and embedded source document. These controls never change shipping outputs.

Once the candidate commit is pushed, add `-ResolveSourceUrls` to `validate-package.ps1` or `package-gate.ps1`. This also fetches every tracked source URL and checks its HTTP response bytes against the PDB checksum. Ordinary CI enables it on Windows, Linux and macOS; the Linux release path requires it before authentication or publication. Local checks before a push use the committed Git blobs without requiring unpublished URLs to exist. These are build-time checks; the library's runtime network behavior is unchanged.

## Release preparation

`scripts/validate-changelog.ps1` is the shared lifecycle entry point for the package gate and release workflow. Automatic selection accepts an unreleased target, a finalized commit before tagging, the exact tagged commit, or development after a published tag. Only the exact release commit selects `Tagged`. Finalized states require a clean tree and consistent package version, changelog date, commit date and tag/SHA. An unreleased or planned target cannot pass the tagged release path.

`Development` keeps the package version unchanged and requires the matching published tag to be an ancestor of the current commit. It preserves the complete published changelog history while permitting notes under `Unreleased`. It identifies the output as a development package, not the published release. Both workflows fetch complete history and tags so selection does not depend on a shallow checkout. Development packages still undergo all source, symbol, archive and consumer checks; this state cannot prepare or publish an already used version.

Run the inexpensive lifecycle stage or its isolated fixtures independently:

```powershell
pwsh -NoProfile -File scripts/package-gate.ps1 -Stage Changelog
pwsh -NoProfile -File scripts/test-changelog-contract.ps1 -ExpectedVersion 0.1.1 -ExpectedPackageVersion 0.1.1
```

While editing an exact tagged checkout, use `-FixturesOnly` on `test-changelog-contract.ps1` to run disposable lifecycle regressions without validating the dirty checkout as a release. The full package gate always validates the actual checkout and all fixtures.

For a later release, first select a new version and finalize its notes in a separate release-preparation change. After changelog finalization, validate the exact pre-tag candidate (substitute that new version and unused tag):

```powershell
$releaseCommit = (git rev-parse HEAD).Trim()
$releaseDate = (git show -s --format=%cs $releaseCommit).Trim()
pwsh -NoProfile -File scripts/validate-changelog.ps1 -State Finalized -ExpectedVersion 0.1.1 -ExpectedPackageVersion 0.1.1 -ExpectedTag v0.1.1 -ExpectedCommit $releaseCommit -ExpectedReleaseDate $releaseDate -RequireIcon
```

The tag workflow uses the same entry point with `-State Tagged`, requiring the tag to point at the exact commit. Contract fixtures exercise the actual package-gate lifecycle stage and workflow lifecycle commands, first and subsequent releases, development with LF/CRLF checkouts, published-history preservation, invalid version/date/SHA/tag, heading uniqueness and changes stranded under `Unreleased`, including child-process failure propagation. A tagged planned target and a development commit both fail the publication check. Fixture tags exist only in disposable repositories. The package gate creates no product tags and publishes no packages.

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

The full package gate runs the same restore/build/test/format path, packs the exact product, normalizes the generated NuGet core-properties entry, inspects both archives, runs the fail-closed JSON vulnerability audit and its synthetic clean/vulnerable fixtures, checks repeated pack byte identity, exercises the changelog contract tests, and restores the consumer from an isolated local feed:

```powershell
pwsh -NoProfile -File scripts/package-gate.ps1
```

The package consumer intentionally uses `PackageReference`, not `ProjectReference`. It prints trusted and untrusted passes, the explicit executed-request sender contract, and the structured `UntrustedHeaderAccepted` failure from an unsafe boundary.

The package gate also extracts the packed README Quick start code and compiles it in a fresh isolated `PackageReference` consumer, so the published example is checked as shipped.

The package gate checks that nested test, shipping, and smoke-project `Properties/launchSettings.json` files are ignored. Its self-test also removes the rule in an isolated temporary repository and confirms the guard fails closed.

The vulnerability audit has no build/test-only exception path: any package advisory in the JSON report fails the gate. The repeat-pack check compares normalized `.nupkg` and `.snupkg` hashes from the same Release inputs.

## Release preparation

The package gate validates the changelog before release finalization: the current package version stays under one leading `## [Unreleased]` section until finalization. The release validator checks the exact finalization commit, commit date, changelog date, package version, and intended tag before tag creation. On a tag-triggered run, `-RequireTag` also verifies that the tag points to that exact commit.

```powershell
pwsh -NoProfile -File scripts/test-changelog-contract.ps1 -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
$releaseCommit = (git rev-parse HEAD).Trim()
$releaseDate = (git show -s --format=%cs $releaseCommit).Trim()
pwsh -NoProfile -File scripts/validate-release.ps1 -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0 -ExpectedTag v0.1.0 -ExpectedCommit $releaseCommit -ExpectedReleaseDate $releaseDate -RequireIcon
```

The contract test covers first and subsequent releases, candidate versus finalized state, date/version/tag mismatches, an absent tag in tag mode, tags pointing at another commit, and changes left under `Unreleased`. The release workflow remains tag-only and uses `-RequireTag`. The package gate does not create tags or publish packages.

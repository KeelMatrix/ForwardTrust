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

The package gate executes the packed README installation commands in a pristine `net8.0` xUnit project with an isolated cache and package-source mapping, then executes its published test for trusted and untrusted scenarios.

The package gate checks that nested test, shipping, and smoke-project `Properties/launchSettings.json` files are ignored. Its self-test also removes the rule in an isolated temporary repository and confirms the guard fails closed.

The vulnerability audit has no build/test-only exception path: any package advisory in the JSON report fails the gate. The repeat-pack check compares normalized `.nupkg` and `.snupkg` hashes from the same Release inputs.

## Release preparation

`scripts/validate-changelog.ps1` is the shared lifecycle entry point for the package gate and release workflow. Automatic selection accepts an unreleased candidate, a finalized commit before tagging, or the exact tagged commit. Finalized states require a clean tree and consistent package version, changelog date, commit date and tag/SHA. An unreleased target cannot pass the tagged release path.

Run the inexpensive lifecycle stage or its isolated fixtures independently:

```powershell
pwsh -NoProfile -File scripts/package-gate.ps1 -Stage Changelog
pwsh -NoProfile -File scripts/test-changelog-contract.ps1 -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
```

After changelog finalization, validate the exact pre-tag candidate:

```powershell
$releaseCommit = (git rev-parse HEAD).Trim()
$releaseDate = (git show -s --format=%cs $releaseCommit).Trim()
pwsh -NoProfile -File scripts/validate-changelog.ps1 -State Finalized -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0 -ExpectedTag v0.1.0 -ExpectedCommit $releaseCommit -ExpectedReleaseDate $releaseDate -RequireIcon
```

The tag workflow uses the same entry point with `-State Tagged`, requiring the tag to point at the exact commit. Contract fixtures exercise the actual package-gate lifecycle stage, first and subsequent releases, invalid version/date/SHA/tag, heading uniqueness and changes stranded under `Unreleased`, including child-process failure propagation. Fixture tags exist only in disposable repositories. The package gate creates no product tags and publishes no packages.

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

The full package gate runs the same restore/build/test/format path, packs the exact product, inspects both archives, runs the vulnerability audit, exercises the changelog contract tests, and restores the consumer from an isolated local feed:

```powershell
pwsh -NoProfile -File scripts/package-gate.ps1
```

The package consumer intentionally uses `PackageReference`, not `ProjectReference`. It prints trusted and untrusted passes, the explicit executed-request sender contract, and the structured `UntrustedHeaderAccepted` failure from an unsafe boundary.

The package gate also extracts the packed README Quick start code and compiles it in a fresh isolated `PackageReference` consumer, so the published example is checked as shipped.

## Release preparation

The repository-controlled validator is the single changelog/version contract used before tagging and by the tag-triggered release workflow:

```powershell
pwsh -NoProfile -File scripts/test-changelog-contract.ps1 -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
```

No tag or publication is part of local validation. GitHub Actions is intentionally manual-only for CI and tag-only for release while the repository is private.

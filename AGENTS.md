# ForwardTrust Development Guide

## Navigation

- `src/KeelMatrix.ForwardTrust` contains the shipping verifier and immutable scenario/result contracts.
- `tests/KeelMatrix.ForwardTrust.Tests` contains unit, ASP.NET Core TestServer, security/redaction, cancellation, and API-surface tests.
- `smoke/ForwardTrust.Consumer` is a non-packable PackageReference consumer used against the built package.
- `docs/compatibility.md` records the supported net8.0 servicing facts and peer-simulation seam.
- `scripts/` contains the package gate and changelog/version contract checks.

## Commands

```powershell
dotnet restore KeelMatrix.ForwardTrust.sln
dotnet build KeelMatrix.ForwardTrust.sln -c Release --no-restore
dotnet test KeelMatrix.ForwardTrust.sln -c Release --no-build
dotnet format KeelMatrix.ForwardTrust.sln --verify-no-changes
dotnet pack src/KeelMatrix.ForwardTrust/KeelMatrix.ForwardTrust.csproj -c Release --no-build -o artifacts/packages
pwsh -NoProfile -File scripts/test-changelog-contract.ps1 -ExpectedVersion 0.1.0 -ExpectedPackageVersion 0.1.0
pwsh -NoProfile -File scripts/package-gate.ps1
```

## Invariants

- The only shipping package is `KeelMatrix.ForwardTrust`, targeting `net8.0`.
- Verification is read-only with respect to the caller's application configuration; it never rewrites `ForwardedHeadersOptions`.
- Scenario validation is complete before any request executes.
- No external network, DNS, cloud metadata, or telemetry call is part of v1.
- Diagnostics never include cookies, bearer tokens, arbitrary response bodies, or unrelated headers.
- Trusted proxy/network configuration remains the application's responsibility; broad trust is never a recommended fix.
- The repository-root `icon.png` is founder-owned and must not be created, copied, edited, or removed by engineering work.

## Validation strategy

Start with the matching test class for a verifier change, then run the test project, Release build, format verification, pack/archive inspection, vulnerability audit, and the isolated package-consumer smoke. Keep package artifacts under `artifacts/`, which is disposable and ignored.

# ForwardTrust Development Guide

## Navigation

- `src/KeelMatrix.ForwardTrust` contains the shipping verifier and immutable scenario/result contracts. Public scenario and failure collections are read-only defensive snapshots.
- `tests/KeelMatrix.ForwardTrust.Tests` contains unit, ASP.NET Core TestServer, security/redaction, cancellation, and API-surface tests.
- `smoke/ForwardTrust.Consumer` is a non-packable PackageReference consumer used against the built package.
- `docs/compatibility.md` records the supported net8.0 servicing facts and peer-simulation seam.
- `scripts/` contains the package gate and changelog/version contract checks.
- `keelmatrix.website.json` classifies the public primary package; `scripts/validate-website-metadata.ps1` checks evaluated source and archive metadata.
- `tools/SourceLinkCheck` is a non-packable archive verifier used by the shared package gate; see `docs/DEV.md` for source and symbol checks.

## Commands

```powershell
dotnet restore KeelMatrix.ForwardTrust.sln
dotnet build KeelMatrix.ForwardTrust.sln -c Release --no-restore
dotnet test KeelMatrix.ForwardTrust.sln -c Release --no-build
dotnet format KeelMatrix.ForwardTrust.sln --verify-no-changes
dotnet pack src/KeelMatrix.ForwardTrust/KeelMatrix.ForwardTrust.csproj -c Release --no-build -o artifacts/packages
pwsh -NoProfile -File scripts/test-changelog-contract.ps1 -ExpectedVersion 0.1.1 -ExpectedPackageVersion 0.1.1
pwsh -NoProfile -File scripts/package-gate.ps1
pwsh -NoProfile -File scripts/package-gate.ps1 -Stage WebsiteMetadata
pwsh -NoProfile -File scripts/Test-WebsiteMetadata.ps1
```

## Invariants

- The only shipping package is `KeelMatrix.ForwardTrust`, targeting `net8.0`.
- Website classification is `public-product`/`primary` in the root manifest and exact lowercase NuGet sentinels. Project and repository URLs are `https://github.com/KeelMatrix/ForwardTrust`.
- Verification is read-only with respect to the caller's application configuration; it never rewrites `ForwardedHeadersOptions`.
- Scenario validation is complete before any request executes.
- Accepted expectations correspond to original forwarded values in every asserted dimension; controls prove a reproducible change at an effective consumed hop, including partial chains.
- Original host proof uses the accepted path's own baseline; indistinguishable overwritten baselines remain unproven.
- Original address proof uses repeatable remaining-XFF evidence from the unchanged original, with a strict valid consumed suffix bound to its effective causal hop index. Identity-only proof omits only XFF for its replayed accepted baseline; coincident addresses remain unproven. Scheme, Host and changed-request commits never substitute for original address application.
- Address-dependent scheme/host traversal requires replayed truncated pairs that retain the original address and reproduce the full counterfactual identity; non-target differences are never ignored.
- Failed host normalization excludes only that candidate; malformed IDN siblings cannot exclude a valid effective-hop witness, and normalization never substitutes for middleware acceptance.
- Per-request deadlines include synchronous invocation and asynchronous completion. Caller cancellation is checked between requests; arbitrary caller code cannot be forcibly interrupted.
- Each `ForwardTrustRequest` snapshots its immediate-peer address and returns a defensive copy, so sender mutations cannot change later probe inputs.
- No external network, DNS, cloud metadata, or telemetry call is part of v1.
- Diagnostics never include cookies, bearer tokens, arbitrary response bodies, or unrelated headers.
- `ForwardLimitMismatch` requires a failed asserted dimension whose own forwarded-header chain contains multiple values.
- Trusted proxy/network configuration remains the application's responsibility; broad trust is never a recommended fix.
- The package includes `icon.png` from the repository root; it must be a 512x512 PNG no larger than 200 KB.
- The shared changelog validator distinguishes development after publication from the exact tagged release; preserve published history and keep release validation strict. See `docs/DEV.md`.

## Validation strategy

Start with the matching test class for a verifier change, then run the test project, Release build, format verification, pack/archive inspection, vulnerability audit, and the isolated package-consumer smoke. Keep package artifacts under `artifacts/`, which is disposable and ignored.

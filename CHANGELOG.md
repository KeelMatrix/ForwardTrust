# Changelog

All notable changes to KeelMatrix.ForwardTrust are documented here.

## [Unreleased]

No unreleased changes.

## [0.1.0] - 2026-09-24

### Added

- Executable trusted and untrusted forwarded-header scenarios over a caller-provided ASP.NET Core test host.
- Structured scheme, host, client-address, forward-limit, malformed-scenario, and host/probe diagnostics.
- Bounded, deterministic, offline verification with a net8.0 package and package-consumer smoke path.

### Changed

- Verification now requires load-bearing peer-seam controls and counterfactual attribution for every asserted forwarded dimension; coincidental defaults are reported as unproven instead of passing.
- Release-equivalent package validation now checks the complete `.nupkg`/`.snupkg` artifact set and fails closed when the founder-owned icon is absent.

# Changelog

All notable changes to KeelMatrix.ForwardTrust are documented here.

## [Unreleased]

No unreleased changes.

## [0.1.0] - 2026-09-24

### Added

- Executable ASP.NET Core forwarded-header scenarios for trusted and untrusted peers, multi-hop forwarding, schemes, hosts, client addresses, malformed headers, and forward limits through a caller-provided test host.
- Structured results that identify scenario names, trust dimensions, expected identities, observed identities, and bounded host/probe failures while excluding cookies, authorization data, arbitrary response bodies, and unrelated headers.
- Conservative, finite, deterministic `net8.0` verification with pre-request scenario validation, scenario-local accepted/rejected controls, counterfactual attribution, bounded request timeouts, and cancellation support.
- Offline operation with no external network, DNS, cloud-metadata, or telemetry calls, plus a NuGet package-consumer smoke path and exact package artifact validation.

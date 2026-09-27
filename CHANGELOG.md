# Changelog

All notable changes to KeelMatrix.ForwardTrust are documented here.

## [Unreleased]

### Changed

- Bounded scenario intake and header validation now reject empty, oversized, lazy-over-limit, and invalid HTTP input before invoking the caller-provided sender; forward-limit diagnostics use the maximum forwarded chain depth.
- Local and release package gates emit a JSON vulnerability report, fail closed on any reported advisory, normalize NuGet core-properties entries, and require byte-identical repeated `.nupkg` and `.snupkg` output.

## [0.1.0] - 2026-09-24

### Added

- Executable ASP.NET Core forwarded-header scenarios for trusted and untrusted peers, multi-hop forwarding, schemes, hosts, client addresses, malformed headers, and forward limits through a caller-provided test host.
- Structured results that identify scenario names, trust dimensions, expected identities, observed identities, and bounded host/probe failures while excluding cookies, authorization data, arbitrary response bodies, and unrelated headers.
- Conservative, finite `net8.0` verification with pre-request scenario validation, scenario-local accepted/rejected controls, exact and repeatable counterfactual observations, bounded request timeouts, and cancellation support under the explicit contract that the caller-provided sender executes every request against the application pipeline; deliberately fabricated, self-consistent senders are outside the detectable boundary.
- Offline operation with no external network, DNS, cloud-metadata, or telemetry calls, plus a NuGet package-consumer smoke path and exact package artifact validation.

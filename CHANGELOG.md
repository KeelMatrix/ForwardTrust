# Changelog

All notable changes to KeelMatrix.ForwardTrust are documented here.

## [Unreleased]

### Added

- Executable ASP.NET Core forwarded-header scenarios for trusted and untrusted peers, multi-hop forwarding, schemes, hosts, client addresses, malformed headers, and forward limits through a caller-provided test host.
- Structured results identify scenario names, trust dimensions, expected identities, observed identities, and bounded host/probe failures without exposing cookies, authorization data, arbitrary response bodies, or unrelated headers.
- Finite `net8.0` verification validates the complete bounded scenario set before requests run, rejects case-insensitive duplicate header names, requires scenario-local accepted/rejected controls, and checks counterfactual effects only in the targeted identity dimension while preserving every non-target field.
- Bounded request timeouts, cancellation support, and an offline verdict path require no external network, DNS, cloud-metadata, or telemetry calls.

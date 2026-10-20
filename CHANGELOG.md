# Changelog

All notable changes to KeelMatrix.ForwardTrust are documented here.

## [Unreleased]

### Added

- Executable ASP.NET Core forwarded-header scenarios for trusted and untrusted peers, multi-hop forwarding, schemes, hosts, client addresses, malformed headers, and forward limits through a caller-provided test host.
- Structured results identify scenario names, trust dimensions, expected identities, observed identities, and bounded host/probe failures without exposing cookies, authorization data, arbitrary response bodies, or unrelated headers.
- Finite `net8.0` verification validates the complete bounded scenario set and each generated counterfactual request before any request runs, rejects case-insensitive duplicate header names, requires scenario-local accepted/rejected controls, and checks counterfactual effects only in the targeted identity dimension while preserving every non-target field.
- Host probes accept a caller-selected ASCII counterfactual value that can follow the application's existing host allowlist (internationalized names use punycode), and `ForwardedIdentity` snapshots and returns copies of client addresses so IPv6 scope changes cannot mutate an expectation.
- Caller-provided host/probe requests use a five-second default timeout and accept any positive value up to two minutes; zero, negative, and over-two-minute values are rejected. A request that exceeds its timeout returns `HostProbeFailure`. Verification also supports caller cancellation and an offline verdict path with no external network, DNS, cloud-metadata, or telemetry calls.

### Fixed

- Generated counterfactual requests are checked against the same per-value and aggregate UTF-8 header limits as the original scenario. An over-limit generated request returns `MalformedScenario` before any sender call.

- Scenarios without an asserted forwarded dimension now fail before the request sender runs, for both accepted and rejected expectations.
- A rejected primary host assertion now requires its accepted control to declare the original forwarded host before default or custom counterfactuals can establish causality; application host-policy rejection remains a failure.
- Aggregate, per-scenario, and scenario-result collections are read-only defensive snapshots, so callers cannot mutate a returned result into contradictory verdicts.
- `ForwardLimitMismatch` now requires the failed asserted dimension itself to contain multiple forwarded values; unrelated identity failures and unasserted host chains no longer produce the diagnostic.
- Request senders receive an isolated immediate-peer address on every `ForwardTrustRequest` read, so mutating an IPv6 scope ID cannot change later original, counterfactual, or replay inputs.

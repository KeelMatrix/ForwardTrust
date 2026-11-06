# Changelog

All notable changes to KeelMatrix.ForwardTrust are documented here.

## [Unreleased]

### Added

- Executable ASP.NET Core forwarded-header scenarios for trusted and untrusted peers, multi-hop forwarding, schemes, hosts, client addresses, malformed headers, and forward limits through a caller-provided test host.
- Structured results identify scenario names, trust dimensions, expected identities, observed identities, and bounded host/probe failures without exposing cookies, authorization data, arbitrary response bodies, or unrelated headers.
- Finite `net8.0` verification validates original-value correspondence and every generated counterfactual before any request runs, rejects case-insensitive duplicate header names, and requires scenario-local accepted/rejected controls. Per-dimension controls support effective partial-chain values, forward limits and trust stops while preserving every non-target identity field.
- Host probes accept a caller-selected ASCII counterfactual value that can follow the application's existing host allowlist (internationalized names use punycode). Replayed ordinary-host controls distinguish original host application from a matching baseline. `ForwardedIdentity` snapshots and returns copies of client addresses so IPv6 scope changes cannot mutate an expectation.
- Caller-provided host/probe requests use a five-second default timeout and accept any positive value up to two minutes; zero, negative, and over-two-minute values are rejected. The deadline includes synchronous invocation and asynchronous completion, and late results return `HostProbeFailure`. Caller cancellation stops further requests when control returns; arbitrary sender code cannot be forcibly interrupted. The verdict path makes no external network, DNS, cloud-metadata, or telemetry calls.

### Fixed

- Generated counterfactual requests are checked against the same per-value and aggregate UTF-8 header limits as the original scenario. An over-limit generated request returns `MalformedScenario` before any sender call.

- Scenarios without an asserted forwarded dimension now fail before the request sender runs, for both accepted and rejected expectations.
- A rejected primary host assertion now requires its accepted control to declare the original forwarded host before default or custom counterfactuals can establish causality; application host-policy rejection remains a failure.
- Aggregate, per-scenario, and scenario-result collections are read-only defensive snapshots, so callers cannot mutate a returned result into contradictory verdicts.
- `ForwardLimitMismatch` now requires the failed asserted dimension itself to contain multiple forwarded values; unrelated identity failures and unasserted host chains no longer produce the diagnostic.
- Request senders receive an isolated immediate-peer address on every `ForwardTrustRequest` read, so mutating an IPv6 scope ID cannot change later original, counterfactual, or replay inputs.

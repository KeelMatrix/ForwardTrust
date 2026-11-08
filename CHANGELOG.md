# Changelog

All notable changes to KeelMatrix.ForwardTrust are documented here.

## [Unreleased]

## [0.1.0] - 2026-11-08

### Added

- Provides .NET 8 integration-test scenarios for ASP.NET Core forwarded-header trust, covering trusted and untrusted peers, client addresses, schemes, optional hosts, and multi-hop chains stopped by forward limits or trust boundaries.
- Checks original forwarded-value application in each asserted dimension with opposite-trust controls, exact counterfactual values, and repeat requests through a caller-provided sender that executes every request against the application pipeline; ambiguous or non-causal results remain unproven.
- Validates bounded scenario inputs and generated headers before sending requests, and returns immutable scenario results with structured, bounded failure diagnostics that exclude cookies, authorization data, response bodies, and unrelated headers.
- Supports caller cancellation and a configurable per-request deadline that includes synchronous invocation and asynchronous completion, defaulting to five seconds with a two-minute maximum; arbitrary sender code cannot be forcibly interrupted.
- Leaves application trust settings unchanged and makes no external network, DNS, cloud-metadata, or telemetry calls; in-process verification does not establish the configuration of production proxies or network infrastructure.

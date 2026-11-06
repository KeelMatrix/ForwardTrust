# ForwardTrust Compatibility

This note records the current platform facts that shape the v1 contract. It is intentionally short and is not a replacement for the linked primary documentation.

## Supported runtime

ForwardTrust ships only a `net8.0` asset and is tested against the ASP.NET Core 8 shared framework. On 2026-11-06, .NET 8 was in maintenance support at patch `8.0.33`, with support listed through 2026-11-10. The documented setup and repository consumers select [TestHost 8.0.33](https://www.nuget.org/packages/Microsoft.AspNetCore.TestHost/8.0.33), which targets `net8.0`. Consumers should keep the .NET 8 servicing line current.

## Forwarded-header behavior

ASP.NET Core 8.0.17 introduced hardening that ignores `X-Forwarded-*` values from unknown proxies. The v1 fixtures use `ForwardedHeadersOptions.KnownProxies`, `KnownNetworks`, and `ForwardLimit`; on net8.0, `ForwardLimit` defaults to `1`, and headers are processed right-to-left. ForwardTrust does not alter any of those options.

Each scenario with an asserted forwarded dimension declares its own opposite-trust peer and expected identity. Accepted expectations must correspond to original values in each asserted dimension; contradictory definitions return `MalformedScenario` before sending. Controls replace matching original tokens, testing repeated matches from right to left until an effective-hop witness passes. This supports partial chains, unequal lengths, and processing stopped by a limit or trust boundary. IP endpoint ports are ignored for address comparison and IPv4-mapped IPv6 addresses compare equivalently.

Host correspondence uses the shared framework's normalization. Malformed punycode decoding or Unicode encoding failures exclude only that token from original-value witnesses; a different valid effective-hop witness remains eligible. If no original matches, validation returns `MalformedScenario` before sending. Normalizable originals still require middleware acceptance and causal proof under the application's host policy.

Each hop trial sends original and counterfactual requests through both peers, then reissues each identical request. The accepted path must match the exact generated value, or the caller-selected value for a host probe. The rejected path must preserve its full identity. Ordinarily the accepted counterfactual must preserve every non-target identity field. If changing the address stops later scheme/host processing, additional controls remove only the dependent header prefixes earlier than that address hop. The truncated originals must retain their original addresses, the rejected path must remain unchanged, and the truncated counterfactuals must reproduce the full counterfactual identities. Within the truncated pair, only the target address may change. Stable unrelated rewrites and unstable replays remain failures. This measures the traversal dependency without changing application trust.

Original host application is compared with the accepted path's own replayed baseline with `X-Forwarded-Host` omitted, regardless of the other peer's host. If the original host and baseline coincide, ordinary `Host` controls on that same accepted path must move the baseline to the alternate host while leaving the identity with the original forwarded host unchanged. If application middleware overwrites that baseline control, the original remains unproven, even when it was actually consumed. A host probe can set `ForwardTrustScenario.CounterfactualHost` to a distinct ASCII value accepted by the application's existing host policy; use punycode for internationalized names. ForwardTrust does not change that policy. All potential generated controls share the scenario's header bounds, including an added or replaced `Host` header. Omission and prefix truncation only reduce those validated headers.

`ForwardedIdentity` copies its `IPAddress` at construction and when `ClientAddress` is read. Mutating an input or returned IPv6 address therefore cannot change the expected identity stored by a scenario.

`ForwardTrustRequest` snapshots its simulated immediate-peer address and returns a defensive copy from `ImmediatePeerAddress` on every read. Sender code therefore cannot mutate the verifier's address state between original, counterfactual, and replay probes, including by changing an IPv6 scope ID.

Forwarded hop depth is the maximum comma-separated chain length in any asserted forwarded dimension, not the sum across `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host`. For example, a two-hop scenario can use `X-Forwarded-For: 198.51.100.10, 10.0.0.10`, `X-Forwarded-Proto: https, http`, and `X-Forwarded-Host: public.example, internal.example` with the application's `ForwardLimit` set to `2`; a one-hop request with one value in each field is not treated as a three-hop request.

The verifier reports `ForwardLimitMismatch` only when a failed asserted dimension has more than one value in its own forwarded-header chain. A mismatch on a different dimension, or a multi-hop `X-Forwarded-Host` header without an asserted host, does not produce this diagnostic.

The verifier requires at least one scenario, stops lazy scenario intake at the first item beyond `MaxScenarioCount`, and bounds each scenario to 64 headers, 64 KiB of aggregate UTF-8 header-name/value bytes, 256-character names, and 16 KiB values. Header names use HTTP token syntax, case-insensitive duplicate names are rejected rather than collapsed, and field values reject control characters other than horizontal tab and legal visible/obs-text characters. Each generated counterfactual is checked against the same value and aggregate limits; any violation is reported as `MalformedScenario` before a caller-provided sender runs.

Each scenario must also assert at least one supported forwarded dimension. `X-Forwarded-For` and `X-Forwarded-Proto` are active when present; `X-Forwarded-Host` is active only when the expected identity includes a host. A scenario without an active dimension returns `ForwardedValueNotProven` before the caller-provided sender runs. An unasserted host remains optional when another dimension is active.

When the primary expectation is `Rejected` and host is asserted, the accepted control must declare the expected host for the original forwarded value. If it does not, validation returns `RequestApplicationNotProven` before any sender call; a default or custom counterfactual cannot supply that missing original-host proof. If the application rejects the original host through its host policy, the accepted control fails its expected-host assertion even when the counterfactual host is allowed.

`ForwardTrustResult.Scenarios`, aggregate failures, and per-scenario failures are read-only defensive snapshots. A caller cannot mutate a returned collection to make aggregate and per-scenario verdicts disagree.

## Cancellation and deadlines

`ForwardTrustVerifierOptions.RequestTimeout` defaults to five seconds and must be positive and no greater than two minutes. Its monotonic deadline includes synchronous invocation and asynchronous completion. Late results return `HostProbeFailure`; caller cancellation is checked before each invocation and after completion and propagates `OperationCanceledException`. Arbitrary caller code cannot be forcibly interrupted, so synchronously blocking work is checked when it returns. The API XML is the source of truth for this contract.

## Test-host peer seam

The supported seam is caller-owned: `TestServer.SendAsync(Action<HttpContext>, CancellationToken)` can assign `HttpContext.Connection.RemoteIpAddress` before the real pipeline runs. ForwardTrust exposes only a framework-neutral request/probe delegate, so consumers may use TestServer or their own test host without a runtime TestHost dependency.

A verdict assumes that delegate executes every supplied request against the application pipeline. Replays and counterfactuals detect observable non-execution and non-causal drift, but no black-box verifier can distinguish a real pipeline from a deliberately fabricated sender that returns the same self-consistent identity for every generated request.

## Primary sources

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [.NET 8 downloads and servicing](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- [Unknown-proxy forwarded-header hardening](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/8.0/forwarded-headers-unknown-proxies)
- [ASP.NET Core proxy and load-balancer configuration](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-8.0)
- [`TestServer.SendAsync`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.testhost.testserver.sendasync)

Package and repository name availability was checked immediately before repository creation: the NuGet registration endpoint returned 404 for `KeelMatrix.ForwardTrust`, and `KeelMatrix/ForwardTrust` did not yet exist. Availability must be checked again before publication.

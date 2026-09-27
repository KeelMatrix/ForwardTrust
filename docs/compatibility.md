# ForwardTrust Compatibility

This note records the current platform facts that shape the v1 contract. It is intentionally short and is not a replacement for the linked primary documentation.

## Supported runtime

ForwardTrust ships only a `net8.0` asset and is tested against the ASP.NET Core 8 shared framework. On 2026-09-24, .NET 8 was in maintenance support at patch `8.0.31`, with support listed through 2026-11-10. Consumers should keep the .NET 8 servicing line current.

## Forwarded-header behavior

ASP.NET Core 8.0.17 introduced hardening that ignores `X-Forwarded-*` values from unknown proxies. The v1 fixtures use `ForwardedHeadersOptions.KnownProxies`, `KnownNetworks`, and `ForwardLimit`; on net8.0, `ForwardLimit` defaults to `1`, and headers are processed right-to-left. ForwardTrust does not alter any of those options.

Each scenario with an asserted forwarded dimension declares its own opposite-trust peer and expected identity. For every asserted scheme, host, and client-address header, the verifier sends the original and a generated counterfactual through both peers, reissues each identical request, and requires stable full identities. The accepted path must match the exact counterfactual value and the rejected path must remain stable. These controls detect missing controls, peer/default echoes, and observable rewrite, replay, ordering, timing, randomness, or state drift.

Forwarded hop depth is the maximum comma-separated chain length in any asserted forwarded dimension, not the sum across `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host`. For example, a two-hop scenario can use `X-Forwarded-For: 198.51.100.10, 10.0.0.10`, `X-Forwarded-Proto: https, http`, and `X-Forwarded-Host: public.example, internal.example` with the application's `ForwardLimit` set to `2`; a one-hop request with one value in each field is not treated as a three-hop request.

The verifier requires at least one scenario, stops lazy scenario intake at the first item beyond `MaxScenarioCount`, and bounds each scenario to 64 headers, 64 KiB of aggregate UTF-8 header-name/value bytes, 256-character names, and 16 KiB values. Header names use HTTP token syntax and field values reject control characters other than horizontal tab and legal visible/obs-text characters. A violation is reported as `MalformedScenario` before any caller-provided sender invocation.

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

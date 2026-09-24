# ForwardTrust Compatibility

This note records the current platform facts that shape the v1 contract. It is intentionally short and is not a replacement for the linked primary documentation.

## Supported runtime

ForwardTrust ships only a `net8.0` asset and is tested against the ASP.NET Core 8 shared framework. On 2026-09-24, .NET 8 was in maintenance support at patch `8.0.31`, with support listed through 2026-11-10. Consumers should keep the .NET 8 servicing line current.

## Forwarded-header behavior

ASP.NET Core 8.0.17 introduced hardening that ignores `X-Forwarded-*` values from unknown proxies. The v1 fixtures use `ForwardedHeadersOptions.KnownProxies`, `KnownNetworks`, and `ForwardLimit`; on net8.0, `ForwardLimit` defaults to `1`, and headers are processed right-to-left. ForwardTrust does not alter any of those options.

## Test-host peer seam

The supported seam is caller-owned: `TestServer.SendAsync(Action<HttpContext>, CancellationToken)` can assign `HttpContext.Connection.RemoteIpAddress` before the real pipeline runs. ForwardTrust exposes only a framework-neutral request/probe delegate, so consumers may use TestServer or their own test host without a runtime TestHost dependency.

## Primary sources

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [.NET 8 downloads and servicing](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- [Unknown-proxy forwarded-header hardening](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/8.0/forwarded-headers-unknown-proxies)
- [ASP.NET Core proxy and load-balancer configuration](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-8.0)
- [`TestServer.SendAsync`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.testhost.testserver.sendasync)

Package and repository name availability was checked immediately before repository creation: the NuGet registration endpoint returned 404 for `KeelMatrix.ForwardTrust`, and `KeelMatrix/ForwardTrust` did not yet exist. Availability must be checked again before publication.

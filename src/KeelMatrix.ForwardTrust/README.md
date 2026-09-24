# KeelMatrix.ForwardTrust

Verify ASP.NET Core forwarded-header trust behavior with executable integration-test scenarios.

## Install

```bash
dotnet add package KeelMatrix.ForwardTrust
```

## Quick start

Create a `ForwardTrustScenario` with the simulated immediate peer, forwarded headers, and expected `ForwardedIdentity`. Pass a `ForwardTrustRequestSender` that applies the request to your real ASP.NET Core test host and returns the identity from a test-only probe endpoint.

```csharp
var result = await new ForwardTrustVerifier().VerifyAsync(
    [scenario],
    SendThroughYourTestHost);
Assert.True(result.Succeeded);
```

Use `Accepted` for a configured trusted proxy and `Rejected` for an untrusted peer. Host values are checked only when `ForwardedIdentity.Host` is explicitly supplied. `ForwardTrustResult.Failures` distinguishes trusted-header rejection, untrusted-header acceptance, scheme, host, client-address, forward-limit, malformed-scenario, and host/probe failures.

## Boundaries

ForwardTrust proves application-pipeline interpretation in an in-process test. It does not prove the actual configuration of a cloud load balancer, ingress controller, firewall, reverse proxy, or production network path. It never rewrites `ForwardedHeadersOptions`, discovers infrastructure, or makes external network calls.

Clearing trusted proxy/network lists broadens trust and can enable spoofing. Configure only the proxies and networks that are actually trusted.

The package targets `net8.0`, has no `Microsoft.AspNetCore.Mvc.Testing` runtime dependency, and omits telemetry in v1 so verdicts remain offline and independent of data collection. See the [canonical repository README](https://github.com/KeelMatrix/ForwardTrust#readme) for the full contract.

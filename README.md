# KeelMatrix.ForwardTrust

KeelMatrix.ForwardTrust turns an ASP.NET Core application's forwarded-header trust boundary into executable integration-test scenarios. It checks what the real middleware pipeline exposes for trusted proxies, untrusted peers, multiple hops, schemes, hosts, and client addresses.

## Install

```bash
dotnet add package KeelMatrix.ForwardTrust
```

## Quick start

Declare an immutable scenario and provide a request sender owned by your test host. The sender applies `request.ImmediatePeerAddress` to the test connection, copies `request.Headers`, runs the real application pipeline, and returns the probe's `ForwardedIdentity`.

```csharp
var scenario = new ForwardTrustScenario(
    "trusted-proxy",
    "10.0.0.10",
    new ForwardedIdentity("https", IPAddress.Parse("198.51.100.10"), "public.example"),
    ForwardTrustHeaderExpectation.Accepted,
    new Dictionary<string, string>
    {
        ["X-Forwarded-For"] = "198.51.100.10",
        ["X-Forwarded-Proto"] = "https",
        ["X-Forwarded-Host"] = "public.example"
    });

var result = await new ForwardTrustVerifier().VerifyAsync([scenario], SendThroughYourTestHost);
Assert.True(result.Succeeded);
```

For an untrusted-peer scenario, set `ForwardTrustHeaderExpectation.Rejected` and expect the identity that your application exposes when the forwarded values are ignored. The verifier validates all scenarios before sending any request, bounds the scenario count and request timeout, and returns structured failures.

## What ForwardTrust proves

It proves how the application pipeline interprets the forwarded headers for the peer, headers, and middleware configuration supplied by the test. Failure diagnostics identify the scenario and trust dimension without dumping cookies, authorization data, response bodies, or unrelated headers.

## What ForwardTrust does not prove

An in-process test does not prove the actual configuration of a cloud load balancer, ingress controller, firewall, reverse proxy, or production network path. It does not discover infrastructure, verify authorization policy, run a scanner, or configure `ForwardedHeadersOptions` for you.

Only assert forwarded host values when the application intentionally uses them. Clearing trusted proxy/network lists broadens trust and can enable spoofing; it is not a recommended fix. Configure only the proxies and networks that are actually trusted.

## Supported scope and privacy

The package targets `net8.0` and the ASP.NET Core 8 shared framework. It uses no `Microsoft.AspNetCore.Mvc.Testing` runtime dependency and makes no external network, DNS, cloud-metadata, or telemetry call. Telemetry is intentionally omitted in v1 because the verifier handles security-boundary test data and the package must keep the offline verdict path completely independent of data collection. See [docs/compatibility.md](docs/compatibility.md), [SECURITY.md](SECURITY.md), and [PRIVACY.md](PRIVACY.md).

The repository README is the canonical user-facing source. The packed project README mirrors this install, quick-start, scope, and privacy contract.

## Documentation

- [Compatibility and tested seam](docs/compatibility.md)
- [Contributing](CONTRIBUTING.md)
- [Security](SECURITY.md)
- [Privacy](PRIVACY.md)

## License

MIT. See [LICENSE](LICENSE).

# KeelMatrix.ForwardTrust

Verify ASP.NET Core forwarded-header trust behavior with executable integration-test scenarios.

## Install

```bash
dotnet add package KeelMatrix.ForwardTrust
dotnet add package Microsoft.AspNetCore.TestHost
dotnet add package xunit
```

The second and third packages belong in the consumer's test project. They provide the test host and assertion framework used by this example; `KeelMatrix.ForwardTrust` itself has no `Microsoft.AspNetCore.TestHost` runtime dependency.

## Quick start

This complete xUnit example runs the real forwarded-header middleware in an in-memory ASP.NET Core test host. The request sender applies both the simulated peer and headers before the pipeline runs.

```csharp
using System.Net;
using KeelMatrix.ForwardTrust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class ForwardTrustQuickStartTests
{
    [Fact]
    public async Task TrustedProxyScenarioPasses()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .Configure(app =>
                {
                    var options = new ForwardedHeadersOptions
                    {
                        ForwardedHeaders = ForwardedHeaders.XForwardedFor
                            | ForwardedHeaders.XForwardedProto
                            | ForwardedHeaders.XForwardedHost
                    };
                    options.KnownProxies.Clear();
                    options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
                    app.UseForwardedHeaders(options);
                    app.Run(static context => context.Response.WriteAsync("probe", context.RequestAborted));
                }))
            .Build();
        await host.StartAsync();

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
            },
            control: new ForwardTrustControl(
                "10.0.0.20",
                new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), "localhost")));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            SendThroughTestHost(host.GetTestServer()));

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(failure => failure.Message)));
    }

    private static ForwardTrustRequestSender SendThroughTestHost(TestServer server)
    {
        return async (request, cancellationToken) =>
        {
            var context = await server.SendAsync(httpContext =>
            {
                httpContext.Request.Path = request.Path;
                httpContext.Connection.RemoteIpAddress = request.ImmediatePeerAddress;
                foreach (var header in request.Headers)
                {
                    httpContext.Request.Headers[header.Key] = header.Value;
                }
            }, cancellationToken);

            return new ForwardedIdentity(
                context.Request.Scheme,
                context.Connection.RemoteIpAddress ?? IPAddress.None,
                context.Request.Host.ToString());
        };
    }
}
```

For an untrusted peer, use `ForwardTrustHeaderExpectation.Rejected`, expect the identity exposed when forwarded values are ignored, and set `control` to a trusted peer plus the identity those same headers must produce. An accepted scenario uses an untrusted control peer and its ignored-header identity. For every asserted dimension, the verifier sends original and generated-counterfactual requests through both peers, then reissues each identical request. A pass requires stable full identities, the exact generated value on the accepted side, and no change on the rejected side.

## Failure diagnostics

`ForwardTrustResult.Failures` distinguishes trusted-header rejection, untrusted-header acceptance, scheme, host, client-address, forward-limit, malformed-scenario, timed-out host/probe, host setup, request-application-not-proven, and forwarded-value-not-proven failures. Duplicate scenario names and contradictory expectations are rejected before any request is sent.

`RequestApplicationNotProven` means an asserted scenario omitted a valid scenario-local opposite-trust control or reused its primary peer. `ForwardedValueNotProven` means an accepted-side counterfactual did not produce the generated value or an identical request produced an unstable observation. Neither outcome is a pass. The controls reject observable no-op, rewrite, replay, call-order, time, randomness, and state-drift behavior.

## What ForwardTrust proves

Under the request-sender contract below, ForwardTrust proves application-pipeline interpretation in an in-process test. It checks each asserted scheme, host, and client-address dimension independently with a scenario-local accepted/rejected control pair, exact counterfactual matching, and repeated identical requests.

## What ForwardTrust does not prove

ForwardTrust assumes the caller-provided request sender actually executes every supplied request against the application pipeline. Its controls detect accidental non-execution and observable non-causal drift, but a black-box verifier cannot distinguish the real pipeline from a deliberately fabricated sender that returns the same self-consistent identities for every generated request.

It also does not prove the actual configuration of a cloud load balancer, ingress controller, firewall, reverse proxy, or production network path. It never rewrites `ForwardedHeadersOptions`, discovers infrastructure, or makes external network calls.

Clearing trusted proxy/network lists broadens trust and can enable spoofing. Configure only the proxies and networks that are actually trusted.

The repository README is the canonical source for the complete user-facing contract: [github.com/KeelMatrix/ForwardTrust](https://github.com/KeelMatrix/ForwardTrust#readme). This package README mirrors its install, executable quick start, scope, diagnostics, and privacy claims.

The package targets `net8.0`, has no `Microsoft.AspNetCore.TestHost` runtime dependency, and omits telemetry in v1 so verdicts remain offline and independent of data collection. See the canonical [compatibility note](https://github.com/KeelMatrix/ForwardTrust/blob/main/docs/compatibility.md), [security policy](https://github.com/KeelMatrix/ForwardTrust/blob/main/SECURITY.md), and [privacy policy](https://github.com/KeelMatrix/ForwardTrust/blob/main/PRIVACY.md).

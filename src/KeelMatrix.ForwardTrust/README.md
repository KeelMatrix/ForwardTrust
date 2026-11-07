# KeelMatrix.ForwardTrust

Verify ASP.NET Core forwarded-header trust behavior with executable integration-test scenarios.

## Install

```bash
dotnet new xunit --framework net8.0 --name ForwardTrust.Tests
cd ForwardTrust.Tests
dotnet add package KeelMatrix.ForwardTrust
dotnet add package Microsoft.AspNetCore.TestHost --version 8.0.33
dotnet add package xunit
```

Use the .NET 8 SDK. In an existing xUnit test project, run the three `dotnet add package` commands there. TestHost is pinned to ASP.NET Core 8 because an unversioned command can select a newer, incompatible framework. ForwardTrust has no TestHost runtime dependency.

## Quick start

Replace the generated `UnitTest1.cs` with this code, then run `dotnet test -c Release`. This complete xUnit example runs the real forwarded-header middleware in an in-memory ASP.NET Core test host. The request sender applies both the simulated peer and headers before the pipeline runs.

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

        var untrustedScenario = new ForwardTrustScenario(
            "untrusted-peer",
            "10.0.0.20",
            new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), "localhost"),
            ForwardTrustHeaderExpectation.Rejected,
            scenario.Headers,
            control: new ForwardTrustControl("10.0.0.10", scenario.ExpectedIdentity));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario, untrustedScenario],
            SendThroughTestHost(host.GetTestServer()));

        Assert.Equal(2, result.Scenarios.Count);
        Assert.All(result.Scenarios, item => Assert.True(item.Succeeded));
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

For an untrusted peer, use `ForwardTrustHeaderExpectation.Rejected`, expect the identity exposed when forwarded values are ignored, and set `control` to a trusted peer plus the identity those same headers must produce. An accepted scenario uses an untrusted control peer and its ignored-header identity. The verifier checks every generated counterfactual against the same header limits before sending any request. Each hop trial sends original and counterfactual requests through both peers and reissues each identical request. The accepted counterfactual must match the exact generated value (or the caller-selected host value); the rejected path must preserve its full identity. Ordinarily every non-target field must also stay unchanged. If an address change stops later scheme/host processing, additional replayed pairs remove only the dependent prefixes earlier than that address hop. They must retain the original addresses, reproduce the full counterfactual identities, preserve every non-target field within the truncated pair, and keep the rejected path unchanged. These controls reject unrelated rewrites and unstable responses without changing application trust; see the [compatibility note](https://github.com/KeelMatrix/ForwardTrust/blob/main/docs/compatibility.md).

Accepted expectations, on either side, must match original forwarded values in every asserted dimension; contradictory definitions return `MalformedScenario` before any request. IP endpoints compare by address, including equivalent IPv4/IPv6 representations. Controls change a matching original token at the effective consumed hop, preserving other tokens. This supports right-to-left processing stopped by forward limits or trust boundaries, unequal header lengths, and original values equal to baseline when their application is independently observable. Repeated matching values can require additional hop trials.

Host correspondence uses framework normalization. A token that fails normalization, including malformed punycode or Unicode, cannot supply an original-value witness and does not exclude a different valid effective-hop witness. Without a matching original value, verification returns `MalformedScenario` before sending. Normalization alone does not prove middleware acceptance; the original host and its counterfactual must still satisfy the application's host policy and causal controls.

Original address application is checked separately from address counterfactuals against a replayed baseline on the accepted peer, with `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` omitted. If the address coincides with that baseline, an observable original scheme or forwarded-host effect must establish the original commit with all original forwarded values retained. The observed Host can supply this evidence without an expected-host assertion. If no original effect is observable, verification returns `ForwardedValueNotProven`, even when the middleware actually consumed the values. A changed address can stop trust traversal before a later host, scheme or prefix abort and commit partially; that counterfactual commit cannot prove the original request.

Original host application is compared with the accepted path's own replayed baseline with `X-Forwarded-Host` omitted, regardless of the other peer's host. If they coincide, ordinary `Host` controls must move that same path's baseline to the alternate host while leaving the identity with the original forwarded host unchanged. If middleware overwrites the baseline control, the original remains `ForwardedValueNotProven`, even when it was actually consumed. Every possible generated control, including an added or replaced `Host` header, is checked against the same header bounds before any request.

For a two-hop boundary, set the application pipeline's `ForwardLimit` to `2` and keep the chain aligned across the asserted forwarded dimensions:

```csharp
var twoHop = new ForwardTrustScenario(
    "two-hop-trusted-chain",
    "10.0.0.11",
    new ForwardedIdentity("https", IPAddress.Parse("198.51.100.10"), "public.example"),
    ForwardTrustHeaderExpectation.Accepted,
    new Dictionary<string, string>
    {
        ["X-Forwarded-For"] = "198.51.100.10, 10.0.0.10",
        ["X-Forwarded-Proto"] = "https, http",
        ["X-Forwarded-Host"] = "public.example, internal.example"
    },
    control: new ForwardTrustControl(
        "10.0.0.20",
        new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), "localhost")));

var result = await new ForwardTrustVerifier().VerifyAsync([twoHop], sender);
Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(failure => failure.Message)));
```

Scenario input must contain at least one item and is limited by `ForwardTrustVerifierOptions.MaxScenarioCount` (32 by default, 256 maximum); lazy input is stopped at the first item over that limit. Each scenario accepts at most 64 headers, 64 KiB of aggregate UTF-8 header-name/value bytes, 256 characters per header name, and 16 KiB per value. Header names must use HTTP token characters, names that differ only by case are rejected, and values may contain horizontal tab, visible ASCII, or obs-text only. Generated counterfactual values are checked against the same per-value and aggregate UTF-8 limits before any request sender runs. Violations return `MalformedScenario` before the sender runs.

Each caller-provided host/probe request uses `ForwardTrustVerifierOptions.RequestTimeout`, which defaults to 5 seconds and accepts any positive `TimeSpan` up to and including 2 minutes. Constructing `ForwardTrustVerifier` with zero, a negative value, or a value over 2 minutes throws `ArgumentOutOfRangeException`. The deadline includes synchronous sender invocation and asynchronous completion. Late results return `ForwardTrustFailureKind.HostProbeFailure`. Arbitrary sender code cannot be forcibly interrupted; synchronous work is checked when it returns. Caller cancellation is checked before each request and after completion, propagating `OperationCanceledException` and stopping further requests.

Each individual scenario must also assert at least one supported forwarded dimension: `X-Forwarded-For`, `X-Forwarded-Proto`, or `X-Forwarded-Host` when its expected identity includes a host. Without one, verification returns `ForwardedValueNotProven` before the request sender runs. A host header is ignored when the expected identity does not assert a host; other asserted dimensions remain eligible.

When a rejected primary scenario asserts `X-Forwarded-Host`, its accepted control must declare the expected host for the original forwarded value. Otherwise verification returns `RequestApplicationNotProven` before the sender runs. A default or caller-selected counterfactual cannot replace proof of the original accepted host.

`ForwardedIdentity` copies its `IPAddress` on construction and when `ClientAddress` is read, so changes to an IPv6 scope ID through the input object or a returned address cannot change a stored expectation. `ForwardTrustRequest` also snapshots its immediate-peer address and returns a fresh copy from `ImmediatePeerAddress` on every read, so sender code cannot change later original, counterfactual, or replay inputs by changing an IPv6 scope ID. For host probes, set `CounterfactualHost` in the `ForwardTrustScenario` object initializer when the verifier's default host is not accepted by the application's existing `AllowedHosts` policy. Choose a distinct ASCII host value that the policy already allows; use punycode for internationalized names. For example, an exact `public.example` allowlist can use `public.example:8443`. ForwardTrust does not broaden or change the host policy.

## Failure diagnostics

`ForwardTrustResult.Failures` distinguishes trusted-header rejection, untrusted-header acceptance, scheme, host, client-address, forward-limit, malformed-scenario, timed-out host/probe, host setup, request-application-not-proven, and forwarded-value-not-proven failures. Duplicate scenario names, contradictory expectations, and case-insensitive duplicate header names are rejected before any request is sent.

`ForwardTrustResult.Scenarios`, `ForwardTrustResult.Failures`, and each scenario's `Failures` are read-only defensive snapshots. Callers cannot mutate a returned collection to change or contradict a verification verdict.

`ForwardLimitMismatch` is reported only when a failed asserted forwarded dimension has multiple comma-separated values of its own. A mismatch in another identity dimension, or a multi-hop `X-Forwarded-Host` value when host is not asserted, does not produce this diagnostic.

`RequestApplicationNotProven` means an asserted scenario omitted a valid scenario-local opposite-trust control, reused its primary peer, or asserted a host on a rejected primary without declaring the original host expected from its accepted control. `ForwardedValueNotProven` includes an indistinguishable original address or host baseline, an unexplained dependency or unrelated identity change, a counterfactual that did not produce its generated or caller-selected value, or an unstable replay. A scenario with no supported forwarded dimension also returns this failure before the sender runs. Neither outcome is a pass.

## What ForwardTrust proves

Under the request-sender contract below, ForwardTrust proves application-pipeline interpretation in an in-process test. It checks each asserted scheme, host, and client-address dimension independently with a scenario-local accepted/rejected control pair, exact counterfactual matching, and repeated identical requests.

## What ForwardTrust does not prove

ForwardTrust assumes the caller-provided request sender actually executes every supplied request against the application pipeline. Its controls detect accidental non-execution and observable non-causal drift, but a black-box verifier cannot distinguish the real pipeline from a deliberately fabricated sender that returns the same self-consistent identities for every generated request.

It also does not prove the actual configuration of a cloud load balancer, ingress controller, firewall, reverse proxy, or production network path. It never rewrites `ForwardedHeadersOptions`, discovers infrastructure, or makes external network calls.

Clearing trusted proxy/network lists broadens trust and can enable spoofing. Configure only the proxies and networks that are actually trusted.

This packed README is the source for the executable first-success setup, mirrored by the [repository README](https://github.com/KeelMatrix/ForwardTrust#readme). Public API XML documents scenario and deadline contracts, with implementation and executable regressions as behavioral evidence.

The package targets `net8.0`, has no `Microsoft.AspNetCore.TestHost` runtime dependency, and omits telemetry in v1 so verdicts remain offline and independent of data collection. See the canonical [compatibility note](https://github.com/KeelMatrix/ForwardTrust/blob/main/docs/compatibility.md), [security policy](https://github.com/KeelMatrix/ForwardTrust/blob/main/SECURITY.md), and [privacy policy](https://github.com/KeelMatrix/ForwardTrust/blob/main/PRIVACY.md).

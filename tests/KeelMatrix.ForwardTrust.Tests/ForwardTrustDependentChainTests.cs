using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustDependentChainTests
{
    private static readonly string[] Schemes = ["https", "custom", "http"];
    private static readonly string[] Hosts = ["public.example", "localhost", "internal.example"];

    public static IEnumerable<object?[]> ChainCases()
    {
        foreach (var addressLength in new[] { 1, 2 })
            foreach (var dependentLength in new[] { 1, 2, 3 })
                foreach (var fields in new[] { 2, 4, 6 })
                    foreach (var limit in new int?[] { 1, 2, 3, null })
                        foreach (var representation in Enumerable.Range(0, 6))
                            foreach (var network in new[] { false, true })
                                foreach (var rejectedPrimary in new[] { false, true })
                                    yield return [addressLength, dependentLength, fields, limit, representation, network, rejectedPrimary];
    }

    [Theory]
    [MemberData(nameof(ChainCases))]
    public async Task EffectiveAddressProofPreservesDependentTraversal(
        int addressLength, int dependentLength, int fields, int? limit, int representation, bool network, bool rejectedPrimary)
    {
        var ipv6 = representation % 3 == 1;
        var trusted = IPAddress.Parse(ipv6 ? "2001:db8::10" : "10.0.0.10");
        var untrusted = IPAddress.Parse(ipv6 ? "2001:db8::20" : "10.0.0.20");
        var addresses = ipv6 ? new[] { "2001:db8::12", "2001:db8::11" } : ["10.0.0.12", "10.0.0.11"];
        var schemes = Schemes.Take(dependentLength).ToArray();
        var hosts = Hosts.Take(dependentLength).ToArray();
        var hasProto = (fields & 4) != 0;
        var hasHost = (fields & 2) != 0;
        var consumed = Math.Min(Math.Max(addressLength, dependentLength), limit ?? int.MaxValue);
        var accepted = new ForwardedIdentity(hasProto ? schemes[Math.Max(0, dependentLength - consumed)] : "http",
            IPAddress.Parse(addresses[Math.Max(0, addressLength - consumed)]),
            hasHost ? hosts[Math.Max(0, dependentLength - consumed)] : null);
        var rejected = new ForwardedIdentity("http", untrusted, hasHost ? "localhost" : null);
        string AddressToken(string value)
        {
            var mapped = representation % 3 == 2 ? $"::ffff:{value}" : value;
            return representation >= 3 ? (mapped.Contains(':') ? $"\"[{mapped}]:4567\"" : $"\"{mapped}:4567\"") : mapped;
        }
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = string.Join(", ", addresses.Take(addressLength).Select(AddressToken))
        };
        if (hasProto) { headers["X-Forwarded-Proto"] = string.Join(", ", schemes.Select(value => $"\"{value}\"")); }
        if (hasHost) { headers["X-Forwarded-Host"] = string.Join(", ", hosts.Select(value => $"\"{value}\"")); }
        ForwardedHeadersOptions? actualOptions = null;
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            actualOptions = options;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | (ForwardedHeaders)fields;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            if (network)
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
                    IPAddress.Parse(ipv6 ? "2001:db8::" : "10.0.0.0"), ipv6 ? 123 : 28));
            }
            else
            {
                options.KnownProxies.Add(trusted);
                foreach (var value in addresses) { options.KnownProxies.Add(IPAddress.Parse(value)); }
            }
            options.ForwardLimit = limit;
            options.RequireHeaderSymmetry = false;
        });
        var context = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = trusted;
            context.Request.Host = new HostString("localhost");
            foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
        });
        Assert.Equal(accepted.Scheme, context.Request.Scheme);
        Assert.Equal(accepted.ClientAddress.MapToIPv6(), context.Connection.RemoteIpAddress!.MapToIPv6());
        if (hasHost) { Assert.Equal(accepted.Host, context.Request.Host.ToString()); }
        Assert.Equal(Math.Max(0, addressLength - consumed), context.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
        Assert.True(context.Request.Headers.ContainsKey("X-Original-For"));
        foreach (var field in headers.Keys.Where(key => key != "X-Forwarded-For"))
        {
            Assert.Equal(Math.Max(0, dependentLength - consumed), context.Request.Headers.GetCommaSeparatedValues(field).Length);
            Assert.True(context.Request.Headers.ContainsKey(field.Replace("Forwarded", "Original", StringComparison.Ordinal)));
        }
        var scenario = new ForwardTrustScenario("dependent-chains", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => $"{f.Kind}: {f.Message}")));
        Assert.Equal(network ? 0 : 3, actualOptions!.KnownProxies.Count);
        Assert.Equal(network ? 1 : 0, actualOptions.KnownNetworks.Count);
        Assert.Equal(limit, actualOptions.ForwardLimit);
    }

    public static IEnumerable<object[]> InvalidDependencyCases()
    {
        foreach (var effect in new[] { "scheme", "host", "baseline-address", "square", "rejected", "unstable", "no-op", "unsafe" })
            foreach (var rejectedPrimary in new[] { false, true })
                yield return [effect, rejectedPrimary];
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SymmetryDoesNotHideALaterEffectiveRepeatedAddress(bool symmetry, bool rejectedPrimary, bool assertHost)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        foreach (var limit in new int?[] { 1, 2, null })
        {
            await using var host = await TestHostHarness.StartAsync(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                    | (assertHost ? ForwardedHeaders.XForwardedHost : ForwardedHeaders.None);
                options.KnownProxies.Clear();
                options.KnownNetworks.Clear();
                options.KnownProxies.Add(trusted);
                options.RequireHeaderSymmetry = symmetry;
                options.ForwardLimit = limit;
            });
            var accepted = new ForwardedIdentity(limit == 1 ? "http" : "https", trusted,
                assertHost ? limit == 1 ? "internal.example" : "public.example" : null);
            var rejected = new ForwardedIdentity("http", untrusted, assertHost ? "localhost" : null);
            var headers = new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "10.0.0.10, 10.0.0.10",
                ["X-Forwarded-Proto"] = "https, http"
            };
            if (assertHost) { headers["X-Forwarded-Host"] = "public.example, internal.example"; }
            var scenario = new ForwardTrustScenario("symmetric-repeated-address", (rejectedPrimary ? untrusted : trusted).ToString(),
                rejectedPrimary ? rejected : accepted,
                rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
                control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
            var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
            Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnassertedHostStillParticipatesInAddressDependencyControls(bool rejectedPrimary, bool repeatedAddress)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        var forwarded = IPAddress.Parse(repeatedAddress ? "10.0.0.10" : "10.0.0.11");
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.KnownProxies.Add(forwarded);
            options.ForwardLimit = null;
        });
        var accepted = new ForwardedIdentity("http", forwarded);
        var rejected = new ForwardedIdentity("http", untrusted);
        var scenario = new ForwardTrustScenario("unasserted-dependent-host", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = repeatedAddress ? "10.0.0.10, 10.0.0.10" : "10.0.0.11",
                ["X-Forwarded-Host"] = "\"public.example\", , \"\", \"localhost\", \"internal.example\""
            }, control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
    }

    [Theory]
    [MemberData(nameof(InvalidDependencyCases))]
    public async Task DependentControlsCannotHideUnrelatedChangesOrMissingAddressProof(string effect, bool rejectedPrimary)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        var forwarded = IPAddress.Parse("10.0.0.11");
        var accepted = new ForwardedIdentity("https", forwarded, "public.example");
        var rejected = new ForwardedIdentity("http", untrusted, "localhost");
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "10.0.0.11",
            ["X-Forwarded-Proto"] = "https, http, http",
            ["X-Forwarded-Host"] = "public.example, internal.example"
        };
        var scenario = new ForwardTrustScenario("dependency-false-pass", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var driftCalls = 0;
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.KnownProxies.Add(forwarded);
            if (effect == "unsafe") { options.KnownProxies.Add(untrusted); }
            options.ForwardLimit = null;
        }, null, app => app.Use(async (context, next) =>
        {
            var acceptedPeer = (bool)context.Items["accepted-peer"]!;
            var counterfactual = (bool)context.Items["address-probe"]!;
            var truncated = (bool)context.Items["truncated"]!;
            if (acceptedPeer)
            {
                if (effect == "scheme" && counterfactual) { context.Request.Scheme = "side-effect"; }
                if (effect == "host" && counterfactual) { context.Request.Host = new HostString("side-effect.example"); }
                if (effect == "baseline-address" && truncated && !counterfactual)
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.77");
                }
                if (effect == "square" && truncated && counterfactual) { context.Request.Host = new HostString("side-effect.example"); }
                if (effect == "unstable" && truncated && counterfactual)
                {
                    context.Request.Scheme = ++driftCalls % 2 == 0 ? "drift-a" : "drift-b";
                }
            }
            else if (effect == "rejected" && truncated) { context.Request.Scheme = "side-effect"; }
            await next();
        }));
        var sender = TestHostHarness.CreateSender(host.Server, beforePipeline: (context, request) =>
        {
            context.Items["accepted-peer"] = request.ImmediatePeerAddress.Equals(trusted);
            context.Items["address-probe"] = request.Headers["X-Forwarded-For"] == "203.0.113.254";
            context.Items["truncated"] = request.Headers["X-Forwarded-Proto"] == "http";
        });
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], async (request, token) =>
        {
            var identity = await sender(request, token);
            return effect == "no-op" && request.ImmediatePeerAddress.Equals(trusted)
                ? new ForwardedIdentity(identity.Scheme, forwarded, identity.Host) : identity;
        });
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Kind is ForwardTrustFailureKind.ForwardedValueNotProven
            or ForwardTrustFailureKind.UntrustedHeaderAccepted);
        if (effect == "unstable")
        {
            Assert.Contains(result.Failures, failure => failure.Message.StartsWith("Reissuing an identical", StringComparison.Ordinal));
        }
    }
}

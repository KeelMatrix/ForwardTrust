using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustOriginalCommitTests
{
    public static IEnumerable<object[]> UnobservableCases()
    {
        foreach (var effect in new[] { "host-unavailable", "host-overwritten", "baseline-unstable", "original-unstable", "no-op", "unsafe", "non-target" })
            foreach (var rejectedPrimary in new[] { false, true })
                yield return [effect, rejectedPrimary];
    }

    [Theory]
    [MemberData(nameof(UnobservableCases))]
    public async Task OriginalCommitControlsDoNotAcceptMissingOrUnstableEvidence(string effect, bool rejectedPrimary)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        var baselineCalls = 0;
        var originalCalls = 0;
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = effect == "no-op" ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            if (effect == "unsafe") { options.KnownProxies.Add(untrusted); }
        }, null, app => app.Use(async (context, next) =>
        {
            if (effect == "non-target" && context.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("203.0.113.254")))
            {
                context.Request.Scheme = "side-effect";
            }
            await next();
        }), beforeForwarding: app => app.Use(async (context, next) =>
        {
            if (effect == "host-overwritten") { context.Request.Host = new HostString("localhost"); }
            if (context.Connection.RemoteIpAddress!.Equals(trusted))
            {
                if (effect == "baseline-unstable" && !context.Request.Headers.ContainsKey("X-Forwarded-For"))
                {
                    context.Request.Scheme = ++baselineCalls % 2 == 0 ? "http" : "custom";
                }
                if (effect == "original-unstable" && context.Request.Headers["X-Forwarded-For"] == "10.0.0.10")
                {
                    context.Request.Scheme = ++originalCalls % 2 == 0 ? "http" : "custom";
                }
            }
            await next();
        }));
        var accepted = new ForwardedIdentity(effect == "original-unstable" ? "custom" : "http", trusted);
        var rejected = new ForwardedIdentity("http", untrusted);
        var scenario = new ForwardTrustScenario("unobservable-original", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-For"] = "10.0.0.10", ["X-Forwarded-Host"] = "localhost" },
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var sender = TestHostHarness.CreateSender(host.Server);
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], async (request, cancellation) =>
        {
            var identity = await sender(request, cancellation);
            // This class of ambiguity belongs to legacy identity-only senders.
            return new ForwardedIdentity(identity.Scheme, identity.ClientAddress, effect == "host-unavailable" ? null : identity.Host);
        });
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Kind is ForwardTrustFailureKind.ForwardedValueNotProven
            or ForwardTrustFailureKind.UntrustedHeaderAccepted);
        if (effect.EndsWith("unstable", StringComparison.Ordinal))
        {
            Assert.Contains(result.Failures, failure => failure.Message.StartsWith("Reissuing an identical", StringComparison.Ordinal));
        }
    }

    public static IEnumerable<object?[]> AbortCases()
    {
        foreach (var length in new[] { 2, 3, 4 })
            foreach (var badDepth in Enumerable.Range(2, length - 1))
                foreach (var field in new[] { "policy-host", "malformed-host", "scheme", "prefix", "prefix-only" })
                    foreach (var representation in Enumerable.Range(0, 6))
                        foreach (var network in new[] { false, true })
                            foreach (var rejectedPrimary in new[] { false, true })
                                foreach (var unlimited in new[] { false, true })
                                    foreach (var mixedAddresses in new[] { false, true })
                                        yield return [length, badDepth, field, representation, network, rejectedPrimary, unlimited, mixedAddresses];
    }

    [Theory]
    [MemberData(nameof(AbortCases))]
    public async Task AnEarlierEnabledFieldAbortCannotBeHiddenByATrustStop(
        int length, int badDepth, string field, int representation, bool network, bool rejectedPrimary, bool unlimited, bool mixedAddresses)
    {
        var ipv6 = representation % 3 == 1;
        var mappedPeer = representation % 3 == 2;
        var trusted = IPAddress.Parse(ipv6 ? "2001:db8::10" : mappedPeer ? "::ffff:10.0.0.10" : "10.0.0.10");
        var untrusted = IPAddress.Parse(ipv6 ? "2001:db8::20" : mappedPeer ? "::ffff:10.0.0.20" : "10.0.0.20");
        var intermediate = IPAddress.Parse(ipv6 ? "2001:db8::11" : mappedPeer ? "::ffff:10.0.0.11" : "10.0.0.11");
        string AddressToken(IPAddress address) => representation < 3 ? address.ToString()
            : address.ToString().Contains(':') ? $"\"[{address}]:4567\"" : $"\"{address}:4567\"";
        var addresses = Enumerable.Repeat(AddressToken(mixedAddresses ? intermediate : trusted), length).ToArray();
        addresses[^1] = AddressToken(trusted);
        var hosts = Enumerable.Repeat("localhost", length).ToArray();
        var schemes = Enumerable.Repeat("http", length).ToArray();
        var prefixes = Enumerable.Repeat("/valid", length).ToArray();
        var badIndex = length - badDepth;
        if (field == "policy-host") { hosts[badIndex] = "blocked.example"; }
        if (field == "malformed-host") { hosts[badIndex] = new[] { "bad host", "[broken", ":443", "host:", "host:bad", "host/path" }[representation]; }
        if (field == "scheme") { schemes[badIndex] = "bad scheme"; }
        if (field.StartsWith("prefix", StringComparison.Ordinal)) { prefixes[badIndex] = "invalid-prefix"; }
        var headers = new Dictionary<string, string> { ["X-Forwarded-For"] = string.Join(", ", addresses) };
        if (field != "prefix-only") { headers["X-Forwarded-Host"] = string.Join(", ", hosts); }
        if (field == "scheme") { headers["X-Forwarded-Proto"] = string.Join(", ", schemes); }
        if (field.StartsWith("prefix", StringComparison.Ordinal)) { headers["X-Forwarded-Prefix"] = string.Join(", ", prefixes); }
        var flags = ForwardedHeaders.XForwardedFor
            | (field != "prefix-only" ? ForwardedHeaders.XForwardedHost : ForwardedHeaders.None)
            | (field == "scheme" ? ForwardedHeaders.XForwardedProto : ForwardedHeaders.None)
            | (field.StartsWith("prefix", StringComparison.Ordinal) ? ForwardedHeaders.XForwardedPrefix : ForwardedHeaders.None);
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = flags;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            if (network)
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
                    IPAddress.Parse(ipv6 ? "2001:db8::" : "10.0.0.0"), ipv6 ? 123 : 28));
            }
            else
            {
                options.KnownProxies.Add(mappedPeer ? trusted.MapToIPv4() : trusted);
                if (mixedAddresses) { options.KnownProxies.Add(mappedPeer ? intermediate.MapToIPv4() : intermediate); }
            }
            options.RequireHeaderSymmetry = true;
            options.ForwardLimit = unlimited ? null : length;
            options.AllowedHosts.Add("localhost");
        });
        // This evidence comes directly from the framework pipeline, independently
        // of the verifier's identities and hop-selection implementation.
        foreach (var changed in new[] { false, true })
        {
            var context = await host.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = trusted;
                context.Request.Host = new HostString("localhost");
                foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
                if (changed)
                {
                    addresses[^1] = "203.0.113.254";
                    context.Request.Headers["X-Forwarded-For"] = string.Join(", ", addresses);
                }
            });
            Assert.Equal(changed, context.Request.Headers.ContainsKey("X-Original-For"));
            Assert.Equal(changed ? length - 1 : length, context.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
            if (!changed) { Assert.Equal(headers["X-Forwarded-For"], context.Request.Headers["X-Forwarded-For"].ToString()); }
            Assert.Equal(changed ? IPAddress.Parse("203.0.113.254").MapToIPv6() : trusted.MapToIPv6(),
                context.Connection.RemoteIpAddress!.MapToIPv6());
            Assert.Equal("http", context.Request.Scheme);
            Assert.Equal("localhost", context.Request.Host.ToString());
        }
        var accepted = new ForwardedIdentity("http", trusted);
        var rejected = new ForwardedIdentity("http", untrusted);
        var scenario = new ForwardTrustScenario("abort-with-trust-stop", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven);
    }

    public static IEnumerable<object[]> CommitCases()
    {
        foreach (var boundary in new[] { (2, 2, 1, true), (2, 2, 2, false), (4, 3, 2, true), (4, 3, 3, false), (4, 4, 3, true), (4, 4, 4, false) })
            foreach (var representation in Enumerable.Range(0, 6))
                foreach (var network in new[] { false, true })
                    foreach (var rejectedPrimary in new[] { false, true })
                        yield return [boundary.Item1, boundary.Item2, boundary.Item3, boundary.Item4, representation, network, rejectedPrimary];
    }

    [Theory]
    [MemberData(nameof(CommitCases))]
    public async Task ARealOriginalCommitStillPasses(int length, int badDepth, int limit, bool symmetry, int representation, bool network, bool rejectedPrimary)
    {
        var ipv6 = representation % 3 == 1;
        var mapped = representation % 3 == 2;
        var trusted = IPAddress.Parse(ipv6 ? "2001:db8::10" : mapped ? "::ffff:10.0.0.10" : "10.0.0.10");
        var untrusted = IPAddress.Parse(ipv6 ? "2001:db8::20" : mapped ? "::ffff:10.0.0.20" : "10.0.0.20");
        var token = representation < 3 ? trusted.ToString() : trusted.ToString().Contains(':') ? $"\"[{trusted}]:4567\"" : $"\"{trusted}:4567\"";
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            if (network)
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
                    IPAddress.Parse(ipv6 ? "2001:db8::" : "10.0.0.0"), ipv6 ? 123 : 28));
            }
            else { options.KnownProxies.Add(mapped ? trusted.MapToIPv4() : trusted); }
            options.RequireHeaderSymmetry = symmetry;
            options.ForwardLimit = limit;
            options.AllowedHosts.Add("localhost");
        });
        var hosts = Enumerable.Repeat("localhost", length).ToArray();
        hosts[length - badDepth] = "blocked.example";
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = string.Join(", ", Enumerable.Repeat(token, length)),
            ["X-Forwarded-Host"] = string.Join(", ", hosts)
        };
        var context = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = trusted;
            context.Request.Host = new HostString("localhost");
            foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
        });
        Assert.True(context.Request.Headers.ContainsKey("X-Original-For"));
        Assert.Equal(length - limit, context.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
        var accepted = new ForwardedIdentity("http", trusted);
        var rejected = new ForwardedIdentity("http", untrusted);
        var scenario = new ForwardTrustScenario("committed-baseline", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MalformedEarlierAddressCommitsTheValidSuffix(bool equalBaseline, bool rejectedPrimary)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        var forwarded = equalBaseline ? trusted : IPAddress.Parse("10.0.0.11");
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.KnownProxies.Add(forwarded);
            options.RequireHeaderSymmetry = true;
            options.ForwardLimit = null;
        });
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = $"malformed, {forwarded}",
            ["X-Forwarded-Host"] = "blocked.example, localhost"
        };
        var context = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = trusted;
            context.Request.Host = new HostString("localhost");
            foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
        });
        Assert.True(context.Request.Headers.ContainsKey("X-Original-For"));
        Assert.Equal("malformed", context.Request.Headers["X-Forwarded-For"].ToString());
        Assert.Equal("blocked.example", context.Request.Headers["X-Forwarded-Host"].ToString());
        Assert.Equal(forwarded, context.Connection.RemoteIpAddress);
        var accepted = new ForwardedIdentity("http", forwarded);
        var rejected = new ForwardedIdentity("http", untrusted);
        var scenario = new ForwardTrustScenario("malformed-address-partial-commit", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
    }

    [Fact]
    public async Task AddressCounterfactualCannotProveAnOriginalThatAborted()
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.RequireHeaderSymmetry = true;
            options.ForwardLimit = 2;
            options.AllowedHosts.Add("localhost");
        });
        const string original = "10.0.0.10, 10.0.0.10";
        foreach (var changed in new[] { false, true })
        {
            var context = await host.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = trusted;
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("localhost");
                context.Request.Headers["X-Forwarded-For"] = changed ? "10.0.0.10, 203.0.113.254" : original;
                context.Request.Headers["X-Forwarded-Host"] = "blocked.example, localhost";
            });
            Assert.Equal(changed ? "203.0.113.254" : trusted.ToString(), context.Connection.RemoteIpAddress!.ToString());
            Assert.Equal(changed ? "10.0.0.10" : original, context.Request.Headers["X-Forwarded-For"].ToString());
            Assert.Equal(changed, context.Request.Headers.ContainsKey("X-Original-For"));
            Assert.Equal("http", context.Request.Scheme);
            Assert.Equal("localhost", context.Request.Host.ToString());
        }
        var scenario = new ForwardTrustScenario("original-aborted", trusted.ToString(), new ForwardedIdentity("http", trusted),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-For"] = original, ["X-Forwarded-Host"] = "blocked.example, localhost" },
            control: new ForwardTrustControl(untrusted.ToString(), new ForwardedIdentity("http", untrusted)));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven);
    }
}

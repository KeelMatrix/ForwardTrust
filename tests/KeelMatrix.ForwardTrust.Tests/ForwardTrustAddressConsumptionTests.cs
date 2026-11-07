using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustAddressConsumptionTests
{
    public static IEnumerable<object?[]> CompositionCases()
    {
        foreach (var topology in new[] { "single", "scheme-before", "host-before", "host-after", "scheme-after" })
            foreach (var field in Fields(topology))
                foreach (var representation in Enumerable.Range(0, 6))
                    foreach (var network in new[] { false, true })
                        foreach (var reverse in new[] { false, true })
                            foreach (var boundary in new[] { (2, 2), (4, 2), (4, 4) })
                                foreach (var limit in new int?[] { boundary.Item1, null })
                                    foreach (var assertHost in topology.StartsWith("host", StringComparison.Ordinal) || field.EndsWith("host", StringComparison.Ordinal) ? new[] { false, true } : new[] { false })
                                        yield return [topology, field, representation, network, reverse, boundary.Item1, boundary.Item2, limit, true, assertHost];

        // The 44 original positive boundary rows retain their exact parameters.
        foreach (var topology in new[] { "single", "scheme-before", "host-before", "host-after" })
            foreach (var field in Fields(topology))
                foreach (var reverse in new[] { false, true })
                    foreach (var symmetry in new[] { false, true })
                        yield return [topology, field, 0, false, reverse, 2, 2, symmetry ? 1 : 2, symmetry, false];
    }

    private static string[] Fields(string topology) => topology == "single"
        ? ["policy-host", "malformed-host", "scheme", "prefix"]
        : topology.StartsWith("scheme", StringComparison.Ordinal) ? ["policy-host", "malformed-host", "prefix"] : ["scheme", "prefix"];

    [Theory]
    [MemberData(nameof(CompositionCases))]
    public async Task OriginalConsumptionIsDimensionSpecificAcrossIndependentStages(
        string topology, string field, int representation, bool network, bool reverse,
        int length, int badDepth, int? limit, bool symmetry, bool assertHost)
    {
        var (trusted, rejected) = Peers(representation);
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = string.Join(", ", Enumerable.Repeat(AddressToken(trusted, representation), length))
        };
        var values = Enumerable.Repeat(field.EndsWith("host", StringComparison.Ordinal) ? "localhost" : field == "scheme" ? "http" : "/valid", length).ToArray();
        values[length - badDepth] = field switch
        {
            "policy-host" => "blocked.example",
            "malformed-host" => new[] { "bad host", "[broken", ":443", "host:", "host:bad", "host/path" }[representation],
            "scheme" => "bad scheme",
            _ => "invalid-prefix"
        };
        headers[field.EndsWith("host", StringComparison.Ordinal) ? "X-Forwarded-Host" : field == "scheme" ? "X-Forwarded-Proto" : "X-Forwarded-Prefix"] = string.Join(", ", values);
        if (topology.StartsWith("scheme", StringComparison.Ordinal)) { headers["X-Forwarded-Proto"] = string.Join(", ", Enumerable.Repeat("https", length)); }
        if (topology.StartsWith("host", StringComparison.Ordinal)) { headers["X-Forwarded-Host"] = string.Join(", ", Enumerable.Repeat("localhost", length)); }
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        var witness = topology.StartsWith("host", StringComparison.Ordinal) ? ForwardedHeaders.XForwardedHost : ForwardedHeaders.XForwardedProto;
        ForwardedHeadersOptions Options(ForwardedHeaders flags) => CreateOptions(flags, representation, network, trusted, limit, symmetry);
        if (topology.EndsWith("before", StringComparison.Ordinal)) { app.UseForwardedHeaders(Options(witness)); }
        var flags = ForwardedHeaders.XForwardedFor | (field.EndsWith("host", StringComparison.Ordinal) ? ForwardedHeaders.XForwardedHost
            : field == "scheme" ? ForwardedHeaders.XForwardedProto : ForwardedHeaders.XForwardedPrefix);
        var addressOptions = Options(flags);
        if (field == "policy-host") { addressOptions.AllowedHosts.Add("localhost"); }
        app.UseForwardedHeaders(addressOptions);
        if (topology.EndsWith("after", StringComparison.Ordinal)) { app.UseForwardedHeaders(Options(witness)); }
        app.Run(_ => Task.CompletedTask);
        var pipeline = app.Build();
        async Task<DefaultHttpContext> Send(IPAddress peer, IReadOnlyDictionary<string, string> supplied)
        {
            var context = Context(peer, supplied, services);
            await pipeline(context);
            return context;
        }
        var original = await Send(trusted, headers);
        var rejectedOriginal = await Send(rejected, headers);
        var changedTokens = Enumerable.Repeat(AddressToken(trusted, representation), length).ToArray();
        changedTokens[^1] = "203.0.113.254";
        var changed = await Send(trusted, new Dictionary<string, string>(headers) { ["X-Forwarded-For"] = string.Join(", ", changedTokens) });
        var committed = !symmetry || limit < badDepth;
        Assert.Equal(committed, original.Request.Headers.ContainsKey("X-Original-For"));
        Assert.Equal(committed ? length - limit : length, original.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
        if (!committed) { Assert.Equal(headers["X-Forwarded-For"], original.Request.Headers["X-Forwarded-For"].ToString()); }
        Assert.True(changed.Request.Headers.ContainsKey("X-Original-For"));
        Assert.Equal(length - 1, changed.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
        Assert.Equal(IPAddress.Parse("203.0.113.254"), changed.Connection.RemoteIpAddress);
        Assert.False(rejectedOriginal.Request.Headers.ContainsKey("X-Original-For"));
        Assert.Equal(headers["X-Forwarded-For"], rejectedOriginal.Request.Headers["X-Forwarded-For"].ToString());
        var acceptedIdentity = Identity(original, false, assertHost);
        var rejectedIdentity = Identity(rejectedOriginal, false, assertHost);
        var scenario = new ForwardTrustScenario("composed-address", (reverse ? rejected : trusted).ToString(), reverse ? rejectedIdentity : acceptedIdentity,
            reverse ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((reverse ? trusted : rejected).ToString(), reverse ? acceptedIdentity : rejectedIdentity));
        foreach (var capture in new[] { false, true })
        {
            var result = await new ForwardTrustVerifier().VerifyAsync([scenario], async (request, _) => Identity(await Send(request.ImmediatePeerAddress, request.Headers), capture));
            if (committed && capture) { Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message))); }
            else
            {
                Assert.False(result.Succeeded);
                Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven
                    && failure.Dimension == ForwardTrustDimension.ClientAddress);
            }
        }
    }

    private static (IPAddress Trusted, IPAddress Rejected) Peers(int representation) => (
        IPAddress.Parse(representation % 3 == 1 ? "2001:db8::10" : representation % 3 == 2 ? "::ffff:10.0.0.10" : "10.0.0.10"),
        IPAddress.Parse(representation % 3 == 1 ? "2001:db8::20" : representation % 3 == 2 ? "::ffff:10.0.0.20" : "10.0.0.20"));

    private static string AddressToken(IPAddress address, int representation) => representation < 3 ? address.ToString()
        : address.ToString().Contains(':') ? $"\"[{address}]:4567\"" : $"\"{address}:4567\"";

    private static ForwardedHeadersOptions CreateOptions(ForwardedHeaders flags, int representation, bool network, IPAddress trusted, int? limit, bool symmetry)
    {
        var options = new ForwardedHeadersOptions { ForwardedHeaders = flags, ForwardLimit = limit, RequireHeaderSymmetry = symmetry };
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        if (network)
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
                IPAddress.Parse(representation % 3 == 1 ? "2001:db8::" : "10.0.0.0"), representation % 3 == 1 ? 123 : 28));
        }
        else { options.KnownProxies.Add(representation % 3 == 2 ? trusted.MapToIPv4() : trusted); }
        return options;
    }

    private static DefaultHttpContext Context(IPAddress peer, IReadOnlyDictionary<string, string> headers, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = peer;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
        return context;
    }

    private static ForwardedIdentity Identity(HttpContext context, bool capture, bool includeHost = true)
    {
        var identity = new ForwardedIdentity(context.Request.Scheme, context.Connection.RemoteIpAddress!, includeHost ? context.Request.Host.ToString() : null);
        return capture ? identity.WithForwardedForObservation(context.Request.Headers.TryGetValue("X-Forwarded-For", out var remaining) ? remaining.ToString() : null) : identity;
    }

    public static IEnumerable<object[]> ObservationCases()
    {
        foreach (var observation in new[] { "unavailable", "absent", "partial", "unchanged", "nonprefix", "reordered", "expanded", "port", "representation", "marker-only", "preseeded-marker", "empty-entry", "empty-remainder", "control-character", "wrong-boundary", "malformed-suffix", "over-bound", "unstable", "rejected-consumption", "no-op" })
            foreach (var reverse in new[] { false, true })
                foreach (var assertHost in new[] { false, true })
                    yield return [observation, reverse, assertHost];
    }

    [Theory]
    [MemberData(nameof(ObservationCases))]
    public async Task OnlyConsistentRepeatableOriginalEvidenceSuppliesAnAddressWitness(string observation, bool reverse, bool assertHost)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var rejected = IPAddress.Parse("10.0.0.20");
        var original = observation is "absent" or "no-op" or "marker-only" ? "10.0.0.10" : observation == "empty-entry" ? "10.0.0.11, \"\", 10.0.0.10"
            : observation == "malformed-suffix" ? "malformed, 10.0.0.10" : "\"10.0.0.11:1111\", \"10.0.0.12:2222\", 10.0.0.10";
        var headers = new Dictionary<string, string> { ["X-Forwarded-For"] = original };
        if (assertHost) { headers["X-Forwarded-Host"] = "public.example"; }
        if (observation is "marker-only" or "preseeded-marker") { headers["X-Original-For"] = "preseeded"; }
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseForwardedHeaders(CreateOptions(observation is "no-op" or "marker-only" ? ForwardedHeaders.None
            : ForwardedHeaders.XForwardedFor | (assertHost ? ForwardedHeaders.XForwardedHost : ForwardedHeaders.None), 0, false, trusted, 1, false));
        app.Run(_ => Task.CompletedTask);
        var pipeline = app.Build();
        var originalContext = Context(trusted, headers, services);
        var initialCount = originalContext.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length;
        await pipeline(originalContext);
        var actualCommit = observation is not ("no-op" or "marker-only");
        if (observation != "empty-entry")
        {
            Assert.Equal(actualCommit ? initialCount - 1 : initialCount, originalContext.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
        }
        if (actualCommit)
        {
            Assert.True(originalContext.Request.Headers.ContainsKey("X-Original-For"));
            Assert.NotEqual("preseeded", originalContext.Request.Headers["X-Original-For"].ToString());
        }
        var changedHeaders = new Dictionary<string, string>(headers)
        {
            ["X-Forwarded-For"] = string.Concat(original.AsSpan(0, original.LastIndexOf("10.0.0.10", StringComparison.Ordinal)), "203.0.113.254")
        };
        var changedContext = Context(trusted, changedHeaders, services);
        await pipeline(changedContext);
        Assert.Equal(actualCommit ? IPAddress.Parse("203.0.113.254") : trusted, changedContext.Connection.RemoteIpAddress);
        Assert.Equal(actualCommit ? originalContext.Request.Headers["X-Forwarded-For"].ToString() : changedHeaders["X-Forwarded-For"], changedContext.Request.Headers["X-Forwarded-For"].ToString());
        if (actualCommit) { Assert.NotEqual("preseeded", changedContext.Request.Headers["X-Original-For"].ToString()); }
        var acceptedIdentity = new ForwardedIdentity("http", trusted, assertHost ? "public.example" : null);
        var rejectedIdentity = new ForwardedIdentity("http", rejected, assertHost ? "localhost" : null);
        var scenario = new ForwardTrustScenario("observed-address", (reverse ? rejected : trusted).ToString(), reverse ? rejectedIdentity : acceptedIdentity,
            reverse ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((reverse ? trusted : rejected).ToString(), reverse ? acceptedIdentity : rejectedIdentity));
        var originalCalls = 0;
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], async (request, _) =>
        {
            var context = Context(request.ImmediatePeerAddress, request.Headers, services);
            await pipeline(context);
            var identity = Identity(context, observation != "unavailable", assertHost);
            var acceptedPeer = request.ImmediatePeerAddress.Equals(trusted);
            if (observation == "rejected-consumption" && !acceptedPeer) { return identity.WithForwardedForObservation(null); }
            if (!acceptedPeer || request.Headers.GetValueOrDefault("X-Forwarded-For") != original) { return identity; }
            originalCalls++;
            return observation switch
            {
                "unchanged" or "marker-only" => identity.WithForwardedForObservation(original),
                "nonprefix" => identity.WithForwardedForObservation("10.0.0.13:1111, 10.0.0.12:2222"),
                "reordered" => identity.WithForwardedForObservation("10.0.0.12:2222, 10.0.0.11:1111"),
                "expanded" => identity.WithForwardedForObservation(original + ", 10.0.0.99"),
                "port" => identity.WithForwardedForObservation("10.0.0.11:9999, 10.0.0.12:2222"),
                "representation" => identity.WithForwardedForObservation("[::ffff:10.0.0.11]:1111, 10.0.0.12:2222"),
                "empty-remainder" => identity.WithForwardedForObservation(string.Empty),
                "control-character" => identity.WithForwardedForObservation("10.0.0.11:1111, 10.0.0.12:2222\r\n"),
                "wrong-boundary" => identity.WithForwardedForObservation(null),
                "malformed-suffix" or "no-op" => identity.WithForwardedForObservation(null),
                "over-bound" => identity.WithForwardedForObservation(new string('x', 16385)),
                "unstable" when originalCalls % 2 == 0 => identity.WithForwardedForObservation(original),
                _ => identity
            };
        });
        if (observation is "absent" or "partial" or "preseeded-marker" or "empty-entry")
        {
            Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Contains(result.Failures, failure => failure.Dimension == ForwardTrustDimension.ClientAddress
                || failure.Kind == ForwardTrustFailureKind.HostSetupFailure);
        }
        Assert.All(result.Failures, failure =>
        {
            Assert.DoesNotContain("1111", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("preseeded", failure.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TheCausalTrialMustUseTheConsumedBoundaryRatherThanAnotherMatchingAddress(bool reverse, bool assertHost)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var rejected = IPAddress.Parse("10.0.0.20");
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | (assertHost ? ForwardedHeaders.XForwardedHost : ForwardedHeaders.None);
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.ForwardLimit = 2;
        });
        var acceptedIdentity = new ForwardedIdentity("http", trusted, assertHost ? "public.example" : null);
        var rejectedIdentity = new ForwardedIdentity("http", rejected, assertHost ? "localhost" : null);
        var headers = new Dictionary<string, string> { ["X-Forwarded-For"] = "10.0.0.10, 10.0.0.10" };
        if (assertHost) { headers["X-Forwarded-Host"] = "public.example, public.example"; }
        foreach (var trial in new[] { ("10.0.0.10, 10.0.0.10", 0, trusted), ("203.0.113.254, 10.0.0.10", 0, IPAddress.Parse("203.0.113.254")), ("10.0.0.10, 203.0.113.254", 1, IPAddress.Parse("203.0.113.254")) })
        {
            var context = await host.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = trusted;
                foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
                context.Request.Headers["X-Forwarded-For"] = trial.Item1;
            });
            Assert.True(context.Request.Headers.ContainsKey("X-Original-For"));
            Assert.Equal(trial.Item2, context.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
            Assert.Equal(trial.Item3, context.Connection.RemoteIpAddress);
        }
        var scenario = new ForwardTrustScenario("repeated-boundary", (reverse ? rejected : trusted).ToString(), reverse ? rejectedIdentity : acceptedIdentity,
            reverse ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            headers,
            control: new ForwardTrustControl((reverse ? trusted : rejected).ToString(), reverse ? acceptedIdentity : rejectedIdentity));
        var sender = TestHostHarness.CreateSender(host.Server);
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], async (request, cancellation) =>
        {
            var identity = await sender(request, cancellation);
            // A bounded faulty adapter hides only the actual boundary's causal effect.
            // The nearer matching token still has an effect and cannot replace it.
            return request.ImmediatePeerAddress.Equals(trusted)
                && request.Headers.GetValueOrDefault("X-Forwarded-For") == "203.0.113.254, 10.0.0.10"
                    ? new ForwardedIdentity(identity.Scheme, trusted, identity.Host).WithForwardedForObservation(identity.RemainingForwardedFor)
                    : identity;
        });
        Assert.Contains(result.Failures, failure => failure.Dimension == ForwardTrustDimension.ClientAddress
            && failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven);
    }

    public static IEnumerable<object[]> MalformedPrefixCases()
    {
        foreach (var equal in new[] { false, true })
            foreach (var representation in Enumerable.Range(0, 6))
                foreach (var network in new[] { false, true })
                    foreach (var reverse in new[] { false, true })
                        yield return [equal, representation, network, reverse];
    }

    [Theory]
    [MemberData(nameof(MalformedPrefixCases))]
    public async Task AValidSuffixCanBeConsumedBeforeAMalformedEarlierAddress(bool equal, int representation, bool network, bool reverse)
    {
        var (trusted, rejected) = Peers(representation);
        var forwarded = equal ? trusted : IPAddress.Parse(representation % 3 == 1 ? "2001:db8::11" : representation % 3 == 2 ? "::ffff:10.0.0.11" : "10.0.0.11");
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            var configured = CreateOptions(ForwardedHeaders.XForwardedFor, representation, network, trusted, null, true);
            options.ForwardedHeaders = configured.ForwardedHeaders;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var proxy in configured.KnownProxies) { options.KnownProxies.Add(proxy); }
            foreach (var range in configured.KnownNetworks) { options.KnownNetworks.Add(range); }
            if (!network) { options.KnownProxies.Add(representation % 3 == 2 ? forwarded.MapToIPv4() : forwarded); }
            options.ForwardLimit = null;
            options.RequireHeaderSymmetry = true;
        });
        var headers = new Dictionary<string, string> { ["X-Forwarded-For"] = $"malformed, {AddressToken(forwarded, representation)}" };
        foreach (var changed in new[] { false, true })
        {
            var context = await host.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = trusted;
                context.Request.Headers["X-Forwarded-For"] = changed ? "malformed, 203.0.113.254" : headers["X-Forwarded-For"];
            });
            Assert.True(context.Request.Headers.ContainsKey("X-Original-For"));
            Assert.Equal("malformed", context.Request.Headers["X-Forwarded-For"].ToString());
            Assert.Equal((changed ? IPAddress.Parse("203.0.113.254") : forwarded).MapToIPv6(), context.Connection.RemoteIpAddress!.MapToIPv6());
        }
        // Expected metadata is never an observation and cannot rescue legacy ambiguity.
        var acceptedIdentity = new ForwardedIdentity("http", forwarded).WithForwardedForObservation(null);
        var rejectedIdentity = new ForwardedIdentity("http", rejected).WithForwardedForObservation("irrelevant-expectation");
        var scenario = new ForwardTrustScenario("malformed-prefix", (reverse ? rejected : trusted).ToString(), reverse ? rejectedIdentity : acceptedIdentity,
            reverse ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((reverse ? trusted : rejected).ToString(), reverse ? acceptedIdentity : rejectedIdentity));
        foreach (var capture in new[] { false, true })
        {
            var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server, captureForwardedFor: capture));
            Assert.Equal(capture || !equal, result.Succeeded);
            if (!result.Succeeded)
            {
                Assert.Contains(result.Failures, failure => failure.Dimension == ForwardTrustDimension.ClientAddress
                    && failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven);
            }
        }
    }

    public static IEnumerable<object?[]> DependentBoundaryCases()
    {
        foreach (var representation in Enumerable.Range(0, 6))
            foreach (var network in new[] { false, true })
                foreach (var reverse in new[] { false, true })
                    foreach (var relationship in new[] { 1, 2, 3 })
                        foreach (var limit in new int?[] { 1, 2, 3, null })
                            yield return [representation, network, reverse, relationship, limit];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityOnlyAddressBaselineRetainsOtherOriginalHeaders(bool reverse)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var rejected = IPAddress.Parse("10.0.0.20");
        var forwarded = IPAddress.Parse("10.0.0.11");
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.KnownProxies.Add(forwarded);
        }, null, null, beforeForwarding: app => app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress!.Equals(trusted)
                && !context.Request.Headers.ContainsKey("X-Forwarded-For")
                && context.Request.Headers["X-Forwarded-Proto"] == "https")
            {
                context.Connection.RemoteIpAddress = forwarded;
            }
            await next();
        }));
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = forwarded.ToString(),
            ["X-Forwarded-Proto"] = "https",
            ["X-Forwarded-Host"] = "public.example"
        };
        var acceptedIdentity = new ForwardedIdentity("https", forwarded);
        var rejectedIdentity = new ForwardedIdentity("http", rejected);
        var scenario = new ForwardTrustScenario("address-specific-baseline", (reverse ? rejected : trusted).ToString(), reverse ? rejectedIdentity : acceptedIdentity,
            reverse ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((reverse ? trusted : rejected).ToString(), reverse ? acceptedIdentity : rejectedIdentity));
        foreach (var capture in new[] { false, true })
        {
            var baselines = 0;
            var sender = TestHostHarness.CreateSender(host.Server, captureForwardedFor: capture);
            var result = await new ForwardTrustVerifier().VerifyAsync([scenario], async (request, cancellation) =>
            {
                if (!request.Headers.ContainsKey("X-Forwarded-For"))
                {
                    baselines++;
                    Assert.Equal("https", request.Headers["X-Forwarded-Proto"]);
                    Assert.Equal("public.example", request.Headers["X-Forwarded-Host"]);
                }
                return await sender(request, cancellation);
            });
            Assert.Equal(capture, result.Succeeded);
            Assert.Equal(capture ? 0 : 2, baselines);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealMiddlewareProofHonorsCancellationAndDeadlineAtEveryExecutedPhase(bool capture, bool reverse)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var rejected = IPAddress.Parse("10.0.0.20");
        var forwarded = capture ? trusted : IPAddress.Parse("10.0.0.11");
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
        });
        var acceptedIdentity = new ForwardedIdentity("http", forwarded);
        var rejectedIdentity = new ForwardedIdentity("http", rejected);
        var scenario = new ForwardTrustScenario("real-address-phases", (reverse ? rejected : trusted).ToString(), reverse ? rejectedIdentity : acceptedIdentity,
            reverse ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-For"] = forwarded.ToString() },
            control: new ForwardTrustControl((reverse ? trusted : rejected).ToString(), reverse ? acceptedIdentity : rejectedIdentity));
        var sender = TestHostHarness.CreateSender(host.Server, captureForwardedFor: capture);
        var phases = 0;
        var positive = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, cancellation) =>
        {
            phases++;
            return sender(request, cancellation);
        });
        Assert.True(positive.Succeeded);
        foreach (var asynchronous in new[] { false, true })
            foreach (var phase in Enumerable.Range(1, phases))
            {
                using var cancellation = new CancellationTokenSource();
                var calls = 0;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ForwardTrustVerifier().VerifyAsync([scenario], async (request, _) =>
                {
                    calls++;
                    if (asynchronous) { await Task.Yield(); }
                    if (calls == phase) { cancellation.Cancel(); }
                    return await sender(request, CancellationToken.None);
                }, cancellation.Token));
                Assert.Equal(phase, calls);
                calls = 0;
                var verifier = new ForwardTrustVerifier(new ForwardTrustVerifierOptions { RequestTimeout = TimeSpan.FromMilliseconds(20) });
                var result = await verifier.VerifyAsync([scenario], async (request, _) =>
                {
                    calls++;
                    if (calls == phase)
                    {
                        if (asynchronous) { await Task.Delay(80, CancellationToken.None); }
                        else { Thread.Sleep(80); }
                    }
                    return await sender(request, CancellationToken.None);
                });
                Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.HostProbeFailure);
            }
        await Task.Delay(100);
    }

    [Theory]
    [MemberData(nameof(DependentBoundaryCases))]
    public async Task ConsumptionBoundaryPreservesExactDependentChainControls(int representation, bool network, bool reverse, int relationship, int? limit)
    {
        var (trusted, rejected) = Peers(representation);
        var intermediate = IPAddress.Parse(representation % 3 == 1 ? "2001:db8::11" : representation % 3 == 2 ? "::ffff:10.0.0.11" : "10.0.0.11");
        var client = IPAddress.Parse(representation % 3 == 1 ? "2001:db8:1::42" : representation % 3 == 2 ? "::ffff:198.51.100.42" : "198.51.100.42");
        var addresses = relationship == 1 ? new[] { intermediate } : relationship == 2 ? [client, intermediate] : new[] { client, intermediate, intermediate };
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = string.Join(", ", addresses.Select(address => AddressToken(address, representation))),
            ["X-Forwarded-Proto"] = "https, http",
            ["X-Forwarded-Host"] = "public.example, localhost"
        };
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            var configured = CreateOptions(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
                representation, network, trusted, limit, false);
            options.ForwardedHeaders = configured.ForwardedHeaders;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var proxy in configured.KnownProxies) { options.KnownProxies.Add(proxy); }
            foreach (var range in configured.KnownNetworks) { options.KnownNetworks.Add(range); }
            if (!network) { options.KnownProxies.Add(representation % 3 == 2 ? intermediate.MapToIPv4() : intermediate); }
            options.ForwardLimit = limit;
        });
        var boundary = Math.Max(0, addresses.Length - (limit ?? addresses.Length));
        var original = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = trusted;
            context.Request.Host = new HostString("localhost");
            foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
        });
        Assert.True(original.Request.Headers.ContainsKey("X-Original-For"));
        Assert.Equal(boundary, original.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
        Assert.Equal(addresses[boundary].MapToIPv6(), original.Connection.RemoteIpAddress!.MapToIPv6());
        var changedTokens = addresses.Select(address => AddressToken(address, representation)).ToArray();
        changedTokens[boundary] = "203.0.113.254";
        var changed = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = trusted;
            foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
            context.Request.Headers["X-Forwarded-For"] = string.Join(", ", changedTokens);
        });
        Assert.True(changed.Request.Headers.ContainsKey("X-Original-For"));
        Assert.Equal(boundary, changed.Request.Headers.GetCommaSeparatedValues("X-Forwarded-For").Length);
        Assert.Equal(IPAddress.Parse("203.0.113.254"), changed.Connection.RemoteIpAddress);
        var acceptedIdentity = Identity(original, false);
        var rejectedIdentity = new ForwardedIdentity("http", rejected, "localhost");
        var scenario = new ForwardTrustScenario("consumed-dependent-boundary", (reverse ? rejected : trusted).ToString(), reverse ? rejectedIdentity : acceptedIdentity,
            reverse ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((reverse ? trusted : rejected).ToString(), reverse ? acceptedIdentity : rejectedIdentity));
        foreach (var capture in new[] { false, true })
        {
            var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server, captureForwardedFor: capture));
            Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
        }
    }

    [Fact]
    public async Task IndependentSchemeCommitCannotWitnessAnAbortedOriginalAddress()
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
        }, null, null, beforeForwarding: app =>
        {
            var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto, ForwardLimit = 2 };
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            app.UseForwardedHeaders(options);
        });
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "10.0.0.10, 10.0.0.10",
            ["X-Forwarded-Proto"] = "https, https",
            ["X-Forwarded-Host"] = "blocked.example, localhost"
        };
        foreach (var changed in new[] { false, true })
        {
            var context = await host.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = trusted;
                context.Request.Host = new HostString("localhost");
                foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
                if (changed) { context.Request.Headers["X-Forwarded-For"] = "10.0.0.10, 203.0.113.254"; }
            });
            Assert.Equal(changed, context.Request.Headers.ContainsKey("X-Original-For"));
            Assert.Equal(changed ? "10.0.0.10" : headers["X-Forwarded-For"], context.Request.Headers["X-Forwarded-For"].ToString());
            Assert.Equal("https", context.Request.Scheme);
        }
        var scenario = new ForwardTrustScenario("independent-scheme", trusted.ToString(), new ForwardedIdentity("https", trusted),
            ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl(untrusted.ToString(), new ForwardedIdentity("http", untrusted)));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven
            && failure.Dimension == ForwardTrustDimension.ClientAddress);
    }
}

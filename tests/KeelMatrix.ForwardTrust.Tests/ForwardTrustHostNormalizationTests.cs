using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustHostNormalizationTests
{
    private static readonly IPAddress Trusted = IPAddress.Parse("10.0.0.10");
    private static readonly IPAddress Untrusted = IPAddress.Parse("10.0.0.20");
    private static readonly string[] MalformedHosts = ["xn--", "xn--.example", "xn--a.example", "\u0080.example"];

    public static IEnumerable<object?[]> NormalizationCases()
    {
        foreach (var rejectedPrimary in new[] { false, true })
        {
            foreach (var value in MalformedHosts)
            {
                yield return [value, "localhost", 1, false, rejectedPrimary, true];
                yield return [$"\"{value}\"", "localhost", 1, false, rejectedPrimary, true];
                foreach (var limit in new int?[] { 1, 2, null })
                {
                    yield return [$"{value}, public.example", "public.example", limit, true, rejectedPrimary, false];
                    yield return [$"public.example, {value}", "public.example", limit, limit != 1, rejectedPrimary, false];
                }
            }
            foreach (var value in new[] { "public.example", "xn--bcher-kva.example", "XN--BCHER-KVA.example", "[::1]:8443", "public.example:8443" })
            {
                yield return [value, value, 1, true, rejectedPrimary, false];
                yield return [$"\"{value}\"", value, 1, true, rejectedPrimary, false];
            }
        }
    }

    [Theory]
    [MemberData(nameof(NormalizationCases))]
    public async Task HostNormalizationReturnsStructuredVerdicts(
        string header, string expectedHost, int? limit, bool shouldPass, bool rejectedPrimary, bool noWitness)
    {
        var middleware = CreateMiddleware(ForwardedHeaders.XForwardedHost, limit);
        var headers = new Dictionary<string, string> { ["X-Forwarded-Host"] = header };
        var accepted = new ForwardedIdentity("http", Trusted, expectedHost);
        var scenario = Pair(headers, accepted, rejectedPrimary);
        var direct = await ObserveAsync(middleware, Trusted, headers);
        if (shouldPass) { AssertIdentity(accepted, direct); }
        var calls = 0;
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], async (request, cancellationToken) =>
        {
            calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return await ObserveAsync(middleware, request.ImmediatePeerAddress, request.Headers);
        });
        Assert.Equal(shouldPass, result.Succeeded);
        if (noWitness)
        {
            Assert.Equal(0, calls);
            Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
        }
        else { Assert.True(calls > 0); }
    }

    public static IEnumerable<object?[]> MixedChainCases()
    {
        foreach (var malformed in new[] { "xn--", "xn--a.example", "\u0080.example", "[broken", "bad/host" })
            foreach (var quoted in new[] { false, true })
                for (var hops = 2; hops <= 3; hops++)
                    for (var position = 0; position < hops; position++)
                        foreach (var limit in new int?[] { 1, 2, 3, null })
                            foreach (var flags in new[] { ForwardedHeaders.XForwardedHost, ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto,
                                ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor,
                                ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor })
                                foreach (var rejectedPrimary in new[] { false, true })
                                {
                                    yield return [malformed, quoted, hops, position, limit, flags, rejectedPrimary];
                                }
    }

    [Theory]
    [MemberData(nameof(MixedChainCases))]
    public async Task MalformedSiblingsPreserveEffectiveHopProof(
        string malformed, bool quoted, int hops, int position, int? limit, ForwardedHeaders flags, bool rejectedPrimary)
    {
        // Repeated valid candidates require trials at the actual consumed hop.
        var hosts = Enumerable.Repeat("public.example", hops).ToArray();
        hosts[position] = malformed;
        var consumed = Math.Min(hops, limit ?? hops);
        var effective = hops - consumed;
        var hostWasApplied = hosts.Skip(effective).Any(value => value == "public.example");
        var headers = ChainHeaders(hosts, flags, quoted);
        var accepted = ChainIdentity(flags, effective, hostWasApplied ? "public.example" : "localhost");
        var middleware = CreateMiddleware(flags, limit);
        AssertIdentity(accepted, await ObserveAsync(middleware, Trusted, headers));
        var scenario = Pair(headers, accepted, rejectedPrimary);
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) =>
            ObserveAsync(middleware, request.ImmediatePeerAddress, request.Headers));
        Assert.Equal(hostWasApplied, result.Succeeded);
        if (!hostWasApplied)
        {
            Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
        }

        // A correct expected identity alone never substitutes for causal proof.
        var noOp = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) => ValueTask.FromResult(
            request.ImmediatePeerAddress.Equals(Trusted) ? accepted : Rejected));
        Assert.False(noOp.Succeeded);
    }

    public static IEnumerable<object?[]> StopCases()
    {
        foreach (var malformed in MalformedHosts.Append("[broken"))
            foreach (var quoted in new[] { false, true })
                for (var position = 0; position < 3; position++)
                    foreach (var limit in new int?[] { 1, 2, 3, null })
                        foreach (var stop in new[] { "unknown-proxy", "malformed-address", "host-filter" })
                            foreach (var rejectedPrimary in new[] { false, true })
                            {
                                yield return [malformed, quoted, position, limit, stop, rejectedPrimary];
                            }
    }

    [Theory]
    [MemberData(nameof(StopCases))]
    public async Task NormalizationPreservesTrustAndHostFilteringBoundaries(
        string malformed, bool quoted, int position, int? limit, string stop, bool rejectedPrimary)
    {
        var flags = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        var hosts = new[] { "public.example", "public.example", "public.example" };
        hosts[position] = malformed;
        var headers = ChainHeaders(hosts, flags, quoted);
        var consumed = stop == "host-filter" ? Math.Min(3, limit ?? 3) : 1;
        var effective = 3 - consumed;
        if (stop == "malformed-address") { headers["X-Forwarded-For"] = "198.51.100.100, unknown, 10.0.0.12"; }
        // Host policy can reject every original but permit the counterfactual.
        var hostWasApplied = stop != "host-filter" && hosts.Skip(effective).Any(value => value == "public.example");
        var accepted = ChainIdentity(flags, effective, hostWasApplied ? "public.example" : "localhost");
        var middleware = CreateMiddleware(flags, limit, stop);
        AssertIdentity(accepted, await ObserveAsync(middleware, Trusted, headers));
        var result = await new ForwardTrustVerifier().VerifyAsync([Pair(headers, accepted, rejectedPrimary)], (request, _) =>
            ObserveAsync(middleware, request.ImmediatePeerAddress, request.Headers));
        Assert.Equal(hostWasApplied, result.Succeeded);
    }

    [Theory]
    [InlineData("xn--:8443")]
    [InlineData("xn--a.example:8443")]
    [InlineData("\u0080.example:8443")]
    [InlineData("\u009f.example")]
    [InlineData("bad host")]
    [InlineData("[broken")]
    public async Task InvalidOriginalsAndBaselineEqualWitnessesRemainDistinct(string malformed)
    {
        foreach (var rejectedPrimary in new[] { false, true })
            foreach (var quoted in new[] { false, true })
            {
                var middleware = CreateMiddleware(ForwardedHeaders.XForwardedHost, null);
                var invalid = new Dictionary<string, string> { ["X-Forwarded-Host"] = Quote(malformed, quoted) };
                var calls = 0;
                var result = await new ForwardTrustVerifier().VerifyAsync(
                    [Pair(invalid, new ForwardedIdentity("http", Trusted, "localhost"), rejectedPrimary)], (request, _) =>
                    {
                        calls++;
                        return ObserveAsync(middleware, request.ImmediatePeerAddress, request.Headers);
                    });
                Assert.False(result.Succeeded);
                Assert.Equal(0, calls);
                Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);

                var mixed = new Dictionary<string, string> { ["X-Forwarded-Host"] = $"{Quote(malformed, quoted)}, localhost, {Quote(malformed, quoted)}" };
                var valid = await new ForwardTrustVerifier().VerifyAsync(
                    [Pair(mixed, new ForwardedIdentity("http", Trusted, "localhost"), rejectedPrimary)], (request, _) =>
                        ObserveAsync(middleware, request.ImmediatePeerAddress, request.Headers));
                Assert.True(valid.Succeeded, string.Join(" | ", valid.Failures.Select(failure => failure.Message)));
            }
    }

    [Theory]
    [InlineData("caf\u00e9.example", "xn--caf-dma.example")]
    [InlineData("[broken", "[broken")]
    public async Task NormalizableOriginalStillRequiresMiddlewareAcceptance(string original, string expected)
    {
        foreach (var rejectedPrimary in new[] { false, true })
        {
            var middleware = CreateMiddleware(ForwardedHeaders.XForwardedHost, null);
            var headers = new Dictionary<string, string> { ["X-Forwarded-Host"] = original };
            var scenario = Pair(headers, new ForwardedIdentity("http", Trusted, expected), rejectedPrimary);
            var result = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) =>
                ObserveAsync(middleware, request.ImmediatePeerAddress, request.Headers));
            Assert.False(result.Succeeded);
        }
    }

    private static ForwardedIdentity Rejected => new("http", Untrusted, "localhost");

    private static ForwardTrustScenario Pair(IReadOnlyDictionary<string, string> headers, ForwardedIdentity accepted, bool rejectedPrimary) =>
        new("host-normalization", (rejectedPrimary ? Untrusted : Trusted).ToString(), rejectedPrimary ? Rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((rejectedPrimary ? Trusted : Untrusted).ToString(), rejectedPrimary ? accepted : Rejected));

    private static ForwardedHeadersMiddleware CreateMiddleware(ForwardedHeaders flags, int? limit, string stop = "none")
    {
        var options = new ForwardedHeadersOptions { ForwardedHeaders = flags, ForwardLimit = limit };
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(Trusted);
        options.KnownProxies.Add(IPAddress.Parse("10.0.0.11"));
        if (stop != "unknown-proxy") { options.KnownProxies.Add(IPAddress.Parse("10.0.0.12")); }
        foreach (var host in new[] { "localhost", "counterfactual.example", "xn--bcher-kva.example", "[::1]" }) { options.AllowedHosts.Add(host); }
        if (stop != "host-filter") { options.AllowedHosts.Add("public.example"); }
        return new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
    }

    private static Dictionary<string, string> ChainHeaders(string[] hosts, ForwardedHeaders flags, bool quoted)
    {
        var headers = new Dictionary<string, string> { ["X-Forwarded-Host"] = string.Join(", ", hosts.Select(value => Quote(value, quoted))) };
        if (flags.HasFlag(ForwardedHeaders.XForwardedProto)) { headers["X-Forwarded-Proto"] = string.Join(", ", new[] { "https", "http", "custom" }.Take(hosts.Length)); }
        if (flags.HasFlag(ForwardedHeaders.XForwardedFor)) { headers["X-Forwarded-For"] = string.Join(", ", new[] { "198.51.100.100", "10.0.0.11", "10.0.0.12" }.Take(hosts.Length)); }
        return headers;
    }

    private static ForwardedIdentity ChainIdentity(ForwardedHeaders flags, int effective, string host) => new(
        flags.HasFlag(ForwardedHeaders.XForwardedProto) ? new[] { "https", "http", "custom" }[effective] : "http",
        flags.HasFlag(ForwardedHeaders.XForwardedFor) ? IPAddress.Parse(new[] { "198.51.100.100", "10.0.0.11", "10.0.0.12" }[effective]) : Trusted, host);

    private static string Quote(string value, bool quoted) => quoted ? $"\"{value}\"" : value;

    private static async ValueTask<ForwardedIdentity> ObserveAsync(ForwardedHeadersMiddleware middleware, IPAddress peer, IReadOnlyDictionary<string, string> headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Connection.RemoteIpAddress = peer;
        foreach (var header in headers) { context.Request.Headers[header.Key] = header.Value; }
        await middleware.Invoke(context);
        return new ForwardedIdentity(context.Request.Scheme, context.Connection.RemoteIpAddress!, context.Request.Host.ToString());
    }

    private static void AssertIdentity(ForwardedIdentity expected, ForwardedIdentity actual)
    {
        Assert.Equal(expected.Scheme, actual.Scheme);
        Assert.Equal(expected.Host, actual.Host);
        Assert.Equal(expected.ClientAddress, actual.ClientAddress);
    }
}

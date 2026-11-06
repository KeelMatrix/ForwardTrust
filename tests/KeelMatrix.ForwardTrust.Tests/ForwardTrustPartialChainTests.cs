using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustPartialChainTests
{
    public static IEnumerable<object?[]> ChainCases()
    {
        for (var flags = 1; flags <= 7; flags++)
            for (var hops = 2; hops <= 3; hops++)
                for (var limit = 1; limit <= 4; limit++)
                    for (var representation = 0; representation <= 2; representation++)
                        for (var orientation = 0; orientation <= 1; orientation++)
                        {
                            yield return [flags, hops, limit == 4 ? null : limit, representation, orientation == 1, "none"];
                            if ((flags & 1) != 0 && (limit == 3 || limit == 4))
                            {
                                yield return [flags, hops, limit == 4 ? null : limit, representation, orientation == 1, "unknown"];
                                if (hops == 3) { yield return [flags, hops, limit == 4 ? null : limit, representation, orientation == 1, "malformed"]; }
                            }
                        }
    }

    [Theory]
    [MemberData(nameof(ChainCases))]
    public async Task CorrectEffectiveValuesPassAcrossLimitsAndTrustStops(int flags, int hops, int? limit, int representation, bool rejectedPrimary, string stop)
    {
        var trusted = IPAddress.Parse(representation == 2 ? "2001:db8::10" : "10.0.0.10");
        var untrusted = IPAddress.Parse(representation == 2 ? "2001:db8::20" : "10.0.0.20");
        var addresses = representation == 2
            ? new[] { "2001:db8::100", "2001:db8::11", "2001:db8::12" }
            : ["198.51.100.100", "10.0.0.11", "10.0.0.12"];
        var schemes = new[] { "https", "http", "custom" };
        var hosts = new[] { "public.example", "localhost", "internal.example" };
        var consumed = Math.Min(hops, limit ?? hops);
        if (stop == "unknown") { consumed = 1; }
        if (stop == "malformed") { addresses[hops - 2] = "unknown"; consumed = 1; }
        var effective = hops - consumed;
        var hasFor = (flags & 1) != 0;
        var hasProto = (flags & 4) != 0;
        var hasHost = (flags & 2) != 0;
        var headers = new Dictionary<string, string>();
        if (hasFor)
        {
            headers["X-Forwarded-For"] = string.Join(", ", addresses.Take(hops).Select(value =>
                value == "unknown" ? value : representation switch
                {
                    1 => $"[::ffff:{value}]:4567",
                    2 => $"[{value}]:4567",
                    _ => $"{value}:4567"
                }));
        }
        if (hasProto) { headers["X-Forwarded-Proto"] = string.Join(", ", schemes.Take(hops)); }
        if (hasHost) { headers["X-Forwarded-Host"] = string.Join(", ", hosts.Take(hops)); }
        var accepted = new ForwardedIdentity(hasProto ? schemes[effective] : "http", hasFor ? IPAddress.Parse(addresses[effective]) : trusted, hasHost ? hosts[effective] : null);
        var rejected = new ForwardedIdentity("http", untrusted, hasHost ? "localhost" : null);
        var scenario = new ForwardTrustScenario("effective-chain", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted, rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            headers, control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = (ForwardedHeaders)flags;
            options.KnownProxies.Clear();
            options.KnownProxies.Add(trusted);
            foreach (var address in addresses.Skip(1).Take(hops - 1))
            {
                if (stop != "unknown" && IPAddress.TryParse(address, out var parsed)) { options.KnownProxies.Add(parsed); }
            }
            options.ForwardLimit = limit;
        });
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => $"{f.Kind}: {f.Message}")));

        // The same correct expectations cannot validate a sender that only echoes them.
        var noOp = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) => ValueTask.FromResult(
            request.ImmediatePeerAddress.Equals(trusted) ? accepted : rejected));
        Assert.False(noOp.Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RepeatedOriginalValuesRequireAWitnessAtTheConsumedHop(int limit)
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
            options.ForwardLimit = limit;
        });
        var scenario = new ForwardTrustScenario("repeated-values", "10.0.0.10",
            new ForwardedIdentity("http", IPAddress.Parse("10.0.0.10"), "localhost"), ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "10.0.0.10, 10.0.0.10, 10.0.0.10",
                ["X-Forwarded-Proto"] = "http, http, http",
                ["X-Forwarded-Host"] = "localhost, localhost, localhost"
            }, control: new ForwardTrustControl("10.0.0.20", new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), "localhost")));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(null, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(null, true)]
    public async Task UnequalAndMalformedDimensionLengthsRetainTheirOwnEffectiveValues(int? limit, bool invalidEarlierValues)
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
            options.KnownProxies.Add(IPAddress.Parse("10.0.0.11"));
            options.KnownProxies.Add(IPAddress.Parse("10.0.0.12"));
            options.ForwardLimit = limit;
            options.RequireHeaderSymmetry = false;
        });
        var consumed = Math.Min(3, limit ?? 3);
        var clients = new[] { "198.51.100.100", "10.0.0.11", "10.0.0.12" };
        var scenario = new ForwardTrustScenario("unequal-chains", "10.0.0.10",
            new ForwardedIdentity(invalidEarlierValues || consumed == 1 ? "http" : "https", IPAddress.Parse(clients[3 - consumed]), "public.example"),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "\"198.51.100.100:4567\", [::ffff:10.0.0.11]:1234, 10.0.0.12",
                ["X-Forwarded-Proto"] = invalidEarlierValues ? "not a scheme, http" : "https, http",
                ["X-Forwarded-Host"] = invalidEarlierValues ? "[broken, public.example" : "\"public.example\""
            }, control: new ForwardTrustControl("10.0.0.20", new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), "localhost")));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
    }

    [Fact]
    public async Task AnUnstableHopTrialCannotBeHiddenByALaterMatchingToken()
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
            options.ForwardLimit = 2;
        });
        var scenario = new ForwardTrustScenario("unstable-hop-trial", "10.0.0.10", new ForwardedIdentity("https", IPAddress.Parse("10.0.0.10")),
            ForwardTrustHeaderExpectation.Accepted, new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https, https" },
            control: new ForwardTrustControl("10.0.0.20", new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"))));
        var sender = TestHostHarness.CreateSender(host.Server);
        var calls = 0;
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], async (request, cancellationToken) =>
        {
            var identity = await sender(request, cancellationToken);
            return ++calls == 5 ? new ForwardedIdentity("drift", identity.ClientAddress, identity.Host) : identity;
        });
        Assert.False(result.Succeeded);
        Assert.Equal(8, calls);
    }
}

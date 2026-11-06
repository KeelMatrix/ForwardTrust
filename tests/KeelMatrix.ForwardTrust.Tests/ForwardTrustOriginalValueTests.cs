using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustOriginalValueTests
{
    private static readonly IPAddress Trusted = IPAddress.Parse("10.0.0.10");
    private static readonly IPAddress Untrusted = IPAddress.Parse("10.0.0.20");

    [Theory]
    [InlineData("X-Forwarded-Host", "blocked.example", false)]
    [InlineData("X-Forwarded-Host", "blocked.example", true)]
    [InlineData("X-Forwarded-For", "unknown", false)]
    [InlineData("X-Forwarded-For", "unknown", true)]
    [InlineData("X-Forwarded-Proto", "not a scheme", false)]
    [InlineData("X-Forwarded-Proto", "not a scheme", true)]
    [InlineData("X-Forwarded-Host", "", false)]
    [InlineData("X-Forwarded-For", "", false)]
    [InlineData("X-Forwarded-Proto", "", false)]
    [InlineData("X-Forwarded-Host", "", true)]
    [InlineData("X-Forwarded-For", "", true)]
    [InlineData("X-Forwarded-Proto", "", true)]
    [InlineData("X-Forwarded-Host", "[broken", true)]
    [InlineData("X-Forwarded-For", "unknown, , \"\"", true)]
    [InlineData("X-Forwarded-Proto", "\"\", not a scheme", true)]
    public async Task BaselineExpectationsCannotSubstituteForOriginalAcceptance(string header, string value, bool rejectedPrimary)
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = Flag(header);
            options.KnownProxies.Clear();
            options.KnownProxies.Add(Trusted);
            options.AllowedHosts.Add("counterfactual.example");
        });
        var scenario = Pair(header, value, new ForwardedIdentity("http", Trusted, "localhost"), rejectedPrimary);
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("X-Forwarded-Proto", "https, http", "http")]
    [InlineData("X-Forwarded-Host", "public.example, localhost", "localhost")]
    [InlineData("X-Forwarded-For", "198.51.100.10, 10.0.0.10", "10.0.0.10")]
    public async Task AppliedOriginalEqualToBaselineCanPassAtAPartialHop(string header, string value, string expected)
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = Flag(header);
            options.KnownProxies.Clear();
            options.KnownProxies.Add(Trusted);
            options.ForwardLimit = 1;
        });
        var identity = new ForwardedIdentity(header == "X-Forwarded-Proto" ? expected : "http",
            header == "X-Forwarded-For" ? IPAddress.Parse(expected) : Trusted,
            header == "X-Forwarded-Host" ? expected : null);
        var result = await new ForwardTrustVerifier().VerifyAsync([Pair(header, value, identity, false)], TestHostHarness.CreateSender(host.Server));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HostPolicyRejectionCannotHideBehindAnIdenticalBaseline(bool rejectedPrimary, bool allowOriginal)
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Add(Trusted);
            options.AllowedHosts.Add("counterfactual.example");
            if (allowOriginal) { options.AllowedHosts.Add("localhost"); }
        });
        var scenario = Pair("X-Forwarded-Host", "localhost", new ForwardedIdentity("http", Trusted, "localhost"), rejectedPrimary);
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.Equal(allowOriginal, result.Succeeded);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GeneratedHostBaselineVariantsRespectHeaderCountBounds(bool rejectedPrimary, bool existingHost)
    {
        var headers = new Dictionary<string, string> { ["X-Forwarded-Host"] = "localhost" };
        if (existingHost) { headers["Host"] = "localhost"; }
        while (headers.Count < 64) { headers[$"X-Padding-{headers.Count}"] = "a"; }
        var peer = rejectedPrimary ? Untrusted : Trusted;
        var other = rejectedPrimary ? Trusted : Untrusted;
        var scenario = new ForwardTrustScenario("host-baseline-bounds", peer.ToString(), new ForwardedIdentity("http", peer, "localhost"),
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl(other.ToString(), new ForwardedIdentity("http", other, "localhost")));
        var calls = 0;
        _ = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) =>
        {
            calls++;
            Assert.InRange(request.Headers.Count, 1, 64);
            return ValueTask.FromResult(new ForwardedIdentity("http", request.ImmediatePeerAddress, "localhost"));
        });
        Assert.Equal(existingHost, calls > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContradictoryMultiHopExpectationsFailBeforeAnyRequest(bool rejectedPrimary)
    {
        var calls = 0;
        var scenario = Pair("X-Forwarded-Proto", "https, custom", new ForwardedIdentity("http", Trusted), rejectedPrimary);
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(request.Scenario.ExpectedIdentity);
        });
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, f => f.Kind == ForwardTrustFailureKind.MalformedScenario);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "1", false)]
    [InlineData("X-Forwarded-For", "1", true)]
    [InlineData("X-Forwarded-Proto", "a", false)]
    [InlineData("X-Forwarded-Proto", "a", true)]
    [InlineData("X-Forwarded-Host", "a", false)]
    [InlineData("X-Forwarded-Host", "a", true)]
    public async Task LateAndRepeatedHopCandidatesRespectGeneratedBounds(string header, string value, bool rejectedPrimary)
    {
        var accepted = new ForwardedIdentity(header == "X-Forwarded-Proto" ? value : "http",
            header == "X-Forwarded-For" ? IPAddress.Parse("0.0.0.1") : Trusted,
            header == "X-Forwarded-Host" ? value : null);
        foreach (var duplicate in new[] { false, true })
        {
            var prefix = duplicate ? value + "," : "";
            var original = prefix + new string('b', 16 * 1024 - prefix.Length - 2) + "," + value;
            var scenario = Pair(header, original, accepted, rejectedPrimary);
            var calls = 0;
            var result = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) =>
            {
                calls++;
                return ValueTask.FromResult(request.Scenario.ExpectedIdentity);
            });
            Assert.Equal(0, calls);
            Assert.Contains(result.Failures, f => f.Kind == ForwardTrustFailureKind.MalformedScenario
                && f.Message.Contains("generated counterfactual", StringComparison.Ordinal));
        }
    }

    private static ForwardedHeaders Flag(string header) => header switch
    {
        "X-Forwarded-For" => ForwardedHeaders.XForwardedFor,
        "X-Forwarded-Proto" => ForwardedHeaders.XForwardedProto,
        _ => ForwardedHeaders.XForwardedHost
    };

    private static ForwardTrustScenario Pair(string header, string value, ForwardedIdentity accepted, bool rejectedPrimary)
    {
        var rejected = new ForwardedIdentity("http", Untrusted, accepted.Host is null ? null : "localhost");
        return new ForwardTrustScenario("original-value", (rejectedPrimary ? Untrusted : Trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { [header] = value },
            control: new ForwardTrustControl((rejectedPrimary ? Trusted : Untrusted).ToString(), rejectedPrimary ? accepted : rejected));
    }
}

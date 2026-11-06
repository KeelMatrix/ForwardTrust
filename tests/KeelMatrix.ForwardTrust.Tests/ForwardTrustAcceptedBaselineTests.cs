using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustAcceptedBaselineTests
{
    public static IEnumerable<object[]> HostCases()
    {
        foreach (var equalPeerBaselines in new[] { false, true })
            foreach (var overwrite in new[] { false, true })
                foreach (var originalMatchesBaseline in new[] { false, true })
                    foreach (var allowOriginal in new[] { false, true })
                        foreach (var rejectedPrimary in new[] { false, true })
                            yield return [equalPeerBaselines, overwrite, originalMatchesBaseline, allowOriginal, rejectedPrimary];
    }

    [Theory]
    [MemberData(nameof(HostCases))]
    public async Task OriginalHostRequiresItsOwnObservableContribution(
        bool equalPeerBaselines, bool overwrite, bool originalMatchesBaseline, bool allowOriginal, bool rejectedPrimary)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        var baseline = equalPeerBaselines ? "localhost" : "baseline.example";
        var original = originalMatchesBaseline ? baseline : "public.example";
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.AllowedHosts.Add("counterfactual.example");
            if (allowOriginal) { options.AllowedHosts.Add(original); }
        }, null, null, beforeForwarding: app => app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress!.Equals(trusted)
                && (overwrite || context.Request.Host == new HostString("localhost")))
            {
                context.Request.Host = new HostString(baseline);
            }
            await next();
        }));
        // Verify middleware consumption independently from the final identity.
        var context = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = trusted;
            context.Request.Host = new HostString("localhost");
            context.Request.Headers["X-Forwarded-Host"] = original;
        });
        Assert.Equal(allowOriginal, context.Request.Headers.ContainsKey("X-Original-Host"));
        Assert.Equal(!allowOriginal, context.Request.Headers.ContainsKey("X-Forwarded-Host"));
        Assert.Equal(allowOriginal ? original : baseline, context.Request.Host.ToString());

        var accepted = new ForwardedIdentity("http", trusted, allowOriginal ? original : baseline);
        var rejected = new ForwardedIdentity("http", untrusted, "localhost");
        var scenario = new ForwardTrustScenario("own-host-baseline", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-Host"] = original },
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));
        Assert.Equal(allowOriginal && (!originalMatchesBaseline || !overwrite), result.Succeeded);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OverwrittenAcceptedBaselineCannotProveRejectedOriginal(bool equalBaselines, bool rejectedPrimary)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        var original = equalBaselines ? "localhost" : "blocked.example";
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.AllowedHosts.Add("counterfactual.example");
        }, null, null, beforeForwarding: app => app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress!.Equals(trusted))
            {
                context.Request.Host = new HostString(original);
            }
            await next();
        }));
        var accepted = new ForwardedIdentity("http", trusted, original);
        var rejected = new ForwardedIdentity("http", untrusted, "localhost");
        var scenario = new ForwardTrustScenario("accepted-baseline", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-Host"] = original },
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var originalRequests = 0;
        var sender = TestHostHarness.CreateSender(host.Server, afterSend: context =>
        {
            if (context.Connection.RemoteIpAddress!.Equals(trusted)
                && context.Request.Headers["X-Forwarded-Host"].ToString() == original)
            {
                originalRequests++;
                Assert.False(context.Request.Headers.ContainsKey("X-Original-Host"));
                Assert.Equal(original, context.Request.Host.ToString());
            }
        });
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], sender);
        Assert.True(originalRequests > 0);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortAddressChainCanContinueDependentSchemeThroughTrustedAddress(bool rejectedPrimary)
    {
        var trusted = IPAddress.Parse("10.0.0.10");
        var untrusted = IPAddress.Parse("10.0.0.20");
        var forwarded = IPAddress.Parse("10.0.0.11");
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            options.KnownProxies.Add(trusted);
            options.KnownProxies.Add(forwarded);
            options.ForwardLimit = 2;
            options.RequireHeaderSymmetry = false;
        });
        var accepted = new ForwardedIdentity("https", forwarded);
        var rejected = new ForwardedIdentity("http", untrusted);
        var scenario = new ForwardTrustScenario("dependent-scheme", (rejectedPrimary ? untrusted : trusted).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-For"] = "10.0.0.11", ["X-Forwarded-Proto"] = "https, http" },
            control: new ForwardTrustControl((rejectedPrimary ? trusted : untrusted).ToString(), rejectedPrimary ? accepted : rejected));
        var originalsConsumed = 0;
        var sender = TestHostHarness.CreateSender(host.Server, afterSend: context =>
        {
            if (context.Connection.RemoteIpAddress!.Equals(forwarded) && context.Request.Scheme == "https")
            {
                originalsConsumed++;
                Assert.False(context.Request.Headers.ContainsKey("X-Forwarded-For"));
                Assert.False(context.Request.Headers.ContainsKey("X-Forwarded-Proto"));
                Assert.True(context.Request.Headers.ContainsKey("X-Original-For"));
                Assert.True(context.Request.Headers.ContainsKey("X-Original-Proto"));
            }
        });
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], sender);
        Assert.True(originalsConsumed > 0);
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => $"{f.Kind}: {f.Message}")));
    }
}

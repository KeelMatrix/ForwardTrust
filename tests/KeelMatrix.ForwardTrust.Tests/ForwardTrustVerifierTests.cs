using System.Net;
using KeelMatrix.ForwardTrust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustVerifierTests
{
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.0.0.10");
    private static readonly IPAddress SecondTrustedProxy = IPAddress.Parse("10.0.0.11");
    private static readonly IPAddress UntrustedProxy = IPAddress.Parse("10.0.0.20");
    private static readonly IPAddress ExpectedClient = IPAddress.Parse("198.51.100.10");

    [Fact]
    public async Task TrustedProxyAppliesForwardedIdentity()
    {
        await using var host = await TestHostHarness.StartAsync(ConfigureExactProxies);
        var scenario = Scenario("trusted-proxy", TrustedProxy, ForwardTrustHeaderExpectation.Accepted, ExpectedClient, "https", "public.example");

        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded, FailureText(result));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task UntrustedProxyRejectsSpoofedForwardedIdentity()
    {
        await using var host = await TestHostHarness.StartAsync(ConfigureExactProxies);
        var scenario = Scenario("untrusted-proxy", UntrustedProxy, ForwardTrustHeaderExpectation.Rejected, UntrustedProxy, "http", null);

        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded, FailureText(result));
        Assert.DoesNotContain(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.UntrustedHeaderAccepted);
    }

    [Fact]
    public async Task MultiHopForwardingHonorsForwardLimitAndKnownProxies()
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            ConfigureExactProxies(options);
            options.ForwardLimit = 2;
        });
        var scenario = new ForwardTrustScenario(
            "multi-hop",
            SecondTrustedProxy.ToString(),
            new ForwardedIdentity("https", ExpectedClient, "public.example"),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Forwarded-For"] = $"{ExpectedClient}, {TrustedProxy}",
                ["X-Forwarded-Proto"] = "https, http",
                ["X-Forwarded-Host"] = "public.example, internal.example"
            },
            control: RejectedControl(UntrustedProxy, assertHost: true));

        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task HostIsCheckedOnlyWhenExplicitlyAsserted()
    {
        await using var host = await TestHostHarness.StartAsync(ConfigureExactProxies);
        var sender = TestHostHarness.CreateSender(host.Server);
        var noHostAssertion = Scenario("scheme-only", TrustedProxy, ForwardTrustHeaderExpectation.Accepted, ExpectedClient, "https", null);
        var explicitHostAssertion = Scenario("host-assertion", TrustedProxy, ForwardTrustHeaderExpectation.Accepted, ExpectedClient, "https", "public.example");

        var noHostResult = await new ForwardTrustVerifier().VerifyAsync([noHostAssertion], sender);
        var explicitHostResult = await new ForwardTrustVerifier().VerifyAsync([explicitHostAssertion], sender);

        Assert.True(noHostResult.Succeeded, FailureText(noHostResult));
        Assert.True(explicitHostResult.Succeeded, FailureText(explicitHostResult));
    }

    [Fact]
    public async Task NetworkBoundaryIsTestedWithKnownNetworks()
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
#pragma warning disable ASPDEPR005
            options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
            options.KnownProxies.Clear();
#pragma warning disable ASPDEPR005
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("10.0.0.0"), 24));
#pragma warning restore ASPDEPR005
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        });
        var good = new ForwardTrustScenario(
            "network-boundary-inside",
            "10.0.0.25",
            new ForwardedIdentity("https", IPAddress.Parse("10.0.0.25")),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            control: RejectedControl(IPAddress.Parse("10.0.1.25")));
        var bad = new ForwardTrustScenario(
            "network-boundary-outside",
            "10.0.1.25",
            new ForwardedIdentity("http", IPAddress.Parse("10.0.1.25")),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            control: new ForwardTrustControl(
                "10.0.0.25",
                new ForwardedIdentity("https", IPAddress.Parse("10.0.0.25"))));

        var result = await new ForwardTrustVerifier().VerifyAsync([good, bad], TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task InvalidScenarioIsReportedBeforeSenderRuns()
    {
        var calls = 0;
        var malformed = new ForwardTrustScenario(
            "malformed",
            "not-an-ip",
            new ForwardedIdentity("http", IPAddress.Loopback),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["Bad Header"] = "value" });

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [malformed],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", IPAddress.Loopback));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
    }

    [Fact]
    public async Task MissingOrSamePeerControlsAreRejectedBeforeSenderRuns()
    {
        var calls = 0;
        var headers = new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" };
        var missing = new ForwardTrustScenario(
            "missing-control",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", TrustedProxy),
            ForwardTrustHeaderExpectation.Accepted,
            headers);
        var samePeer = new ForwardTrustScenario(
            "same-peer-control",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", TrustedProxy),
            ForwardTrustHeaderExpectation.Accepted,
            headers,
            control: new ForwardTrustControl(
                TrustedProxy.ToString(),
                new ForwardedIdentity("http", TrustedProxy)));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [missing, samePeer],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("https", TrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.All(result.Scenarios, scenario => Assert.Contains(
            scenario.Failures,
            failure => failure.Kind == ForwardTrustFailureKind.RequestApplicationNotProven));
    }

    [Fact]
    public async Task MismatchedTrustBoundaryHasStructuredRedactedDiagnostics()
    {
        var scenario = Scenario("unsafe-boundary", UntrustedProxy, ForwardTrustHeaderExpectation.Rejected, UntrustedProxy, "http", null);
        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            MisconfiguredSender);
        var text = string.Join("\n", result.Failures.Select(failure => failure.Message));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.UntrustedHeaderAccepted);
        Assert.DoesNotContain("Bearer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response body", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SchemeAndHostMismatchesHaveDistinctDimensions()
    {
        var scenario = Scenario("identity-mismatch", TrustedProxy, ForwardTrustHeaderExpectation.Accepted, ExpectedClient, "https", "public.example");
        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) => ValueTask.FromResult(new ForwardedIdentity("http", ExpectedClient, "wrong.example")));

        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.SchemeMismatch && failure.Dimension == ForwardTrustDimension.Scheme);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.HostMismatch && failure.Dimension == ForwardTrustDimension.Host);
    }

    [Fact]
    public async Task MultiHopMismatchIdentifiesForwardLimit()
    {
        var scenario = new ForwardTrustScenario(
            "forward-limit-mismatch",
            SecondTrustedProxy.ToString(),
            new ForwardedIdentity("https", ExpectedClient),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = $"{ExpectedClient}, {TrustedProxy}",
                ["X-Forwarded-Proto"] = "https, http"
            },
            control: RejectedControl(UntrustedProxy));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) => ValueTask.FromResult(new ForwardedIdentity("http", TrustedProxy)));

        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.ForwardLimitMismatch);
    }

    [Fact]
    public async Task ProbeFailureAndTimeoutAreBounded()
    {
        var scenario = Scenario("probe-timeout", TrustedProxy, ForwardTrustHeaderExpectation.Accepted, ExpectedClient, "https", null);
        var verifier = new ForwardTrustVerifier(new ForwardTrustVerifierOptions { RequestTimeout = TimeSpan.FromMilliseconds(20) });

        var result = await verifier.VerifyAsync(
            [scenario],
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                return new ForwardedIdentity("http", IPAddress.Loopback);
            });

        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.HostProbeFailure);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scenario = Scenario("cancelled", TrustedProxy, ForwardTrustHeaderExpectation.Accepted, ExpectedClient, "https", null);

        await Assert.ThrowsAsync<OperationCanceledException>(() => new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) => ValueTask.FromResult(new ForwardedIdentity("http", IPAddress.Loopback)),
            cancellation.Token));
    }

    [Fact]
    public void DefaultOptionsAreFiniteAndConservative()
    {
        var options = new ForwardTrustVerifierOptions();

        Assert.InRange(options.MaxScenarioCount, 1, 32);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RequestTimeout);
    }

    private static ForwardTrustScenario Scenario(
        string name,
        IPAddress peer,
        ForwardTrustHeaderExpectation expectation,
        IPAddress expectedClient,
        string expectedScheme,
        string? expectedHost)
    {
        return new ForwardTrustScenario(
            name,
            peer.ToString(),
            new ForwardedIdentity(expectedScheme, expectedClient, expectedHost),
            expectation,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Forwarded-For"] = ExpectedClient.ToString(),
                ["X-Forwarded-Proto"] = "https",
                ["X-Forwarded-Host"] = "public.example",
                ["Authorization"] = "Bearer synthetic-test-value",
                ["Cookie"] = "session=synthetic-test-value"
            },
            control: expectation == ForwardTrustHeaderExpectation.Accepted
                ? RejectedControl(UntrustedProxy, expectedHost is not null)
                : new ForwardTrustControl(
                    TrustedProxy.ToString(),
                    new ForwardedIdentity("https", ExpectedClient, "public.example")));
    }

    private static ForwardTrustControl RejectedControl(IPAddress peer, bool assertHost = false)
    {
        return new ForwardTrustControl(
            peer.ToString(),
            new ForwardedIdentity("http", peer, assertHost ? "localhost" : null));
    }

    private static string FailureText(ForwardTrustResult result)
    {
        return string.Join(" | ", result.Failures.Select(static failure => $"{failure.Kind}:{failure.Dimension}:{failure.Message}"));
    }

    private static void ConfigureExactProxies(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;
        options.KnownProxies.Clear();
        options.KnownProxies.Add(TrustedProxy);
        options.KnownProxies.Add(SecondTrustedProxy);
        options.ForwardLimit = 1;
    }

    private static ValueTask<ForwardedIdentity> MisconfiguredSender(ForwardTrustRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Headers.TryGetValue("X-Forwarded-Proto", out var scheme)
            && request.Headers.TryGetValue("X-Forwarded-For", out var client)
            && IPAddress.TryParse(client, out var clientAddress))
        {
            request.Headers.TryGetValue("X-Forwarded-Host", out var host);
            return ValueTask.FromResult(new ForwardedIdentity(scheme, clientAddress, host));
        }

        return ValueTask.FromResult(new ForwardedIdentity("http", request.ImmediatePeerAddress));
    }
}

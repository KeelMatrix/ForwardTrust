using System.Net;
using KeelMatrix.ForwardTrust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustRegressionTests
{
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.0.0.10");
    private static readonly IPAddress SecondTrustedProxy = IPAddress.Parse("10.0.0.11");
    private static readonly IPAddress ThirdTrustedProxy = IPAddress.Parse("10.0.0.12");
    private static readonly IPAddress UntrustedProxy = IPAddress.Parse("10.0.0.20");
    private static readonly IPAddress ExpectedClient = IPAddress.Parse("198.51.100.10");
    private readonly ITestOutputHelper output;

    public ForwardTrustRegressionTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task ReviewersRejectedScenarioFalseSafeProbesFailClosed()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = ExpectedClient.ToString(),
            ["X-Forwarded-Proto"] = "https"
        };
        var droppedHeaders = new ForwardTrustScenario(
            "untrusted-peer-applied-headers-dropped",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            control: TrustedAcceptedControl());
        var staleObservation = new ForwardTrustScenario(
            "untrusted-stale-cached-observation",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            control: TrustedAcceptedControl());
        var crossScenarioOne = new ForwardTrustScenario(
            "cross-scenario-one",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            control: TrustedAcceptedControl());
        var crossScenarioTwo = new ForwardTrustScenario(
            "cross-scenario-two",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            control: TrustedAcceptedControl());
        var cached = new ForwardedIdentity("http", UntrustedProxy);

        var droppedResult = await new ForwardTrustVerifier().VerifyAsync(
            [droppedHeaders],
            static (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ForwardedIdentity("http", request.ImmediatePeerAddress));
            });
        var staleResult = await new ForwardTrustVerifier().VerifyAsync(
            [staleObservation],
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(request.Headers.Count == 0
                    ? new ForwardedIdentity("http", request.ImmediatePeerAddress)
                    : cached);
            });
        var crossScenarioResult = await new ForwardTrustVerifier().VerifyAsync(
            [crossScenarioOne, crossScenarioTwo],
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(request.Headers.Count == 0
                    ? new ForwardedIdentity("http", request.ImmediatePeerAddress)
                    : cached);
            });

        WriteProbeResult(droppedHeaders.Name, droppedResult);
        WriteProbeResult(staleObservation.Name, staleResult);
        WriteProbeResult("cross-scenario-control-vouching", crossScenarioResult);
        Assert.False(droppedResult.Succeeded);
        Assert.False(staleResult.Succeeded);
        Assert.False(crossScenarioResult.Succeeded);
    }

    [Fact]
    public async Task RealTrustedAndUntrustedPipelinesSatisfyScenarioLocalControls()
    {
        await using var host = await TestHostHarness.StartAsync(ConfigureExactProxies);
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = ExpectedClient.ToString(),
            ["X-Forwarded-Proto"] = "https"
        };
        var trusted = new ForwardTrustScenario(
            "real-trusted-control-pair",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", ExpectedClient),
            ForwardTrustHeaderExpectation.Accepted,
            headers,
            control: RejectedControl(UntrustedProxy));
        var untrusted = new ForwardTrustScenario(
            "real-untrusted-control-pair",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            control: TrustedAcceptedControl());

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [trusted, untrusted],
            TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded);
        Assert.All(result.Scenarios, scenario => Assert.True(scenario.Succeeded));
    }

    [Fact]
    public async Task ReviewersDefaultCoincidenceProbesFailClosedWithNoOpSender()
    {
        var trusted = new ForwardTrustScenario(
            "trusted-default-coincidence",
            TrustedProxy.ToString(),
            new ForwardedIdentity("http", TrustedProxy),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = TrustedProxy.ToString(),
                ["X-Forwarded-Proto"] = "http"
            },
            control: RejectedControl(UntrustedProxy));
        var untrusted = new ForwardTrustScenario(
            "untrusted-default-coincidence",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = ExpectedClient.ToString(),
                ["X-Forwarded-Proto"] = "https"
            },
            control: TrustedAcceptedControl());

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [trusted, untrusted],
            static (_, _) => ValueTask.FromResult(new ForwardedIdentity("http", TrustedProxy)));

        Assert.False(result.Succeeded);
        Assert.All(result.Scenarios, scenario => Assert.Contains(
            scenario.Failures,
            failure => failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven));
    }

    [Fact]
    public async Task DefaultCoincidenceRequiresAWorkingCounterfactual()
    {
        await using var host = await TestHostHarness.StartAsync(ConfigureExactProxies);
        var scenario = new ForwardTrustScenario(
            "trusted-default-counterfactual",
            TrustedProxy.ToString(),
            new ForwardedIdentity("http", TrustedProxy),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = TrustedProxy.ToString(),
                ["X-Forwarded-Proto"] = "http"
            },
            control: RejectedControl(UntrustedProxy));

        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task CounterfactualValuesCannotCollideWithAcceptedObservations()
    {
        var acceptedClient = IPAddress.Parse("203.0.113.254");
        await using var host = await TestHostHarness.StartAsync(ConfigureExactProxies);
        var scenario = new ForwardTrustScenario(
            "counterfactual-collision",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", acceptedClient, "counterfactual.example"),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = acceptedClient.ToString(),
                ["X-Forwarded-Proto"] = "https",
                ["X-Forwarded-Host"] = "counterfactual.example"
            },
            control: RejectedControl(UntrustedProxy, assertHost: true));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task UnrelatedCounterfactualChangeFailsClosed()
    {
        var scenario = new ForwardTrustScenario(
            "unrelated-counterfactual-change",
            TrustedProxy.ToString(),
            new ForwardedIdentity("http", TrustedProxy, "public.example"),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "public.example"
            },
            control: RejectedControl(UntrustedProxy, assertHost: true));
        var calls = 0;

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls++;
                return ValueTask.FromResult(calls switch
                {
                    1 => scenario.ExpectedIdentity,
                    2 => scenario.Control!.ExpectedIdentity,
                    3 => new ForwardedIdentity("http", TrustedProxy, "counterfactual.example"),
                    _ => scenario.Control!.ExpectedIdentity
                });
            });

        WriteProbeResult(scenario.Name, result);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven
            && failure.Message.StartsWith("Reissuing an identical", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CounterfactualMustProduceTheAssertedHeaderValue()
    {
        var scenario = new ForwardTrustScenario(
            "unrelated-stable-counterfactual-change",
            TrustedProxy.ToString(),
            new ForwardedIdentity("http", TrustedProxy, "public.example"),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "public.example"
            },
            control: RejectedControl(UntrustedProxy, assertHost: true));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.ImmediatePeerAddress.Equals(UntrustedProxy))
                {
                    return ValueTask.FromResult(scenario.Control!.ExpectedIdentity);
                }

                var host = request.Headers["X-Forwarded-Host"] == "public.example"
                    ? "public.example"
                    : "unrelated.example";
                return ValueTask.FromResult(new ForwardedIdentity("http", TrustedProxy, host));
            });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven
            && failure.Message.Contains("generated forwarded-header counterfactual", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachForwardedDimensionNeedsItsOwnAttribution()
    {
        var scenario = new ForwardTrustScenario(
            "per-dimension-attribution",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", ExpectedClient),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = ExpectedClient.ToString(),
                ["X-Forwarded-Proto"] = "https"
            },
            control: RejectedControl(UntrustedProxy));

        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], static (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scheme = request.Headers.TryGetValue("X-Forwarded-Proto", out var value) ? value : "http";
            return ValueTask.FromResult(new ForwardedIdentity(scheme, request.ImmediatePeerAddress));
        });

        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.ClientAddressMismatch
            && failure.Dimension == ForwardTrustDimension.ClientAddress);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven
            && failure.Dimension == ForwardTrustDimension.ClientAddress);
    }

    [Theory]
    [InlineData(2, null, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, null, false)]
    [InlineData(3, 1, false)]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, true)]
    public async Task RealThreeHopPipelineCoversForwardLimitMatrix(int hops, int? limit, bool expectedSuccess)
    {
        await using var host = await TestHostHarness.StartAsync(options => ConfigureMultiHop(options, limit, hops));
        var scenario = CreateMultiHopScenario(hops);

        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));

        Assert.Equal(expectedSuccess, result.Succeeded);
        if (expectedSuccess)
        {
            Assert.Equal(ExpectedClient, result.Scenarios[0].ObservedIdentity!.ClientAddress);
            Assert.Equal("https", result.Scenarios[0].ObservedIdentity!.Scheme);
        }
        else
        {
            Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.ForwardLimitMismatch);
        }
    }

    [Fact]
    public async Task IPv6KnownProxyAndNetworkBoundariesAreExercised()
    {
        var knownProxy = IPAddress.Parse("2001:db8::10");
        var expectedClient = IPAddress.Parse("2001:db8::20");
        await using var proxyHost = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownProxies.Add(TrustedProxy);
            options.KnownProxies.Add(knownProxy);
            options.ForwardLimit = 1;
        });
        var proxyScenario = new ForwardTrustScenario(
            "ipv6-known-proxy",
            knownProxy.ToString(),
            new ForwardedIdentity("https", expectedClient),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = expectedClient.ToString(),
                ["X-Forwarded-Proto"] = "https"
            },
            control: RejectedControl(UntrustedProxy));
        var proxyResult = await new ForwardTrustVerifier().VerifyAsync([proxyScenario], TestHostHarness.CreateSender(proxyHost.Server));

        await using var networkHost = await TestHostHarness.StartAsync(options =>
        {
#pragma warning disable ASPDEPR005
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("2001:db8:1::"), 64));
#pragma warning restore ASPDEPR005
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        });
        var inside = new ForwardTrustScenario(
            "ipv6-network-inside",
            "2001:db8:1::25",
            new ForwardedIdentity("https", IPAddress.Parse("2001:db8:1::25")),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            control: RejectedControl(IPAddress.Parse("2001:db8:2::25")));
        var outside = new ForwardTrustScenario(
            "ipv6-network-outside",
            "2001:db8:2::25",
            new ForwardedIdentity("http", IPAddress.Parse("2001:db8:2::25")),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            control: new ForwardTrustControl(
                "2001:db8:1::25",
                new ForwardedIdentity("https", IPAddress.Parse("2001:db8:1::25"))));
        var networkResult = await new ForwardTrustVerifier().VerifyAsync([inside, outside], TestHostHarness.CreateSender(networkHost.Server));

        Assert.True(proxyResult.Succeeded);
        Assert.True(networkResult.Succeeded);
    }

    [Fact]
    public async Task MissingAndMalformedHeadersUseTheRealPipeline()
    {
        await using var host = await TestHostHarness.StartAsync(ConfigureExactProxies);
        var missing = new ForwardTrustScenario(
            "missing-forwarded-proto",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", TrustedProxy),
            ForwardTrustHeaderExpectation.Accepted);
        var malformed = new ForwardTrustScenario(
            "malformed-forwarded-for",
            TrustedProxy.ToString(),
            new ForwardedIdentity("http", TrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "not-an-ip",
                ["X-Forwarded-Proto"] = "https"
            },
            control: new ForwardTrustControl(
                SecondTrustedProxy.ToString(),
                new ForwardedIdentity("https", SecondTrustedProxy)));

        var result = await new ForwardTrustVerifier().VerifyAsync([missing, malformed], TestHostHarness.CreateSender(host.Server));

        Assert.False(result.Scenarios[0].Succeeded);
        Assert.Contains(result.Scenarios[0].Failures, failure => failure.Kind == ForwardTrustFailureKind.SchemeMismatch);
        Assert.False(result.Scenarios[1].Succeeded);
        Assert.NotEmpty(result.Scenarios[1].Failures);
    }

    [Fact]
    public async Task SchemeForwardingControlsHttpsRedirectPath()
    {
        var statuses = new List<int>();
        await using var host = await TestHostHarness.StartAsync(
            ConfigureExactProxies,
            services => services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443),
            app => app.UseHttpsRedirection());
        var sender = TestHostHarness.CreateSender(host.Server, context => statuses.Add(context.Response.StatusCode));
        var trusted = new ForwardTrustScenario(
            "https-redirect-trusted",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", TrustedProxy),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            "/auth/callback",
            RejectedControl(UntrustedProxy));
        var trustedResult = await new ForwardTrustVerifier().VerifyAsync([trusted], sender);
        var trustedPrimaryStatus = statuses[0];
        statuses.Clear();
        var untrusted = new ForwardTrustScenario(
            "https-redirect-untrusted",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            "/auth/callback",
            new ForwardTrustControl(
                TrustedProxy.ToString(),
                new ForwardedIdentity("https", TrustedProxy)));
        var untrustedResult = await new ForwardTrustVerifier().VerifyAsync([untrusted], sender);

        Assert.True(trustedResult.Succeeded);
        Assert.Equal(StatusCodes.Status200OK, trustedPrimaryStatus);
        Assert.True(untrustedResult.Succeeded);
        Assert.Equal(StatusCodes.Status307TemporaryRedirect, statuses[0]);
    }

    [Fact]
    public async Task HostSetupFailuresAreDistinctFromTimeouts()
    {
        var scenario = new ForwardTrustScenario(
            "host-setup",
            TrustedProxy.ToString(),
            new ForwardedIdentity("http", TrustedProxy),
            ForwardTrustHeaderExpectation.Rejected);
        var verifier = new ForwardTrustVerifier(new ForwardTrustVerifierOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) });
        var nullResult = await verifier.VerifyAsync([scenario], static (_, _) => ValueTask.FromResult<ForwardedIdentity>(null!));
        var thrownResult = await verifier.VerifyAsync([scenario], static (_, _) => ValueTask.FromException<ForwardedIdentity>(new InvalidOperationException("setup")));

        Assert.Contains(nullResult.Failures, failure => failure.Kind == ForwardTrustFailureKind.HostSetupFailure);
        Assert.Contains(thrownResult.Failures, failure => failure.Kind == ForwardTrustFailureKind.HostSetupFailure);
        Assert.DoesNotContain(nullResult.Failures, failure => failure.Kind == ForwardTrustFailureKind.HostProbeFailure);
    }

    [Fact]
    public async Task MaxScenarioCountRejectsBeforeSending()
    {
        var calls = 0;
        var first = new ForwardTrustScenario("one", TrustedProxy.ToString(), new ForwardedIdentity("http", TrustedProxy), ForwardTrustHeaderExpectation.Rejected);
        var second = new ForwardTrustScenario("two", TrustedProxy.ToString(), new ForwardedIdentity("http", TrustedProxy), ForwardTrustHeaderExpectation.Rejected);
        var result = await new ForwardTrustVerifier(new ForwardTrustVerifierOptions { MaxScenarioCount = 1 }).VerifyAsync(
            [first, second],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", TrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains("maximum", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateAndContradictoryScenarioSetsAreRejectedBeforeSending()
    {
        var calls = 0;
        var duplicate = new ForwardTrustScenario("same", TrustedProxy.ToString(), new ForwardedIdentity("http", TrustedProxy), ForwardTrustHeaderExpectation.Rejected);
        var contradictory = new ForwardTrustScenario("contradictory", TrustedProxy.ToString(), new ForwardedIdentity("https", TrustedProxy), ForwardTrustHeaderExpectation.Accepted, new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" });
        var contradictoryCopy = new ForwardTrustScenario("contradictory", TrustedProxy.ToString(), new ForwardedIdentity("http", TrustedProxy), ForwardTrustHeaderExpectation.Rejected, new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" });

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [duplicate, duplicate, contradictory, contradictoryCopy],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", TrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Message.Contains("Duplicate scenario name", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Message.Contains("Contradictory expectations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoOpPeerSeamControlIsLoadBearing()
    {
        var scenario = new ForwardTrustScenario(
            "no-op-peer-seam",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", ExpectedClient),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = ExpectedClient.ToString(),
                ["X-Forwarded-Proto"] = "https"
            },
            control: RejectedControl(UntrustedProxy));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            static (_, _) => ValueTask.FromResult(new ForwardedIdentity("http", TrustedProxy)));

        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven);
    }

    [Fact]
    public async Task UnknownProxyServicingHardeningFixtureRejectsSpoofedHeaders()
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownProxies.Add(TrustedProxy);
#pragma warning disable ASPDEPR005
            options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
        });
        var scenario = new ForwardTrustScenario(
            "unknown-proxy-servicing-hardening",
            "203.0.113.20",
            new ForwardedIdentity("http", IPAddress.Parse("203.0.113.20")),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = ExpectedClient.ToString(),
                ["X-Forwarded-Proto"] = "https"
            },
            control: TrustedAcceptedControl());

        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task DocumentedQuickStartPathRunsAgainstTheRealPipeline()
    {
        await using var host = await TestHostHarness.StartAsync(ConfigureExactProxies);
        var scenario = new ForwardTrustScenario(
            "trusted-proxy",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", ExpectedClient, "public.example"),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = ExpectedClient.ToString(),
                ["X-Forwarded-Proto"] = "https",
                ["X-Forwarded-Host"] = "public.example"
            },
            control: RejectedControl(UntrustedProxy, assertHost: true));

        ForwardTrustRequestSender SendThroughTestHost = TestHostHarness.CreateSender(host.Server);
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], SendThroughTestHost);

        Assert.True(result.Succeeded);
    }

    private static ForwardTrustScenario CreateMultiHopScenario(int hops)
    {
        return new ForwardTrustScenario(
            $"real-{hops}-hop",
            hops == 2 ? SecondTrustedProxy.ToString() : ThirdTrustedProxy.ToString(),
            new ForwardedIdentity("https", ExpectedClient, "public.example"),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = hops == 2
                    ? $"{ExpectedClient}, {TrustedProxy}"
                    : $"{ExpectedClient}, {TrustedProxy}, {SecondTrustedProxy}",
                ["X-Forwarded-Proto"] = hops == 2 ? "https, http" : "https, http, http",
                ["X-Forwarded-Host"] = hops == 2 ? "public.example, internal.example" : "public.example, internal.example, edge.example"
            },
            control: RejectedControl(UntrustedProxy, assertHost: true));
    }

    private static void ConfigureMultiHop(ForwardedHeadersOptions options, int? limit, int hops)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;
        options.KnownProxies.Clear();
        options.KnownProxies.Add(TrustedProxy);
        options.KnownProxies.Add(SecondTrustedProxy);
        if (hops == 3)
        {
            options.KnownProxies.Add(ThirdTrustedProxy);
        }

        if (limit.HasValue)
        {
            options.ForwardLimit = limit.Value;
        }
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

    private void WriteProbeResult(string name, ForwardTrustResult result)
    {
        var failures = string.Join(",", result.Failures.Select(static failure => failure.Kind));
        output.WriteLine($"{name}: succeeded={result.Succeeded};failures={failures}");
    }

    private static ForwardTrustControl TrustedAcceptedControl()
    {
        return new ForwardTrustControl(
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", ExpectedClient));
    }

    private static ForwardTrustControl RejectedControl(IPAddress peer, bool assertHost = false)
    {
        return new ForwardTrustControl(
            peer.ToString(),
            new ForwardedIdentity("http", peer, assertHost ? "localhost" : null));
    }
}

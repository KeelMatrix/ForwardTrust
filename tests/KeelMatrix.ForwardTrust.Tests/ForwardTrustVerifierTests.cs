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

        Assert.True(result.Succeeded, FailureText(result));
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

    [Theory]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, null, null)]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, null, null)]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, "X-Client-IP", null)]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, "X-Client-IP", null)]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, "X-Forwarded-Host", null)]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, "X-Forwarded-Host", null)]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, "X-Forwarded-Host", "")]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, "X-Forwarded-Host", "")]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, "X-Forwarded-Host", "  ")]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, "X-Forwarded-Host", "  ")]
    public async Task ScenarioWithoutAnAssertedForwardedDimensionFailsBeforeSenderRuns(
        ForwardTrustHeaderExpectation expectation,
        string? headerName,
        string? expectedHost)
    {
        var calls = 0;
        IReadOnlyDictionary<string, string>? headers = headerName is null
            ? null
            : new Dictionary<string, string> { [headerName] = "public.example" };
        var scenario = new ForwardTrustScenario(
            "no-forwarded-dimension",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy, expectedHost),
            expectation,
            headers);

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven
            && failure.Dimension == ForwardTrustDimension.Headers);
    }

    [Fact]
    public async Task UndefinedHeaderExpectationIsReportedBeforeSenderRuns()
    {
        var calls = 0;
        var malformed = new ForwardTrustScenario(
            "undefined-header-expectation",
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", TrustedProxy),
            (ForwardTrustHeaderExpectation)42,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            control: RejectedControl(UntrustedProxy));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [malformed],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("https", TrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.MalformedScenario
            && failure.Message.Contains("Accepted or Rejected", StringComparison.Ordinal));
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
        Assert.DoesNotContain(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.ForwardLimitMismatch);
    }

    [Theory]
    [MemberData(nameof(ForwardLimitDiagnosticCases))]
    public async Task ForwardLimitMismatchRequiresAFailedAssertedDimensionWithMultipleHops(
        string headerName,
        int hopCount,
        bool assertHost,
        ForwardTrustHeaderExpectation expectation,
        bool failAssertedDimension)
    {
        const string trustedPeer = "10.0.0.10";
        const string untrustedPeer = "10.0.0.20";
        var expected = new ForwardedIdentity("https", ExpectedClient, assertHost ? "public.example" : null);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [headerName] = ForwardedHeaderChain(headerName, hopCount)
        };

        if (!headerName.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase))
        {
            headers["X-Forwarded-Host"] = assertHost
                ? "public.example"
                : ForwardedHeaderChain("X-Forwarded-Host", hopCount);
        }
        else if (!assertHost)
        {
            headers["X-Forwarded-For"] = ExpectedClient.ToString();
        }

        var targetDimension = headerName switch
        {
            "X-Forwarded-For" => ForwardTrustDimension.ClientAddress,
            "X-Forwarded-Proto" => ForwardTrustDimension.Scheme,
            _ => ForwardTrustDimension.Host
        };
        var failedDimension = failAssertedDimension
            ? targetDimension
            : targetDimension == ForwardTrustDimension.Scheme
                ? ForwardTrustDimension.ClientAddress
                : ForwardTrustDimension.Scheme;
        var observed = failedDimension switch
        {
            ForwardTrustDimension.Scheme => new ForwardedIdentity("http", expected.ClientAddress, expected.Host),
            ForwardTrustDimension.ClientAddress => new ForwardedIdentity("https", IPAddress.Parse("198.51.100.11"), expected.Host),
            ForwardTrustDimension.Host => new ForwardedIdentity("https", expected.ClientAddress, "wrong.example"),
            _ => throw new InvalidOperationException("Unexpected forwarded dimension.")
        };
        var primaryPeer = IPAddress.Parse(expectation == ForwardTrustHeaderExpectation.Accepted ? trustedPeer : untrustedPeer);
        var controlPeer = IPAddress.Parse(expectation == ForwardTrustHeaderExpectation.Accepted ? untrustedPeer : trustedPeer);
        var scenario = new ForwardTrustScenario(
            $"forward-limit-{headerName}-{hopCount}-{assertHost}-{expectation}-{failAssertedDimension}",
            primaryPeer.ToString(),
            expected,
            expectation,
            headers,
            control: new ForwardTrustControl(controlPeer.ToString(), expected));

        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(request.ImmediatePeerAddress.Equals(primaryPeer) ? observed : expected);
        });

        var expectsForwardLimitMismatch = failAssertedDimension
            && (headerName != "X-Forwarded-Host" || assertHost)
            && hopCount > 1;
        Assert.Equal(
            expectsForwardLimitMismatch,
            result.Failures.Any(failure => failure.Kind == ForwardTrustFailureKind.ForwardLimitMismatch));
    }

    public static IEnumerable<object[]> ForwardLimitDiagnosticCases()
    {
        foreach (var headerName in new[] { "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host" })
        foreach (var hopCount in new[] { 1, 2, 3 })
        foreach (var assertHost in new[] { false, true })
        foreach (var expectation in new[] { ForwardTrustHeaderExpectation.Accepted, ForwardTrustHeaderExpectation.Rejected })
        foreach (var failAssertedDimension in new[] { false, true })
        {
            if (headerName == "X-Forwarded-Host" && !assertHost && failAssertedDimension)
            {
                continue;
            }

            yield return [headerName, hopCount, assertHost, expectation, failAssertedDimension];
        }
    }

    private static string ForwardedHeaderChain(string headerName, int hopCount)
    {
        var values = headerName switch
        {
            "X-Forwarded-For" => new[] { ExpectedClient.ToString(), "10.0.0.11", "10.0.0.12" },
            "X-Forwarded-Proto" => new[] { "https", "http", "http" },
            _ => new[] { "public.example", "internal.example", "edge.example" }
        };

        return string.Join(", ", values.Take(hopCount));
    }

    [Fact]
    public async Task EmptyScenarioSetFailsClosedWithoutSenderInvocation()
    {
        var calls = 0;

        var result = await new ForwardTrustVerifier().VerifyAsync(
            Array.Empty<ForwardTrustScenario>(),
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
    public async Task ScenarioLimitStopsEnumerationAtOneOverTheConfiguredMaximum()
    {
        var enumerated = 0;
        var calls = 0;
        var first = Scenario("first", TrustedProxy, ForwardTrustHeaderExpectation.Rejected, TrustedProxy, "http", null);
        var second = Scenario("second", TrustedProxy, ForwardTrustHeaderExpectation.Rejected, TrustedProxy, "http", null);

        IEnumerable<ForwardTrustScenario> Scenarios()
        {
            enumerated++;
            yield return first;
            enumerated++;
            yield return second;
            throw new InvalidOperationException("The verifier enumerated beyond the configured intake bound.");
        }

        var result = await new ForwardTrustVerifier(new ForwardTrustVerifierOptions { MaxScenarioCount = 1 }).VerifyAsync(
            Scenarios(),
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", IPAddress.Loopback));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(2, enumerated);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
    }

    [Fact]
    public async Task PreCancelledVerificationDoesNotEnumerateScenarios()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new ForwardTrustVerifier().VerifyAsync(
            new ThrowOnEnumeration(),
            (_, _) => ValueTask.FromResult(new ForwardedIdentity("http", IPAddress.Loopback)),
            cancellation.Token));
    }

    [Fact]
    public async Task HeaderCountLimitRejectsBeforeSenderInvocation()
    {
        var calls = 0;
        var headers = Enumerable.Range(0, 65).ToDictionary(index => $"X-Test-{index}", _ => "value");
        var scenario = new ForwardTrustScenario(
            "too-many-headers",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers);

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
    }

    [Fact]
    public async Task HeaderCountAtLimitRemainsEligibleForValidation()
    {
        var expected = new ForwardedIdentity("http", UntrustedProxy);
        var headers = Enumerable.Range(0, 63).ToDictionary(index => $"X-Test-{index}", _ => "value");
        headers["X-Forwarded-For"] = ExpectedClient.ToString();
        var scenario = new ForwardTrustScenario(
            "maximum-header-count",
            UntrustedProxy.ToString(),
            expected,
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            control: new ForwardTrustControl(
                TrustedProxy.ToString(),
                new ForwardedIdentity("http", ExpectedClient)));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            ForwardForTrustedProxy);

        Assert.True(result.Succeeded, FailureText(result));
    }

    [Theory]
    [InlineData(false, false, false, false, "dictionary")]
    [InlineData(false, true, false, false, "dictionary")]
    [InlineData(true, true, false, false, "dictionary")]
    [InlineData(false, false, true, false, "dictionary")]
    [InlineData(false, true, false, true, "dictionary")]
    [InlineData(false, true, false, false, "sorted")]
    public async Task CaseInsensitiveHeaderNameCollisionsAreRejectedBeforeSenderInvocation(
        bool reverse,
        bool conflictingValues,
        bool countAtLimit,
        bool countOverLimit,
        string dictionaryKind)
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            new("X-Forwarded-For", "198.51.100.1"),
            new("x-forwarded-for", conflictingValues ? "198.51.100.2" : "198.51.100.1")
        };
        if (reverse)
        {
            entries.Reverse();
        }

        IDictionary<string, string> mutableHeaders = dictionaryKind == "sorted"
            ? new SortedDictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            mutableHeaders.Add(entry.Key, entry.Value);
        }

        var requestedCount = countOverLimit ? 65 : countAtLimit ? 64 : 2;
        for (var index = 0; mutableHeaders.Count < requestedCount; index++)
        {
            mutableHeaders.Add($"X-Other-{index}", "value");
        }

        var scenario = new ForwardTrustScenario(
            "case-variant-header-collision",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            (IReadOnlyDictionary<string, string>)mutableHeaders);
        var calls = 0;

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
        if (!countOverLimit)
        {
            Assert.Contains(result.Failures, failure =>
                failure.Kind == ForwardTrustFailureKind.MalformedScenario
                && failure.Message.Contains("unique without regard to case", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task MultipleCaseVariantsOfOneHeaderAreRejectedBeforeSenderInvocation()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Trace"] = "first",
            ["x-trace"] = "second",
            ["X-TRACE"] = "third"
        };
        var scenario = new ForwardTrustScenario(
            "three-case-variant-header-names",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers);
        var calls = 0;

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.MalformedScenario
            && failure.Message.Contains("unique without regard to case", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaseInsensitiveSourceComparerStillAcceptsAnUnambiguousHeader()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Test"] = "value",
            ["X-Forwarded-For"] = ExpectedClient.ToString()
        };
        var scenario = new ForwardTrustScenario(
            "case-insensitive-source-comparer",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            control: new ForwardTrustControl(
                TrustedProxy.ToString(),
                new ForwardedIdentity("http", ExpectedClient)));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            ForwardForTrustedProxy);

        Assert.True(result.Succeeded, FailureText(result));
        Assert.Equal("value", scenario.Headers["x-test"]);
    }

    [Theory]
    [InlineData(16_364, false)]
    [InlineData(16_365, true)]
    public async Task DuplicateHeaderNamesRemainRejectedAtAggregateSizeBoundary(int lastValueLength, bool exceedsLimit)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Trace"] = new string('a', 16_384),
            ["x-trace"] = new string('b', 16_384),
            ["X-A"] = new string('c', 16_384),
            ["X-B"] = new string('d', lastValueLength)
        };
        var scenario = new ForwardTrustScenario(
            "duplicate-header-at-aggregate-boundary",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            headers);
        var calls = 0;

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.MalformedScenario
            && failure.Message.Contains("unique without regard to case", StringComparison.Ordinal));
        Assert.Equal(exceedsLimit, result.Failures.Any(failure =>
            failure.Kind == ForwardTrustFailureKind.MalformedScenario
            && failure.Message.Contains("bounded count or aggregate-size limit", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task HeaderAggregateSizeLimitRejectsBeforeSenderInvocation()
    {
        var calls = 0;
        var scenario = new ForwardTrustScenario(
            "headers-too-large",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["X-Test"] = new string('x', 64 * 1024) });

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
    }

    [Fact]
    public async Task HeaderAggregateSizeAtLimitRemainsEligibleForValidation()
    {
        var expected = new ForwardedIdentity("http", UntrustedProxy);
        var headers = new Dictionary<string, string>
        {
            ["X-Test-0"] = new string('x', 16_384),
            ["X-Test-1"] = new string('x', 16_384),
            ["X-Test-2"] = new string('x', 16_384),
            ["X-Test-3"] = new string('x', 16_324),
            ["X-Forwarded-For"] = ExpectedClient.ToString()
        };
        var scenario = new ForwardTrustScenario(
            "maximum-header-bytes",
            UntrustedProxy.ToString(),
            expected,
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            control: new ForwardTrustControl(
                TrustedProxy.ToString(),
                new ForwardedIdentity("http", ExpectedClient)));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            ForwardForTrustedProxy);

        Assert.True(result.Succeeded, FailureText(result));
    }

    [Fact]
    public async Task HeaderValueOverLimitRejectsBeforeSenderInvocation()
    {
        var calls = 0;
        var scenario = new ForwardTrustScenario(
            "header-value-too-large",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["X-Test"] = new string('x', 16_385) });

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("Bad:Header")]
    [InlineData("Bad,Header")]
    [InlineData("Bad;Header")]
    [InlineData("Bad(Header)")]
    [InlineData("Bad\0Header")]
    [InlineData("Bad\u007fHeader")]
    public async Task InvalidHeaderNameTokenClassesAreRejectedBeforeSenderInvocation(string headerName)
    {
        var calls = 0;
        var scenario = new ForwardTrustScenario(
            "invalid-header-name",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { [headerName] = "value" });

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
    }

    [Theory]
    [InlineData("bad\u0001value")]
    [InlineData("bad\u007fvalue")]
    [InlineData("bad\rvalue")]
    [InlineData("bad\nvalue")]
    public async Task InvalidHeaderValueControlClassesAreRejectedBeforeSenderInvocation(string headerValue)
    {
        var calls = 0;
        var scenario = new ForwardTrustScenario(
            "invalid-header-value",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["X-Test"] = headerValue });

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy));
            });

        Assert.False(result.Succeeded);
        Assert.Equal(0, calls);
        Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario);
    }

    [Fact]
    public async Task LegalHeaderValueCharactersRemainAccepted()
    {
        var peer = UntrustedProxy.ToString();
        var expected = new ForwardedIdentity("http", UntrustedProxy);
        var scenario = new ForwardTrustScenario(
            "legal-header-value",
            peer,
            expected,
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string>
            {
                ["X-Test"] = "value\twith\u0080",
                ["X-Forwarded-For"] = ExpectedClient.ToString()
            },
            control: new ForwardTrustControl(
                TrustedProxy.ToString(),
                new ForwardedIdentity("http", ExpectedClient)));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            ForwardForTrustedProxy);

        Assert.True(result.Succeeded, FailureText(result));
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

    private static ValueTask<ForwardedIdentity> ForwardForTrustedProxy(
        ForwardTrustRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clientAddress = request.ImmediatePeerAddress.Equals(TrustedProxy)
            && request.Headers.TryGetValue("X-Forwarded-For", out var value)
            && IPAddress.TryParse(value, out var forwardedAddress)
                ? forwardedAddress
                : request.ImmediatePeerAddress;
        return ValueTask.FromResult(new ForwardedIdentity("http", clientAddress));
    }

    private static string FailureText(ForwardTrustResult result)
    {
        return string.Join(" | ", result.Failures.Select(static failure => $"{failure.Kind}:{failure.Dimension}:{failure.Message}"));
    }

    private sealed class ThrowOnEnumeration : IEnumerable<ForwardTrustScenario>
    {
        public IEnumerator<ForwardTrustScenario> GetEnumerator() =>
            throw new InvalidOperationException("The pre-cancelled verification enumerated its input.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

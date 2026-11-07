using System.Net;
using System.Text;
using KeelMatrix.ForwardTrust;
using Xunit;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustGeneratedHeaderBoundsTests
{
    private const int MaxHeaderValueLength = 16 * 1024;
    private const int MaxAggregateHeaderBytes = 64 * 1024;
    private static readonly IPAddress TrustedPeer = IPAddress.Parse("10.0.0.10");
    private static readonly IPAddress UntrustedPeer = IPAddress.Parse("10.0.0.20");
    private static readonly IPAddress AcceptedClient = IPAddress.Parse("198.51.100.10");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnassertedHostDoesNotGenerateOrdinaryHostControls(bool existingHost, bool rejectedPrimary)
    {
        var headers = new Dictionary<string, string> { ["X-Forwarded-For"] = TrustedPeer.ToString(), ["X-Forwarded-Host"] = "localhost" };
        if (existingHost) { headers["Host"] = "localhost"; }
        while (headers.Count < 64) { headers[$"X-Padding-{headers.Count}"] = "a"; }
        var accepted = new ForwardedIdentity("http", TrustedPeer);
        var rejected = new ForwardedIdentity("http", UntrustedPeer);
        var scenario = new ForwardTrustScenario("unasserted-host-witness-bounds", (rejectedPrimary ? UntrustedPeer : TrustedPeer).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((rejectedPrimary ? TrustedPeer : UntrustedPeer).ToString(), rejectedPrimary ? accepted : rejected));
        var calls = 0;
        _ = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) =>
        {
            calls++;
            Assert.InRange(request.Headers.Count, 0, 64);
            Assert.Equal(existingHost, request.Headers.ContainsKey("Host"));
            return ValueTask.FromResult(new ForwardedIdentity("http", request.ImmediatePeerAddress, "localhost"));
        });
        Assert.True(calls > 0);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task BothPossibleOrdinaryHostAlternatesRespectAggregateBounds(int delta, bool rejectedPrimary)
    {
        const string originalHost = "counterfactual.example";
        const string ordinaryHost = "localhost";
        const string largerAlternate = "alternate-counterfactual.example";
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = TrustedPeer.ToString(),
            ["X-Forwarded-Host"] = originalHost,
            ["Host"] = ordinaryHost
        };
        var target = MaxAggregateHeaderBytes - (largerAlternate.Length - ordinaryHost.Length) + delta;
        for (var index = 0; CountHeaderBytes(headers) < target; index++)
        {
            var name = $"X-Filler-{index}";
            headers[name] = new string('a', Math.Min(MaxHeaderValueLength, target - CountHeaderBytes(headers) - name.Length));
        }
        Assert.Equal(target, CountHeaderBytes(headers));
        var accepted = new ForwardedIdentity("http", TrustedPeer, originalHost);
        var rejected = new ForwardedIdentity("http", UntrustedPeer, ordinaryHost);
        var scenario = new ForwardTrustScenario("ordinary-host-aggregate", (rejectedPrimary ? UntrustedPeer : TrustedPeer).ToString(),
            rejectedPrimary ? rejected : accepted,
            rejectedPrimary ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted, headers,
            control: new ForwardTrustControl((rejectedPrimary ? TrustedPeer : UntrustedPeer).ToString(), rejectedPrimary ? accepted : rejected));
        var calls = 0;
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) =>
        {
            calls++;
            Assert.InRange(CountHeaderBytes(request.Headers), 0, MaxAggregateHeaderBytes);
            return ValueTask.FromResult(new ForwardedIdentity("http", request.ImmediatePeerAddress, originalHost));
        });
        Assert.Equal(delta <= 0, calls > 0);
        if (delta > 0) { Assert.Contains(result.Failures, failure => failure.Kind == ForwardTrustFailureKind.MalformedScenario); }
    }

    public static IEnumerable<object[]> PerValueOverflowCases()
    {
        foreach (var header in HeaderCases())
        {
            foreach (var originalLength in new[] { MaxHeaderValueLength, MaxHeaderValueLength - 1 })
            {
                foreach (var hopCount in new[] { 1, 2 })
                {
                    yield return [header.HeaderName, header.CounterfactualHost!, originalLength, hopCount];
                }
            }
        }
    }

    public static IEnumerable<object[]> InBoundsBoundaryCases()
    {
        foreach (var header in BoundaryHeaderCases())
        {
            foreach (var generatedLength in new[] { MaxHeaderValueLength, MaxHeaderValueLength - 1 })
            {
                foreach (var hopCount in new[] { 1, 2 })
                {
                    yield return [header.HeaderName, header.CounterfactualHost!, generatedLength, hopCount];
                }
            }
        }
    }

    public static IEnumerable<object[]> AggregateOverflowCases()
    {
        foreach (var header in HeaderCases())
        {
            foreach (var aggregateLength in new[] { MaxAggregateHeaderBytes, MaxAggregateHeaderBytes - 1 })
            {
                foreach (var filler in new[] { 'a', '\u0080' })
                {
                    yield return [header.HeaderName, header.CounterfactualHost!, aggregateLength, filler];
                }
            }
        }
    }

    public static IEnumerable<object[]> LongFirstTokenCases()
    {
        foreach (var header in BoundaryHeaderCases())
        {
            yield return [header.HeaderName, header.CounterfactualHost!, 1];
            yield return [header.HeaderName, header.CounterfactualHost!, 2];
        }
    }

    [Theory]
    [MemberData(nameof(PerValueOverflowCases))]
    public async Task GeneratedExpansionPastPerValueLimitReturnsStructuredFailureBeforeSending(
        string headerName,
        string? counterfactualHost,
        int originalLength,
        int hopCount)
    {
        var originalValue = CreateHeaderValue(originalLength, firstTokenLength: 1, hopCount, headerName);
        var scenario = CreateScenario(headerName, originalValue, counterfactualHost);
        var senderCalls = 0;

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                senderCalls++;
                return ValueTask.FromResult(scenario.ExpectedIdentity);
            });

        Assert.Equal(0, senderCalls);
        AssertGeneratedHeaderFailure(result);
    }

    [Theory]
    [MemberData(nameof(InBoundsBoundaryCases))]
    public async Task GeneratedRequestsAtAndBelowPerValueLimitReachBothPrimaryAndControlSenders(
        string headerName,
        string? counterfactualHost,
        int generatedLength,
        int hopCount)
    {
        var counterfactualValue = CounterfactualValue(headerName, counterfactualHost);
        var suffixLength = generatedLength - counterfactualValue.Length;
        var originalValue = CreateHeaderValue(1 + suffixLength, firstTokenLength: 1, hopCount, headerName);
        var expectedGeneratedValue = string.Concat(counterfactualValue, originalValue.AsSpan(1));
        var requests = new List<ForwardTrustRequest>();

        _ = await new ForwardTrustVerifier().VerifyAsync(
            [CreateScenario(headerName, originalValue, counterfactualHost)],
            RecordingSender(requests));

        requests = ForwardedRequests(requests, headerName);
        AssertOriginalAndChangedRequests(requests, headerName, originalValue, expectedGeneratedValue);
    }

    [Theory]
    [MemberData(nameof(AggregateOverflowCases))]
    public async Task GeneratedExpansionPastAggregateUtf8LimitReturnsStructuredFailureBeforeSending(
        string headerName,
        string? counterfactualHost,
        int aggregateLength,
        char filler)
    {
        var counterfactualValue = CounterfactualValue(headerName, counterfactualHost);
        const int generatedValueLength = MaxHeaderValueLength - 128;
        var originalValueLength = generatedValueLength - counterfactualValue.Length + 1;
        var originalValue = CreateHeaderValue(originalValueLength, firstTokenLength: 1, hopCount: 1, headerName);
        var headers = CreateHeadersAtAggregateLimit(headerName, originalValue, aggregateLength, filler);
        var scenario = CreateScenario(headerName, headers, counterfactualHost);
        var senderCalls = 0;

        Assert.Equal(aggregateLength, CountHeaderBytes(headers));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (_, _) =>
            {
                senderCalls++;
                return ValueTask.FromResult(scenario.ExpectedIdentity);
            });

        Assert.Equal(0, senderCalls);
        AssertGeneratedHeaderFailure(result);
    }

    [Theory]
    [MemberData(nameof(LongFirstTokenCases))]
    public async Task ShorterGeneratedValuePreservesOneOrMultipleHops(
        string headerName,
        string? counterfactualHost,
        int hopCount)
    {
        var firstTokenLength = headerName == "X-Forwarded-For" ? 39 : 128;
        var originalValue = CreateHeaderValue(
            firstTokenLength + 32,
            firstTokenLength,
            hopCount,
            headerName);
        var counterfactualValue = CounterfactualValue(headerName, counterfactualHost);
        var expectedGeneratedValue = string.Concat(counterfactualValue, originalValue.AsSpan(firstTokenLength));
        var requests = new List<ForwardTrustRequest>();

        _ = await new ForwardTrustVerifier().VerifyAsync(
            [CreateScenario(headerName, originalValue, counterfactualHost)],
            RecordingSender(requests));

        requests = ForwardedRequests(requests, headerName);
        Assert.True(counterfactualValue.Length < firstTokenLength);
        AssertOriginalAndChangedRequests(requests, headerName, originalValue, expectedGeneratedValue);
    }

    [Theory]
    [MemberData(nameof(HeaderCasesAsMemberData))]
    public async Task SingleValueCounterfactualReachesSenderWithoutAddingASuffix(
        string headerName,
        string? counterfactualHost)
    {
        var original = headerName == "X-Forwarded-For" ? "1" : "x";
        var scenario = CreateScenario(headerName, original, counterfactualHost);
        var requests = new List<ForwardTrustRequest>();
        var counterfactualValue = CounterfactualValue(headerName, counterfactualHost);

        _ = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            RecordingSender(requests));

        requests = ForwardedRequests(requests, headerName);
        AssertOriginalAndChangedRequests(requests, headerName, original, counterfactualValue);
    }

    public static IEnumerable<object[]> HeaderCasesAsMemberData()
    {
        foreach (var header in HeaderCases())
        {
            yield return [header.HeaderName, header.CounterfactualHost!];
        }
    }

    private static IEnumerable<HeaderCase> HeaderCases()
    {
        yield return new HeaderCase("X-Forwarded-For", null);
        yield return new HeaderCase("X-Forwarded-Proto", null);
        yield return new HeaderCase("X-Forwarded-Host", null);
        yield return new HeaderCase("X-Forwarded-Host", "alternate-counterfactual.example");
    }

    private static IEnumerable<HeaderCase> BoundaryHeaderCases()
    {
        yield return new HeaderCase("X-Forwarded-For", null);
        yield return new HeaderCase("X-Forwarded-Proto", null);
        yield return new HeaderCase("X-Forwarded-Host", null);
        yield return new HeaderCase("X-Forwarded-Host", "alt.example");
    }

    private static ForwardTrustScenario CreateScenario(
        string headerName,
        string headerValue,
        string? counterfactualHost)
    {
        return CreateScenario(
            headerName,
            new Dictionary<string, string> { [headerName] = headerValue },
            counterfactualHost);
    }

    private static ForwardTrustScenario CreateScenario(
        string headerName,
        IReadOnlyDictionary<string, string> headers,
        string? counterfactualHost)
    {
        var original = headers[headerName].Split(',')[0].Trim();
        var expectedIdentity = headerName switch
        {
            "X-Forwarded-Proto" => new ForwardedIdentity(original, AcceptedClient),
            "X-Forwarded-Host" => new ForwardedIdentity("http", AcceptedClient, original),
            _ => new ForwardedIdentity("http", IPEndPoint.Parse(original).Address)
        };
        var rejectedIdentity = headerName switch
        {
            "X-Forwarded-Proto" => new ForwardedIdentity("http", UntrustedPeer),
            "X-Forwarded-Host" => new ForwardedIdentity("http", UntrustedPeer, "localhost"),
            _ => new ForwardedIdentity("http", UntrustedPeer)
        };
        return new ForwardTrustScenario(
            "generated-header-bound",
            TrustedPeer.ToString(),
            expectedIdentity,
            ForwardTrustHeaderExpectation.Accepted,
            headers,
            control: new ForwardTrustControl(UntrustedPeer.ToString(), rejectedIdentity))
        {
            CounterfactualHost = counterfactualHost
        };
    }

    private static ForwardTrustRequestSender RecordingSender(List<ForwardTrustRequest> requests)
    {
        return (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            return ValueTask.FromResult(request.Scenario.ExpectedIdentity);
        };
    }

    private static List<ForwardTrustRequest> ForwardedRequests(List<ForwardTrustRequest> requests, string headerName)
    {
        Assert.All(requests, request =>
        {
            Assert.InRange(request.Headers.Count, 0, 64);
            Assert.All(request.Headers.Values, value => Assert.InRange(value.Length, 0, MaxHeaderValueLength));
            Assert.InRange(CountHeaderBytes(request.Headers), 0, MaxAggregateHeaderBytes);
        });
        return requests.Where(request => request.Headers.ContainsKey(headerName) && !request.Headers.ContainsKey("Host")).ToList();
    }

    private static void AssertOriginalAndChangedRequests(List<ForwardTrustRequest> requests, string headerName, string original, string changed)
    {
        Assert.All(requests, request => Assert.Contains(request.Headers[headerName], new[] { original, changed }));
        foreach (var peer in new[] { TrustedPeer, UntrustedPeer })
        {
            Assert.True(requests.Count(request => request.ImmediatePeerAddress.Equals(peer) && request.Headers[headerName] == original) >= 2);
            Assert.Equal(2, requests.Count(request => request.ImmediatePeerAddress.Equals(peer) && request.Headers[headerName] == changed));
        }
    }

    private static string CounterfactualValue(string headerName, string? counterfactualHost)
    {
        if (headerName == "X-Forwarded-Proto")
        {
            return "https";
        }

        if (headerName == "X-Forwarded-Host")
        {
            return counterfactualHost ?? "counterfactual.example";
        }

        return "203.0.113.254";
    }

    private static string CreateHeaderValue(int length, int firstTokenLength, int hopCount, string headerName)
    {
        var suffixLength = length - firstTokenLength;
        if (suffixLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var firstToken = headerName == "X-Forwarded-For"
            ? firstTokenLength == 1 ? "1" : "2001:0db8:0000:0000:0000:0000:0000:0001"
            : new string('a', firstTokenLength);
        if (hopCount == 0)
        {
            return firstToken;
        }

        if (suffixLength < (hopCount == 1 ? 1 : 3))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var suffix = hopCount == 1
            ? string.Concat(",", new string('b', suffixLength - 1))
            : string.Concat(",", new string('b', suffixLength - 3), ",c");
        return string.Concat(firstToken, suffix);
    }

    private static Dictionary<string, string> CreateHeadersAtAggregateLimit(
        string headerName,
        string forwardedValue,
        int aggregateLength,
        char filler)
    {
        var headers = new Dictionary<string, string>
        {
            [headerName] = forwardedValue,
            ["X-Fill-A"] = string.Empty,
            ["X-Fill-B"] = string.Empty,
            ["X-Fill-C"] = string.Empty,
            ["X-Fill-D"] = string.Empty
        };
        var remainingBytes = aggregateLength - CountHeaderBytes(headers);
        var bytesPerCharacter = Encoding.UTF8.GetByteCount(filler.ToString());
        foreach (var name in new[] { "X-Fill-A", "X-Fill-B", "X-Fill-C", "X-Fill-D" })
        {
            var prefix = string.Empty;
            if (bytesPerCharacter > 1 && remainingBytes % bytesPerCharacter != 0)
            {
                prefix = "a";
                remainingBytes--;
            }

            var characterCount = Math.Min(MaxHeaderValueLength - prefix.Length, remainingBytes / bytesPerCharacter);
            var value = string.Concat(prefix, new string(filler, characterCount));
            headers[name] = value;
            remainingBytes -= characterCount * bytesPerCharacter;
        }

        if (remainingBytes != 0)
        {
            throw new InvalidOperationException("The aggregate header target could not be represented with bounded values.");
        }

        return headers;
    }

    private static int CountHeaderBytes(IReadOnlyDictionary<string, string> headers)
    {
        return headers.Sum(header =>
            Encoding.UTF8.GetByteCount(header.Key)
            + Encoding.UTF8.GetByteCount(header.Value));
    }

    private static void AssertGeneratedHeaderFailure(ForwardTrustResult result)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ForwardTrustFailureKind.MalformedScenario
            && failure.Message.Contains("generated counterfactual request", StringComparison.Ordinal));
    }

    private sealed record HeaderCase(
        string HeaderName,
        string? CounterfactualHost);
}

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
        var originalValue = CreateHeaderValue(originalLength, firstTokenLength: 1, hopCount);
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
        var originalValue = CreateHeaderValue(1 + suffixLength, firstTokenLength: 1, hopCount);
        var expectedGeneratedValue = string.Concat(counterfactualValue, originalValue.AsSpan(1));
        var requests = new List<ForwardTrustRequest>();

        _ = await new ForwardTrustVerifier().VerifyAsync(
            [CreateScenario(headerName, originalValue, counterfactualHost)],
            RecordingSender(requests));

        Assert.Equal(8, requests.Count);
        Assert.Equal(expectedGeneratedValue, requests[2].Headers[headerName]);
        Assert.Equal(expectedGeneratedValue, requests[3].Headers[headerName]);
        Assert.Equal(originalValue, requests[0].Headers[headerName]);
        Assert.Equal(originalValue, requests[1].Headers[headerName]);
        Assert.Equal(originalValue, requests[4].Headers[headerName]);
        Assert.Equal(originalValue, requests[5].Headers[headerName]);
        Assert.Equal(expectedGeneratedValue, requests[6].Headers[headerName]);
        Assert.Equal(expectedGeneratedValue, requests[7].Headers[headerName]);
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
        var originalValue = CreateHeaderValue(originalValueLength, firstTokenLength: 1, hopCount: 1);
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
        const int firstTokenLength = 128;
        var originalValue = CreateHeaderValue(
            firstTokenLength + 32,
            firstTokenLength,
            hopCount);
        var counterfactualValue = CounterfactualValue(headerName, counterfactualHost);
        var expectedGeneratedValue = string.Concat(counterfactualValue, originalValue.AsSpan(firstTokenLength));
        var requests = new List<ForwardTrustRequest>();

        _ = await new ForwardTrustVerifier().VerifyAsync(
            [CreateScenario(headerName, originalValue, counterfactualHost)],
            RecordingSender(requests));

        Assert.Equal(8, requests.Count);
        Assert.True(counterfactualValue.Length < firstTokenLength);
        Assert.Equal(expectedGeneratedValue, requests[2].Headers[headerName]);
        Assert.Equal(expectedGeneratedValue, requests[3].Headers[headerName]);
        Assert.Equal(originalValue, requests[0].Headers[headerName]);
        Assert.Equal(originalValue, requests[1].Headers[headerName]);
    }

    [Theory]
    [MemberData(nameof(HeaderCasesAsMemberData))]
    public async Task SingleValueCounterfactualReachesSenderWithoutAddingASuffix(
        string headerName,
        string? counterfactualHost)
    {
        var scenario = CreateScenario(headerName, "x", counterfactualHost);
        var requests = new List<ForwardTrustRequest>();
        var counterfactualValue = CounterfactualValue(headerName, counterfactualHost);

        _ = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            RecordingSender(requests));

        Assert.Equal(8, requests.Count);
        Assert.Equal(counterfactualValue, requests[2].Headers[headerName]);
        Assert.Equal(counterfactualValue, requests[3].Headers[headerName]);
        Assert.Equal("x", requests[0].Headers[headerName]);
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
        var expectedIdentity = headerName switch
        {
            "X-Forwarded-Proto" => new ForwardedIdentity("https", AcceptedClient),
            "X-Forwarded-Host" => new ForwardedIdentity("http", AcceptedClient, "public.example"),
            _ => new ForwardedIdentity("http", AcceptedClient)
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

    private static string CounterfactualValue(string headerName, string? counterfactualHost)
    {
        if (headerName == "X-Forwarded-Proto")
        {
            return "http";
        }

        if (headerName == "X-Forwarded-Host")
        {
            return counterfactualHost ?? "counterfactual.example";
        }

        return "203.0.113.254";
    }

    private static string CreateHeaderValue(int length, int firstTokenLength, int hopCount)
    {
        var suffixLength = length - firstTokenLength;
        if (suffixLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var firstToken = new string('a', firstTokenLength);
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

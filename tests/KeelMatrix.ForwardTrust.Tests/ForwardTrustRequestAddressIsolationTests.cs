using System.Net;
using System.Net.Sockets;
using KeelMatrix.ForwardTrust;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustRequestAddressIsolationTests
{
    private const string OriginalScheme = "https";
    private const string CounterfactualScheme = "http";

    public static IEnumerable<object[]> AddressMutationCases()
    {
        yield return new object[]
        {
            ForwardTrustHeaderExpectation.Accepted,
            IPAddress.Parse("10.0.0.10"),
            new IPAddress(IPAddress.Parse("fe80::20").GetAddressBytes(), 0)
        };
        yield return new object[]
        {
            ForwardTrustHeaderExpectation.Accepted,
            new IPAddress(IPAddress.Parse("fe80::10").GetAddressBytes(), 7),
            IPAddress.Parse("10.0.0.20")
        };
        yield return new object[]
        {
            ForwardTrustHeaderExpectation.Rejected,
            IPAddress.Parse("10.0.0.10"),
            new IPAddress(IPAddress.Parse("fe80::20").GetAddressBytes(), 7)
        };
        yield return new object[]
        {
            ForwardTrustHeaderExpectation.Rejected,
            new IPAddress(IPAddress.Parse("fe80::10").GetAddressBytes(), 0),
            IPAddress.Parse("10.0.0.20")
        };
    }

    [Theory]
    [MemberData(nameof(AddressMutationCases))]
    public async Task SenderCannotMutatePrimaryOrControlAddressAcrossOriginalCounterfactualAndReplayCalls(
        ForwardTrustHeaderExpectation expectation,
        IPAddress primaryPeer,
        IPAddress controlPeer)
    {
        var primaryIdentity = new ForwardedIdentity(
            expectation == ForwardTrustHeaderExpectation.Accepted ? OriginalScheme : CounterfactualScheme,
            IPAddress.Parse("198.51.100.10"));
        var controlIdentity = new ForwardedIdentity(
            expectation == ForwardTrustHeaderExpectation.Accepted ? CounterfactualScheme : OriginalScheme,
            IPAddress.Parse("198.51.100.10"));
        var scenario = new ForwardTrustScenario(
            "request-address-isolation",
            primaryPeer.ToString(),
            primaryIdentity,
            expectation,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = OriginalScheme },
            control: new ForwardTrustControl(controlPeer.ToString(), controlIdentity));
        var observations = new List<ProbeObservation>();

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var addressAlias = request.ImmediatePeerAddress;
                var peerAddressText = addressAlias.ToString();
                var side = addressAlias.Equals(primaryPeer)
                    ? "primary"
                    : addressAlias.Equals(controlPeer) ? "control" : "unexpected";
                var forwardedScheme = request.Headers["X-Forwarded-Proto"];

                if (addressAlias.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    addressAlias.ScopeId = addressAlias.ScopeId == 0 ? 1 : addressAlias.ScopeId + 1;
                }

                var rereadAddressText = request.ImmediatePeerAddress.ToString();
                observations.Add(new ProbeObservation(side, peerAddressText, rereadAddressText, forwardedScheme));

                var expectedIdentity = side == "control" ? controlIdentity : primaryIdentity;
                var sideAcceptsForwardedValues = side == "primary"
                    ? expectation == ForwardTrustHeaderExpectation.Accepted
                    : expectation == ForwardTrustHeaderExpectation.Rejected;
                var observedScheme = sideAcceptsForwardedValues
                    ? forwardedScheme
                    : expectedIdentity.Scheme;

                return ValueTask.FromResult(new ForwardedIdentity(observedScheme, expectedIdentity.ClientAddress));
            });

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(static failure => failure.Message)));
        Assert.Equal(8, observations.Count);
        Assert.All(observations, observation => Assert.Equal(observation.PeerAddress, observation.RereadAddress));
        Assert.DoesNotContain(observations, observation => observation.Side == "unexpected");

        AssertAddressCalls("primary", primaryPeer, observations);
        AssertAddressCalls("control", controlPeer, observations);
    }

    private static void AssertAddressCalls(string side, IPAddress expectedAddress, IReadOnlyCollection<ProbeObservation> observations)
    {
        var expectedText = expectedAddress.ToString();
        foreach (var forwardedScheme in new[] { OriginalScheme, CounterfactualScheme })
        {
            Assert.Equal(2, observations.Count(observation =>
                observation.Side == side
                && observation.PeerAddress == expectedText
                && observation.ForwardedScheme == forwardedScheme));
        }
    }

    private sealed record ProbeObservation(
        string Side,
        string PeerAddress,
        string RereadAddress,
        string ForwardedScheme);
}

using System.Net;
using KeelMatrix.ForwardTrust;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardedIdentityTests
{
    [Fact]
    public void ForwardedForObservationDistinguishesAbsenceFromUnavailableWithoutChangingTheOriginal()
    {
        var address = new IPAddress(IPAddress.Parse("fe80::25").GetAddressBytes(), 3);
        var original = new ForwardedIdentity("http", address);
        var absent = original.WithForwardedForObservation(null);
        const string remainder = "\"10.0.0.11:1111\", [2001:db8::12]:2222";
        var partial = original.WithForwardedForObservation(remainder);
        address.ScopeId = 4;
        partial.ClientAddress.ScopeId = 5;
        Assert.False(original.HasForwardedForObservation);
        Assert.Null(original.RemainingForwardedFor);
        Assert.True(absent.HasForwardedForObservation);
        Assert.Null(absent.RemainingForwardedFor);
        Assert.Equal(remainder, partial.RemainingForwardedFor);
        Assert.Equal(3, partial.ClientAddress.ScopeId);
        Assert.Equal(3, absent.ClientAddress.ScopeId);
        Assert.Equal(remainder, (partial with { }).RemainingForwardedFor);
    }

    [Theory]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(16385)]
    public void ForwardedForObservationHasAFiniteBoundWithoutEchoingItsValue(int length)
    {
        var identity = new ForwardedIdentity("http", IPAddress.Loopback);
        var value = new string('x', length);
        if (length <= 16384) { Assert.Equal(value, identity.WithForwardedForObservation(value).RemainingForwardedFor); }
        else
        {
            var error = Assert.Throws<ArgumentException>(() => identity.WithForwardedForObservation(value));
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClientAddressSnapshotsConstructorInputAndReturnsDefensiveCopies()
    {
        var source = new IPAddress(IPAddress.Parse("fe80::25").GetAddressBytes(), 3);
        var identity = new ForwardedIdentity("https", source);

        source.ScopeId = 4;
        var returnedAddress = identity.ClientAddress;
        Assert.Equal(3, returnedAddress.ScopeId);

        returnedAddress.ScopeId = 5;
        Assert.Equal(3, identity.ClientAddress.ScopeId);
    }

    [Fact]
    public async Task PrimaryAndControlExpectationsRemainStableAfterIpv6ScopeAliasesAreMutated()
    {
        var acceptedAddressBytes = IPAddress.Parse("fe80::10").GetAddressBytes();
        var rejectedAddressBytes = IPAddress.Parse("fe80::20").GetAddressBytes();
        var acceptedPeer = new IPAddress(acceptedAddressBytes, 3);
        var rejectedPeer = new IPAddress(rejectedAddressBytes, 4);
        var acceptedPeerForSender = new IPAddress(acceptedAddressBytes, 3);
        var primaryExpectation = new ForwardedIdentity("https", acceptedPeer);
        var controlExpectation = new ForwardedIdentity("http", rejectedPeer);
        var scenario = new ForwardTrustScenario(
            "scoped-address-snapshot",
            acceptedPeer.ToString(),
            primaryExpectation,
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            control: new ForwardTrustControl(rejectedPeer.ToString(), controlExpectation));

        acceptedPeer.ScopeId = 30;
        rejectedPeer.ScopeId = 40;
        var primaryGetterAlias = scenario.ExpectedIdentity.ClientAddress;
        var controlGetterAlias = scenario.Control!.ExpectedIdentity.ClientAddress;
        primaryGetterAlias.ScopeId = 31;
        controlGetterAlias.ScopeId = 41;

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.ImmediatePeerAddress.Equals(acceptedPeerForSender))
                {
                    var scheme = request.Headers["X-Forwarded-Proto"];
                    return ValueTask.FromResult(new ForwardedIdentity(
                        scheme,
                        new IPAddress(acceptedAddressBytes, 3)));
                }

                return ValueTask.FromResult(new ForwardedIdentity(
                    "http",
                    new IPAddress(rejectedAddressBytes, 4)));
            });

        Assert.Equal(3, scenario.ExpectedIdentity.ClientAddress.ScopeId);
        Assert.Equal(4, scenario.Control.ExpectedIdentity.ClientAddress.ScopeId);
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(static failure => failure.Message)));
    }
}

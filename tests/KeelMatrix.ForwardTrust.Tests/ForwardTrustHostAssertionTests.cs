using System.Net;
using KeelMatrix.ForwardTrust;
using Microsoft.AspNetCore.HttpOverrides;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustHostAssertionTests
{
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.0.0.10");
    private static readonly IPAddress UntrustedProxy = IPAddress.Parse("10.0.0.20");
    private static readonly IPAddress ExpectedClient = IPAddress.Parse("198.51.100.10");

    [Theory]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, true, false)]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, true, true)]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, false, false)]
    [InlineData(ForwardTrustHeaderExpectation.Accepted, false, true)]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, true, false)]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, true, true)]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, false, false)]
    [InlineData(ForwardTrustHeaderExpectation.Rejected, false, true)]
    public async Task HostCounterfactualRequiresAnExpectedOriginalHostOnTheAcceptedSide(
        ForwardTrustHeaderExpectation expectation,
        bool controlHostPresent,
        bool useCustomCounterfactual)
    {
        var primaryIsAccepted = expectation == ForwardTrustHeaderExpectation.Accepted;
        var primaryPeer = primaryIsAccepted ? TrustedProxy : UntrustedProxy;
        var controlPeer = primaryIsAccepted ? UntrustedProxy : TrustedProxy;
        var primaryExpectedHost = primaryIsAccepted ? "public.example" : "localhost";
        var controlExpectedHost = controlHostPresent
            ? primaryIsAccepted ? "localhost" : "public.example"
            : null;
        var scenario = new ForwardTrustScenario(
            "host-proof-boundary",
            primaryPeer.ToString(),
            new ForwardedIdentity("http", primaryPeer, primaryExpectedHost),
            expectation,
            new Dictionary<string, string> { ["X-Forwarded-Host"] = "public.example" },
            control: new ForwardTrustControl(
                controlPeer.ToString(),
                new ForwardedIdentity("http", controlPeer, controlExpectedHost)))
        {
            CounterfactualHost = useCustomCounterfactual ? "counterfactual.example" : null
        };
        var senderCalls = 0;

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (request, _) =>
            {
                senderCalls++;
                var peer = request.ImmediatePeerAddress;
                var acceptedControlWithoutHostProof = !primaryIsAccepted && !controlHostPresent;
                var rejectedControlWithoutHostExpectation = primaryIsAccepted && !controlHostPresent;
                var headerHost = request.Headers["X-Forwarded-Host"].Split(',')[0].Trim();
                var host = peer.Equals(TrustedProxy)
                    ? acceptedControlWithoutHostProof && headerHost == "public.example" ? null : headerHost
                    : rejectedControlWithoutHostExpectation ? null : "localhost";
                return ValueTask.FromResult(new ForwardedIdentity("http", peer, host));
            });

        var expectedSuccess = primaryIsAccepted || controlHostPresent;
        Assert.True(expectedSuccess == result.Succeeded,
            string.Join(" | ", result.Failures.Select(static failure => $"{failure.Kind}:{failure.Dimension}:{failure.Message}")));
        Assert.Equal(result.Succeeded, result.Scenarios[0].Succeeded);
        Assert.Equal(result.Succeeded, result.Failures.Count == 0);

        if (expectedSuccess)
        {
            Assert.True(senderCalls > 0);
        }
        else
        {
            Assert.Equal(0, senderCalls);
            Assert.Contains(result.Failures, static failure =>
                failure.Kind == ForwardTrustFailureKind.RequestApplicationNotProven
                && failure.Dimension == ForwardTrustDimension.Host);
        }
    }

    [Theory]
    [InlineData(ForwardTrustHeaderExpectation.Accepted)]
    [InlineData(ForwardTrustHeaderExpectation.Rejected)]
    public async Task OmittedHostExpectationsLeaveHostHeadersUnasserted(
        ForwardTrustHeaderExpectation expectation)
    {
        var primaryIsAccepted = expectation == ForwardTrustHeaderExpectation.Accepted;
        var primaryPeer = primaryIsAccepted ? TrustedProxy : UntrustedProxy;
        var controlPeer = primaryIsAccepted ? UntrustedProxy : TrustedProxy;
        var primaryClient = primaryIsAccepted ? ExpectedClient : primaryPeer;
        var controlClient = primaryIsAccepted ? controlPeer : ExpectedClient;
        var scenario = new ForwardTrustScenario(
            "host-omitted-with-client-address-asserted",
            primaryPeer.ToString(),
            new ForwardedIdentity("http", primaryClient),
            expectation,
            new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = ExpectedClient.ToString(),
                ["X-Forwarded-Host"] = "unasserted.example"
            },
            control: new ForwardTrustControl(
                controlPeer.ToString(),
                new ForwardedIdentity("http", controlClient)));

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            (request, _) =>
            {
                var peer = request.ImmediatePeerAddress;
                var address = peer.Equals(TrustedProxy)
                    ? IPAddress.Parse(request.Headers["X-Forwarded-For"].Split(',')[0].Trim())
                    : peer;
                var host = peer.Equals(TrustedProxy) ? "forwarded.example" : "localhost";
                return ValueTask.FromResult(new ForwardedIdentity("http", address, host));
            });

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(static failure => failure.Message)));
    }

    [Fact]
    public async Task AllowlistedOriginalHostAndDistinctCounterfactualPassForRejectedPrimary()
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownProxies.Add(TrustedProxy);
            options.AllowedHosts.Clear();
            options.AllowedHosts.Add("public.example");
            options.AllowedHosts.Add("counterfactual.example");
        });
        var scenario = new ForwardTrustScenario(
            "allowlisted-original-host",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy, "localhost"),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["X-Forwarded-Host"] = "public.example" },
            control: new ForwardTrustControl(
                TrustedProxy.ToString(),
                new ForwardedIdentity("http", TrustedProxy, "public.example")))
        {
            CounterfactualHost = "counterfactual.example"
        };

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            TestHostHarness.CreateSender(host.Server));

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(static failure => failure.Message)));
    }

    [Fact]
    public async Task ApplicationHostPolicyRejectionFailsTheAcceptedControlExpectation()
    {
        await using var host = await TestHostHarness.StartAsync(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Clear();
            options.KnownProxies.Add(TrustedProxy);
            options.AllowedHosts.Clear();
            options.AllowedHosts.Add("public.example");
            options.AllowedHosts.Add("counterfactual.example");
        });
        var scenario = new ForwardTrustScenario(
            "policy-rejected-original-host",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy, "localhost"),
            ForwardTrustHeaderExpectation.Rejected,
            new Dictionary<string, string> { ["X-Forwarded-Host"] = "blocked.example" },
            control: new ForwardTrustControl(
                TrustedProxy.ToString(),
                new ForwardedIdentity("http", TrustedProxy, "blocked.example")))
        {
            CounterfactualHost = "counterfactual.example"
        };

        var result = await new ForwardTrustVerifier().VerifyAsync(
            [scenario],
            TestHostHarness.CreateSender(host.Server));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, static failure =>
            failure.Kind == ForwardTrustFailureKind.HostMismatch
            && failure.Dimension == ForwardTrustDimension.Host);
        Assert.Contains(result.Failures, static failure =>
            failure.Kind == ForwardTrustFailureKind.TrustedHeaderRejected
            && failure.Dimension == ForwardTrustDimension.Host);
    }
}

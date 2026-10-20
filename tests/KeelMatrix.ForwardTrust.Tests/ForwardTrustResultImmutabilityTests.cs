using System.Net;
using KeelMatrix.ForwardTrust;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustResultImmutabilityTests
{
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.0.0.10");
    private static readonly IPAddress UntrustedProxy = IPAddress.Parse("10.0.0.20");

    [Theory]
    [InlineData("success")]
    [InlineData("validation-failure")]
    [InlineData("sender-failure")]
    public async Task ReturnedCollectionsRejectMutationAndKeepVerdictsConsistent(string resultKind)
    {
        var verifier = new ForwardTrustVerifier();
        var invalidScenario = new ForwardTrustScenario(
            "validation-failure-source",
            UntrustedProxy.ToString(),
            new ForwardedIdentity("http", UntrustedProxy),
            ForwardTrustHeaderExpectation.Rejected);
        var failureSeed = await verifier.VerifyAsync(
            [invalidScenario],
            static (_, _) => ValueTask.FromResult(new ForwardedIdentity("http", UntrustedProxy)));
        var seedFailure = Assert.Single(failureSeed.Failures);

        ForwardTrustResult result;
        if (resultKind == "success")
        {
            var scenario = CreateValidScenario("successful-result");
            result = await verifier.VerifyAsync([scenario], SendExpectedIdentity);
            Assert.True(result.Succeeded);
        }
        else if (resultKind == "validation-failure")
        {
            result = failureSeed;
            Assert.False(result.Succeeded);
        }
        else
        {
            var scenario = CreateValidScenario("sender-failure-result");
            result = await verifier.VerifyAsync(
                [scenario],
                static (_, _) => ValueTask.FromException<ForwardedIdentity>(new InvalidOperationException("synthetic host failure")));
            Assert.False(result.Succeeded);
            Assert.Contains(result.Failures, static failure => failure.Kind == ForwardTrustFailureKind.HostSetupFailure);
        }

        var aggregateFailures = result.Failures.ToArray();
        var scenarioFailures = result.Scenarios[0].Failures.ToArray();
        var failureList = Assert.IsAssignableFrom<IList<ForwardTrustFailure>>(result.Failures);
        var scenarioFailureList = Assert.IsAssignableFrom<IList<ForwardTrustFailure>>(result.Scenarios[0].Failures);
        var scenarios = Assert.IsAssignableFrom<IList<ForwardTrustScenarioResult>>(result.Scenarios);

        Assert.Throws<NotSupportedException>(() => failureList.Add(seedFailure));
        Assert.Throws<NotSupportedException>(() => scenarioFailureList.Add(seedFailure));
        Assert.Throws<NotSupportedException>(() => scenarios.Add(result.Scenarios[0]));
        if (failureList.Count > 0)
        {
            Assert.Throws<NotSupportedException>(() => failureList[0] = seedFailure);
            Assert.Throws<NotSupportedException>(() => scenarioFailureList[0] = seedFailure);
        }

        Assert.Equal(aggregateFailures, result.Failures);
        Assert.Equal(scenarioFailures, result.Scenarios[0].Failures);
        Assert.Equal(result.Failures, result.Scenarios.SelectMany(static scenario => scenario.Failures));
        Assert.Equal(result.Failures.Count == 0, result.Succeeded);
        Assert.All(result.Scenarios, static scenario =>
            Assert.Equal(scenario.Failures.Count == 0, scenario.Succeeded));
    }

    private static ForwardTrustScenario CreateValidScenario(string name)
    {
        return new ForwardTrustScenario(
            name,
            TrustedProxy.ToString(),
            new ForwardedIdentity("https", TrustedProxy),
            ForwardTrustHeaderExpectation.Accepted,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
            control: new ForwardTrustControl(
                UntrustedProxy.ToString(),
                new ForwardedIdentity("http", UntrustedProxy)));
    }

    private static ValueTask<ForwardedIdentity> SendExpectedIdentity(
        ForwardTrustRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = request.ImmediatePeerAddress;
        var scheme = peer.Equals(TrustedProxy)
            ? request.Headers["X-Forwarded-Proto"]
            : "http";
        return ValueTask.FromResult(new ForwardedIdentity(scheme, peer));
    }
}

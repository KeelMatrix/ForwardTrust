using System.Net;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustDeadlineTests
{
    private static readonly bool[] AsyncModes = [false, true];
    private static readonly string[] Proofs = ["scheme", "host", "address-dependency", "address-dependency-observed", "address-consumption"];

    public static IEnumerable<object[]> CallbackBoundaries() =>
        from proof in Proofs
        from asynchronous in AsyncModes
        from reverse in AsyncModes
        select new object[] { asynchronous, proof, reverse };

    [Theory]
    [MemberData(nameof(CallbackBoundaries))]
    public async Task CancellationDuringAnyCallbackStopsBeforeTheNextRequest(bool asynchronous, string proof, bool reverse)
    {
        var scenario = Scenario(proof, reverse);
        var phaseCount = await CountSuccessfulPhases(scenario);
        foreach (var phase in Enumerable.Range(1, phaseCount))
        {
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ForwardTrustVerifier().VerifyAsync([scenario], async (request, _) =>
            {
                calls++;
                if (asynchronous) { await Task.Yield(); }
                if (calls == phase) { cancellation.Cancel(); }
                return Observe(request);
            }, cancellation.Token));
            Assert.Equal(phase, calls);
        }
    }

    [Theory]
    [MemberData(nameof(CallbackBoundaries))]
    public async Task ResultsAfterTheInvocationDeadlineCannotPass(bool asynchronous, string proof, bool reverse)
    {
        var scenario = Scenario(proof, reverse);
        var phaseCount = await CountSuccessfulPhases(scenario);
        foreach (var phase in Enumerable.Range(1, phaseCount))
        {
            var calls = 0;
            var verifier = new ForwardTrustVerifier(new ForwardTrustVerifierOptions { RequestTimeout = TimeSpan.FromMilliseconds(20) });
            var result = await verifier.VerifyAsync([scenario], async (request, _) =>
            {
                calls++;
                if (calls == phase)
                {
                    if (asynchronous) { await Task.Delay(80, CancellationToken.None); }
                    else { Thread.Sleep(80); }
                }
                return Observe(request);
            });
            Assert.False(result.Succeeded);
            Assert.Contains(result.Failures, f => f.Kind == ForwardTrustFailureKind.HostProbeFailure);
        }
    }

    private static async Task<int> CountSuccessfulPhases(ForwardTrustScenario scenario)
    {
        var calls = 0;
        var result = await new ForwardTrustVerifier().VerifyAsync([scenario], (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(Observe(request));
        });
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures.Select(f => f.Message)));
        return calls;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousWorkBeforeReturningATaskCountsTowardItsDeadline(bool faulted)
    {
        var verifier = new ForwardTrustVerifier(new ForwardTrustVerifierOptions { RequestTimeout = TimeSpan.FromMilliseconds(20) });
        var result = await verifier.VerifyAsync([Scenario()], (request, _) =>
        {
            Thread.Sleep(80);
            return faulted
                ? ValueTask.FromException<ForwardedIdentity>(new InvalidOperationException("late failure"))
                : new ValueTask<ForwardedIdentity>(Task.Run(() => Observe(request)));
        });
        Assert.Contains(result.Failures, f => f.Kind == ForwardTrustFailureKind.HostProbeFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationWinsOverLateFaults(bool deadlineAlsoExpired)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var verifier = new ForwardTrustVerifier(new ForwardTrustVerifierOptions { RequestTimeout = TimeSpan.FromMilliseconds(20) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync([Scenario()], (_, _) =>
        {
            calls++;
            cancellation.Cancel();
            if (deadlineAlsoExpired) { Thread.Sleep(80); }
            throw new InvalidOperationException("cancelled callback");
        }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    private static ForwardTrustScenario Scenario(string proof = "scheme", bool reverse = false)
    {
        var accepted = new ForwardedIdentity(proof is "host" or "address-consumption" ? "http" : "https",
            IPAddress.Parse(proof.StartsWith("address-dependency", StringComparison.Ordinal) ? "10.0.0.11" : "10.0.0.10"), proof == "host" ? "localhost" : null);
        var rejected = new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), proof == "host" ? "localhost" : null);
        return new(proof, reverse ? "10.0.0.20" : "10.0.0.10", reverse ? rejected : accepted,
            reverse ? ForwardTrustHeaderExpectation.Rejected : ForwardTrustHeaderExpectation.Accepted,
        proof == "host" ? new Dictionary<string, string> { ["X-Forwarded-Host"] = "localhost" }
            : proof == "address-consumption" ? new Dictionary<string, string> { ["X-Forwarded-For"] = "10.0.0.10", ["X-Forwarded-Host"] = "localhost" }
            : proof.StartsWith("address-dependency", StringComparison.Ordinal) ? new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https, http", ["X-Forwarded-For"] = "10.0.0.11" }
            : new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
        control: new ForwardTrustControl(reverse ? "10.0.0.10" : "10.0.0.20", reverse ? accepted : rejected));
    }

    private static ForwardedIdentity Observe(ForwardTrustRequest request)
    {
        var identity = ObserveIdentity(request);
        return request.Scenario.Name is "address-consumption" or "address-dependency-observed"
            ? identity.WithForwardedForObservation(request.ImmediatePeerAddress.Equals(IPAddress.Parse("10.0.0.10"))
                ? null : request.Headers.GetValueOrDefault("X-Forwarded-For")) : identity;
    }

    private static ForwardedIdentity ObserveIdentity(ForwardTrustRequest request)
    {
        var trusted = request.ImmediatePeerAddress.Equals(IPAddress.Parse("10.0.0.10"));
        if (request.Scenario.Name == "address-consumption")
        {
            return new ForwardedIdentity("http",
                trusted && request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) ? IPAddress.Parse(forwarded) : request.ImmediatePeerAddress,
                trusted && request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost) ? forwardedHost : request.Headers.GetValueOrDefault("Host", "localhost"));
        }
        if (request.Headers.TryGetValue("X-Forwarded-Host", out var host))
        {
            return new ForwardedIdentity("http", request.ImmediatePeerAddress, trusted ? host : request.Headers.GetValueOrDefault("Host", "localhost"));
        }
        if (!request.Headers.TryGetValue("X-Forwarded-Proto", out var proto))
        {
            return new ForwardedIdentity("http", request.ImmediatePeerAddress, request.Headers.GetValueOrDefault("Host", "localhost"));
        }
        if (request.Headers.TryGetValue("X-Forwarded-For", out var address))
        {
            var scheme = address == "10.0.0.11" ? proto.Split(',')[0].Trim() : proto.Split(',')[^1].Trim();
            return new ForwardedIdentity(trusted ? scheme : "http", trusted ? IPAddress.Parse(address) : request.ImmediatePeerAddress);
        }
        return new ForwardedIdentity(trusted ? proto : "http", request.ImmediatePeerAddress);
    }
}

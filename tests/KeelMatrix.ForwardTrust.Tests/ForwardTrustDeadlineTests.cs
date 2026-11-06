using System.Net;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustDeadlineTests
{
    private static readonly bool[] AsyncModes = [false, true];
    private static readonly string[] Proofs = ["scheme", "host", "address-dependency"];

    public static IEnumerable<object[]> CallbackBoundaries() =>
        from proof in Proofs
        from phase in Enumerable.Range(1, proof == "host" ? 14 : proof == "address-dependency" ? 22 : 8)
        from asynchronous in AsyncModes
        select new object[] { phase, asynchronous, proof };

    [Theory]
    [MemberData(nameof(CallbackBoundaries))]
    public async Task CancellationDuringAnyCallbackStopsBeforeTheNextRequest(int phase, bool asynchronous, string proof)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ForwardTrustVerifier().VerifyAsync([Scenario(proof)], async (request, _) =>
        {
            calls++;
            if (asynchronous) { await Task.Yield(); }
            if (calls == phase) { cancellation.Cancel(); }
            return Observe(request);
        }, cancellation.Token));
        Assert.Equal(phase, calls);
    }

    [Theory]
    [MemberData(nameof(CallbackBoundaries))]
    public async Task ResultsAfterTheInvocationDeadlineCannotPass(int phase, bool asynchronous, string proof)
    {
        var calls = 0;
        var verifier = new ForwardTrustVerifier(new ForwardTrustVerifierOptions { RequestTimeout = TimeSpan.FromMilliseconds(20) });
        var result = await verifier.VerifyAsync([Scenario(proof)], async (request, _) =>
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

    private static ForwardTrustScenario Scenario(string proof = "scheme") => new("deadline", "10.0.0.10",
        new ForwardedIdentity(proof == "host" ? "http" : "https", IPAddress.Parse(proof == "address-dependency" ? "10.0.0.11" : "10.0.0.10"), proof == "host" ? "localhost" : null), ForwardTrustHeaderExpectation.Accepted,
        proof == "host" ? new Dictionary<string, string> { ["X-Forwarded-Host"] = "localhost" }
            : proof == "address-dependency" ? new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https, http", ["X-Forwarded-For"] = "10.0.0.11" }
            : new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
        control: new ForwardTrustControl("10.0.0.20", new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), proof == "host" ? "localhost" : null)));

    private static ForwardedIdentity Observe(ForwardTrustRequest request)
    {
        var trusted = request.ImmediatePeerAddress.Equals(IPAddress.Parse("10.0.0.10"));
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

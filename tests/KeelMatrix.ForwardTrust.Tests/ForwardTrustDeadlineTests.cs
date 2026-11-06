using System.Net;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class ForwardTrustDeadlineTests
{
    private static readonly bool[] AsyncModes = [false, true];

    public static IEnumerable<object[]> CallbackBoundaries() =>
        from hostBaseline in AsyncModes
        from phase in Enumerable.Range(1, hostBaseline ? 12 : 8)
        from asynchronous in AsyncModes
        select new object[] { phase, asynchronous, hostBaseline };

    [Theory]
    [MemberData(nameof(CallbackBoundaries))]
    public async Task CancellationDuringAnyCallbackStopsBeforeTheNextRequest(int phase, bool asynchronous, bool hostBaseline)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ForwardTrustVerifier().VerifyAsync([Scenario(hostBaseline)], async (request, _) =>
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
    public async Task ResultsAfterTheInvocationDeadlineCannotPass(int phase, bool asynchronous, bool hostBaseline)
    {
        var calls = 0;
        var verifier = new ForwardTrustVerifier(new ForwardTrustVerifierOptions { RequestTimeout = TimeSpan.FromMilliseconds(20) });
        var result = await verifier.VerifyAsync([Scenario(hostBaseline)], async (request, _) =>
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

    private static ForwardTrustScenario Scenario(bool hostBaseline = false) => new("deadline", "10.0.0.10",
        new ForwardedIdentity(hostBaseline ? "http" : "https", IPAddress.Parse("10.0.0.10"), hostBaseline ? "localhost" : null), ForwardTrustHeaderExpectation.Accepted,
        hostBaseline ? new Dictionary<string, string> { ["X-Forwarded-Host"] = "localhost" } : new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" },
        control: new ForwardTrustControl("10.0.0.20", new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), hostBaseline ? "localhost" : null)));

    private static ForwardedIdentity Observe(ForwardTrustRequest request)
    {
        var trusted = request.ImmediatePeerAddress.Equals(IPAddress.Parse("10.0.0.10"));
        if (request.Headers.TryGetValue("X-Forwarded-Host", out var host))
        {
            return new ForwardedIdentity("http", request.ImmediatePeerAddress, trusted ? host : request.Headers.GetValueOrDefault("Host", "localhost"));
        }
        return new ForwardedIdentity(trusted ? request.Headers["X-Forwarded-Proto"] : "http", request.ImmediatePeerAddress);
    }
}

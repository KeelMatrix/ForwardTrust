using System.Net;
using KeelMatrix.ForwardTrust;

const string trustedPeer = "10.0.0.10";
var verifier = new ForwardTrustVerifier();

var trusted = new ForwardTrustScenario(
    "trusted-proxy",
    trustedPeer,
    new ForwardedIdentity("https", IPAddress.Parse("198.51.100.10"), "public.example"),
    ForwardTrustHeaderExpectation.Accepted,
    new Dictionary<string, string>
    {
        ["X-Forwarded-For"] = "198.51.100.10",
        ["X-Forwarded-Proto"] = "https",
        ["X-Forwarded-Host"] = "public.example"
    });

var untrusted = new ForwardTrustScenario(
    "untrusted-proxy",
    "10.0.0.20",
    new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20")),
    ForwardTrustHeaderExpectation.Rejected,
    trusted.Headers);

var trustedResult = await verifier.VerifyAsync([trusted], SendAsync);
var untrustedResult = await verifier.VerifyAsync([untrusted], SendAsync);

var misconfigured = new ForwardTrustScenario(
    "unsafe-boundary",
    "10.0.0.20",
    new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20")),
    ForwardTrustHeaderExpectation.Rejected,
    trusted.Headers);
var misconfiguredResult = await verifier.VerifyAsync(
    [misconfigured],
    SendMisconfiguredAsync);

Console.WriteLine($"trusted: {(trustedResult.Succeeded ? "PASS" : "FAIL")}");
Console.WriteLine($"untrusted: {(untrustedResult.Succeeded ? "PASS" : "FAIL")}");
var trustFailure = misconfiguredResult.Failures.FirstOrDefault(failure => failure.Kind == ForwardTrustFailureKind.UntrustedHeaderAccepted);
Console.WriteLine($"misconfigured boundary: {(misconfiguredResult.Succeeded ? "UNEXPECTED PASS" : trustFailure?.Kind.ToString() ?? misconfiguredResult.Failures[0].Kind.ToString())}");

return trustedResult.Succeeded && untrustedResult.Succeeded && !misconfiguredResult.Succeeded ? 0 : 1;

static ValueTask<ForwardedIdentity> SendAsync(ForwardTrustRequest request, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var trusted = request.ImmediatePeerAddress.ToString() == "10.0.0.10";
    if (!trusted)
    {
        return ValueTask.FromResult(new ForwardedIdentity("http", request.ImmediatePeerAddress));
    }

    return ValueTask.FromResult(new ForwardedIdentity(
        request.Headers["X-Forwarded-Proto"],
        IPAddress.Parse(request.Headers["X-Forwarded-For"]),
        request.Headers["X-Forwarded-Host"]));
}

static ValueTask<ForwardedIdentity> SendMisconfiguredAsync(ForwardTrustRequest request, CancellationToken cancellationToken)
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

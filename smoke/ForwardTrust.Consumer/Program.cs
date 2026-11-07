using System.Net;
using KeelMatrix.ForwardTrust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

const string trustedPeer = "10.0.0.10";
var verifier = new ForwardTrustVerifier();

await using var trustedHost = await StartHostAsync(options =>
{
    ConfigureForwardedHeaders(options);
    options.KnownProxies.Add(IPAddress.Parse(trustedPeer));
});

await using var unsafeHost = await StartHostAsync(options =>
{
    ConfigureForwardedHeaders(options);
#pragma warning disable ASPDEPR005
    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Any, 0));
#pragma warning restore ASPDEPR005
});

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
    },
    control: new ForwardTrustControl(
        "10.0.0.20",
        new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"), "localhost")));

var untrusted = new ForwardTrustScenario(
    "untrusted-proxy",
    "10.0.0.20",
    new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20")),
    ForwardTrustHeaderExpectation.Rejected,
    trusted.Headers,
    control: new ForwardTrustControl(
        trustedPeer,
        new ForwardedIdentity("https", IPAddress.Parse("198.51.100.10"), "public.example")));

var trustedResult = await verifier.VerifyAsync([trusted], SendAsync(trustedHost.Server));
var untrustedResult = await verifier.VerifyAsync([untrusted], SendAsync(trustedHost.Server));

var coincident = new ForwardTrustScenario(
    "coincident-address", trustedPeer, new ForwardedIdentity("http", IPAddress.Parse(trustedPeer)),
    ForwardTrustHeaderExpectation.Accepted,
    new Dictionary<string, string> { ["X-Forwarded-For"] = trustedPeer, ["X-Forwarded-Proto"] = "http" },
    control: new ForwardTrustControl("10.0.0.20", new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20"))));
var coincidentResult = await verifier.VerifyAsync([coincident], SendAsync(trustedHost.Server));
var observedSender = SendAsync(trustedHost.Server);
var legacyResult = await verifier.VerifyAsync([coincident], async (request, cancellation) =>
{
    var identity = await observedSender(request, cancellation);
    return new ForwardedIdentity(identity.Scheme, identity.ClientAddress, identity.Host);
});
var legacyUnproven = legacyResult.Failures.Any(failure => failure.Kind == ForwardTrustFailureKind.ForwardedValueNotProven
    && failure.Dimension == ForwardTrustDimension.ClientAddress);

var misconfigured = new ForwardTrustScenario(
    "unsafe-boundary",
    "10.0.0.20",
    new ForwardedIdentity("http", IPAddress.Parse("10.0.0.20")),
    ForwardTrustHeaderExpectation.Rejected,
    trusted.Headers,
    control: new ForwardTrustControl(
        trustedPeer,
        new ForwardedIdentity("https", IPAddress.Parse("198.51.100.10"), "public.example")));
var misconfiguredResult = await verifier.VerifyAsync(
    [misconfigured],
    SendAsync(unsafeHost.Server));

Console.WriteLine($"trusted: {(trustedResult.Succeeded ? "PASS" : "FAIL")}");
Console.WriteLine($"untrusted: {(untrustedResult.Succeeded ? "PASS" : "FAIL")}");
Console.WriteLine($"coincident address with consumption: {(coincidentResult.Succeeded ? "PASS" : "FAIL")}");
Console.WriteLine($"identity-only coincident address: {(legacyUnproven ? "unproven" : "UNEXPECTED RESULT")}");
Console.WriteLine("sender: ASP.NET Core TestServer + ForwardedHeadersMiddleware");
var trustFailure = misconfiguredResult.Failures.FirstOrDefault(failure => failure.Kind == ForwardTrustFailureKind.UntrustedHeaderAccepted);
Console.WriteLine($"misconfigured boundary: {(misconfiguredResult.Succeeded ? "UNEXPECTED PASS" : trustFailure?.Kind.ToString() ?? misconfiguredResult.Failures[0].Kind.ToString())}");

return trustedResult.Succeeded && untrustedResult.Succeeded && coincidentResult.Succeeded && legacyUnproven && !misconfiguredResult.Succeeded ? 0 : 1;

static ForwardTrustRequestSender SendAsync(TestServer server) => async (request, cancellationToken) =>
{
    var context = await server.SendAsync(httpContext =>
    {
        httpContext.Request.Path = request.Path;
        httpContext.Connection.RemoteIpAddress = request.ImmediatePeerAddress;
        foreach (var header in request.Headers)
        {
            httpContext.Request.Headers[header.Key] = header.Value;
        }
    }, cancellationToken);

    return new ForwardedIdentity(
        context.Request.Scheme,
        context.Connection.RemoteIpAddress ?? IPAddress.None,
        context.Request.Host.ToString()).WithForwardedForObservation(
            context.Request.Headers.TryGetValue("X-Forwarded-For", out var remaining) ? remaining.ToString() : null);
};

static async Task<TestHostFixture> StartHostAsync(Action<ForwardedHeadersOptions> configureOptions)
{
    var host = new HostBuilder()
        .ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .Configure(app =>
            {
                var options = new ForwardedHeadersOptions();
                configureOptions(options);
                app.UseForwardedHeaders(options);
                app.Run(static context => context.Response.WriteAsync("probe", context.RequestAborted));
            }))
        .Build();

    await host.StartAsync();
    return new TestHostFixture(host);
}

static void ConfigureForwardedHeaders(ForwardedHeadersOptions options)
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;
}

sealed class TestHostFixture(IHost host) : IAsyncDisposable
{
    public TestServer Server => host.GetTestServer();

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }
}

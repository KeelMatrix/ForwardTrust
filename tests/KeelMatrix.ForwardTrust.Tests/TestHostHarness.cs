using System.Net;
using KeelMatrix.ForwardTrust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KeelMatrix.ForwardTrust.Tests;

internal sealed class TestHostHarness : IAsyncDisposable
{
    private readonly IHost host;

    private TestHostHarness(IHost host)
    {
        this.host = host;
    }

    public TestServer Server => host.GetTestServer();

    public static async Task<TestHostHarness> StartAsync(
        Action<ForwardedHeadersOptions> configureOptions,
        CancellationToken cancellationToken = default)
    {
        return await StartAsync(configureOptions, null, null, cancellationToken);
    }

    public static async Task<TestHostHarness> StartAsync(
        Action<ForwardedHeadersOptions> configureOptions,
        Action<IServiceCollection>? configureServices,
        Action<IApplicationBuilder>? configurePipeline,
        CancellationToken cancellationToken = default)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services => configureServices?.Invoke(services))
                .Configure(app =>
                {
                    var options = new ForwardedHeadersOptions();
                    configureOptions(options);
                    app.UseForwardedHeaders(options);
                    configurePipeline?.Invoke(app);
                    app.Run(static context => context.Response.WriteAsync("probe", context.RequestAborted));
                }))
            .Build();

        await host.StartAsync(cancellationToken);
        return new TestHostHarness(host);
    }

    public static ForwardTrustRequestSender CreateSender(TestServer server, Action<HttpContext>? afterSend = null)
    {
        return async (request, cancellationToken) =>
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
            afterSend?.Invoke(context);

            return new ForwardedIdentity(
                context.Request.Scheme,
                context.Connection.RemoteIpAddress ?? IPAddress.None,
                context.Request.Host.ToString());
        };
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }
}

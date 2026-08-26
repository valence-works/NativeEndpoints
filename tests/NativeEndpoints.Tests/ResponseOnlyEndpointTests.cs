using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NativeEndpoints.Generated;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The no-request shape end to end: nothing binds, and the handler's return value is written as the
/// JSON response body. Both mapping paths are exercised, because the reflective mapper and the
/// generated registration dispatch response-only endpoints through different code.
/// </summary>
public class ResponseOnlyEndpointTests : IAsyncDisposable
{
    private readonly IHost _reflective;
    private readonly IHost _generated;

    public ResponseOnlyEndpointTests()
    {
        _reflective = Host(group => group.MapEndpointsFrom(typeof(ResponseOnlyEndpointTests).Assembly));
        _generated = Host(group => group.Map());
    }

    [Fact]
    public async Task Both_mapping_paths_serve_the_handler_response_as_json()
    {
        foreach (var (label, host) in Hosts())
        {
            using var client = host.GetTestClient();
            var response = await client.GetAsync("/status");

            Assert.True(200 == (int)response.StatusCode,
                $"{label} mapper returned {(int)response.StatusCode} for '/status', expected 200.");
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.Equal("""{"state":"healthy","uptimeSeconds":42}""", await response.Content.ReadAsStringAsync());
        }
    }

    private (string Label, IHost Host)[] Hosts() => [("reflective", _reflective), ("generated", _generated)];

    private static IHost Host(Action<EndpointGroup> map) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => map(endpoints.MapEndpointGroup("Status")));
                }))
            .Start();

    public async ValueTask DisposeAsync()
    {
        await _reflective.StopAsync();
        _reflective.Dispose();
        await _generated.StopAsync();
        _generated.Dispose();
        GC.SuppressFinalize(this);
    }
}

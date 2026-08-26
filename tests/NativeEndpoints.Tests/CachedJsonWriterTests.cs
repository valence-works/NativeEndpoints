using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

public sealed record WriterEcho(string Value);

/// <summary>
/// Success responses are written through a per-endpoint writer that resolves serializer metadata
/// once. These pin what that writer must not change: the group's exact configured Content-Type -
/// charset suffix included, it is a published wire contract - the caller-chosen status code, and
/// identical bytes on every request after the first, which is the one that populates the cache.
/// </summary>
public class CachedJsonWriterTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public CachedJsonWriterTests()
    {
        _host = new HostBuilder()
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
                    app.UseEndpoints(endpoints =>
                    {
                        var group = endpoints.MapEndpointGroup("Writer", jsonContentType: "application/vnd.writer+json; charset=utf-8");
                        group.MapHandler(
                            "GET", "echo", "Echo",
                            (_, _) => Task.FromResult(new WriterEcho("cached")),
                            successStatus: StatusCodes.Status201Created);
                    });
                }))
            .Start();

        _client = _host.GetTestClient();
    }

    [Fact]
    public async Task Configured_content_type_and_status_survive_the_cached_writer_on_every_request()
    {
        var first = await _client.GetAsync("/echo");
        var second = await _client.GetAsync("/echo");

        foreach (var response in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("application/vnd.writer+json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.Equal("""{"value":"cached"}""", await response.Content.ReadAsStringAsync());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// A host composing several modules keeps each module's own error shape: the problem writer keyed by
/// the group name is preferred, and the unkeyed registration remains the single-module fallback.
/// </summary>
public class KeyedProblemWriterTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    private sealed class TaggedWriter(string tag) : IEndpointProblemWriter
    {
        public Task WriteAsync(HttpContext context, EndpointProblem problem)
        {
            context.Response.StatusCode = problem.StatusCode;
            return context.Response.WriteAsync(tag);
        }
    }

    public KeyedProblemWriterTests()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();

                    // Registered before AddNativeEndpoints so its TryAdd keeps this as the unkeyed writer.
                    services.AddSingleton<IEndpointProblemWriter>(new TaggedWriter("unkeyed"));
                    services.AddKeyedSingleton<IEndpointProblemWriter>("Alpha", new TaggedWriter("keyed"));
                    services.AddNativeEndpoints();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapEndpointGroup("Alpha")
                            .MapHandler<string>("GET", "alpha", "Fail",
                                (_, _) => throw new InvalidOperationException("alpha failed"));
                        endpoints.MapEndpointGroup("Beta")
                            .MapHandler<string>("GET", "beta", "Fail",
                                (_, _) => throw new InvalidOperationException("beta failed"));
                    });
                }))
            .Start();

        _client = _host.GetTestClient();
    }

    [Fact]
    public async Task The_writer_keyed_by_the_group_name_is_preferred()
    {
        var response = await _client.GetAsync("/alpha");

        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("keyed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_group_without_a_keyed_writer_falls_back_to_the_unkeyed_one()
    {
        var response = await _client.GetAsync("/beta");

        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("unkeyed", await response.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}

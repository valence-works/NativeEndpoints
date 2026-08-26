using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>A strict endpoint whose 400 exercises the problem writer.</summary>
[Get("solo-strict")]
public sealed class StrictPageEndpoint : ApiEndpoint<StrictPage, string>
{
    public override void Configure(ApiEndpointOptions options) => options.StrictTypedParsing = true;

    public override Task<string> HandleAsync(StrictPage request, CancellationToken cancellationToken) =>
        Task.FromResult($"page:{request.Page}");
}

/// <summary>The request contract for <see cref="StrictPageEndpoint"/>.</summary>
public sealed record StrictPage(int Page);

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
    public async Task A_keyed_only_registration_maps_and_serves_failures_through_the_keyed_writer()
    {
        // No AddNativeEndpoints, so no unkeyed writer: only the writer keyed by the group's own
        // name. This configuration worked before the map-time fail-fast existed — the per-request
        // resolution prefers the keyed writer — so mapping must accept it rather than throw.
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddKeyedSingleton<IEndpointProblemWriter>("Solo", new TaggedWriter("solo"));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapEndpointGroup("Solo").MapEndpoint<StrictPageEndpoint>());
                }))
            .Start();

        using var client = host.GetTestClient();
        var response = await client.GetAsync("/solo-strict?page=notanumber");

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal("solo", await response.Content.ReadAsStringAsync());

        await host.StopAsync();
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

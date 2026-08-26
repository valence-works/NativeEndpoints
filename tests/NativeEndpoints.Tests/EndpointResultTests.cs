using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

public sealed record ResultProbe(bool Created);

public sealed record ResultProbeView(string Message);

/// <summary>
/// <see cref="EndpointResult"/> carries the status alongside the body: the factories directly, and
/// the result-unwrapping dispatch shape through <see cref="EndpointGroup.MapOperation{TMessage}"/>.
/// </summary>
/// <remarks>
/// The endpoint-class result path (<c>ApiEndpointWithResult</c> via <c>MapResultBody</c>) needs an
/// endpoint class to reach, so what is exercised over HTTP here is the same unwrap-and-write dispatch
/// built on the public surface: <c>MapOperation</c> plus <c>WriteJsonAsync</c> with the result's own
/// status code.
/// </remarks>
public class EndpointResultTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public EndpointResultTests()
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
                        var group = endpoints.MapEndpointGroup("Results");
                        group.MapOperation<ResultProbe>(
                            new EndpointOperationDescriptor
                            {
                                Method = "GET",
                                Pattern = "result",
                                Operation = "Get",
                                BodyMode = EndpointBodyMode.None,
                                ResponseType = typeof(ResultProbeView),
                                DocumentedStatus = StatusCodes.Status201Created
                            },
                            async (context, request, _) =>
                            {
                                var result = request.Created
                                    ? EndpointResult.Status(StatusCodes.Status201Created, new ResultProbeView("created"))
                                    : EndpointResult.Ok(new ResultProbeView("found"));
                                await group.WriteJsonAsync(context, result.Response, result.StatusCode);
                            });
                    });
                }))
            .Start();

        _client = _host.GetTestClient();
    }

    [Fact]
    public void Ok_pairs_the_response_with_200()
    {
        var result = EndpointResult.Ok("payload");

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("payload", result.Response);
    }

    [Fact]
    public void Status_pairs_the_response_with_the_given_code()
    {
        var result = EndpointResult.Status(StatusCodes.Status202Accepted, new ResultProbeView("queued"));

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.Equal(new ResultProbeView("queued"), result.Response);
    }

    [Fact]
    public async Task A_result_written_through_the_group_uses_its_own_status()
    {
        var response = await _client.GetAsync("/result?created=true");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(new ResultProbeView("created"), await response.Content.ReadFromJsonAsync<ResultProbeView>());
    }

    [Fact]
    public async Task An_ok_result_written_through_the_group_stays_200()
    {
        var response = await _client.GetAsync("/result");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ResultProbeView("found"), await response.Content.ReadFromJsonAsync<ResultProbeView>());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}

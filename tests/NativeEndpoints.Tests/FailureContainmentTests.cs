using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// Whether an unhandled exception is answered by the group or propagates to the host.
/// </summary>
/// <remarks>
/// Containment is the default and stays the default. The opt-out exists for an owner whose published
/// contract makes the host's exception pipeline responsible for unexpected failures — one already
/// running its own exception middleware, or serving a UI whose error page is not a problem document.
/// That is a runtime routing decision, so unlike the documentation settings it cannot be expressed
/// through a metadata convention.
/// </remarks>
public class FailureContainmentTests
{
    [Fact]
    public async Task A_contained_failure_is_answered_by_the_groups_problem_writer()
    {
        using var host = await HostAsync(containFailures: true);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/boom");

        Assert.Equal(StatusCodes.Status500InternalServerError, (int)response.StatusCode);
        // The sanitized 500: the owner's failure contract, not the exception's detail.
        Assert.DoesNotContain("detonated", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_uncontained_failure_reaches_the_host_pipeline_unswallowed()
    {
        using var host = await HostAsync(containFailures: false);
        using var client = host.GetTestClient();

        // TestServer rethrows what reaches the host pipeline, which is the point: the group did not
        // convert the fault into a response of its own.
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/boom"));

        Assert.Contains("detonated", exception.ToString());
    }

    /// <summary>
    /// Opting out routes failures; it does not opt the operation out of being described. A silent
    /// loss of metadata is exactly the class of bug this release is fixing.
    /// </summary>
    [Fact]
    public async Task Opting_out_of_containment_does_not_change_the_documented_metadata()
    {
        using var contained = await HostAsync(containFailures: true);
        using var uncontained = await HostAsync(containFailures: false);

        Assert.Equal(EndpointNameOf(contained), EndpointNameOf(uncontained));
        Assert.Equal("Containment_Boom", EndpointNameOf(uncontained));
    }

    private static string? EndpointNameOf(IHost host) =>
        host.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.Single()
            .Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;

    private static Task<IHost> HostAsync(bool containFailures) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddNativeEndpoints();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints
                        .MapEndpointGroup("Containment")
                        .MapRaw(
                            new ApiEndpointOptions
                            {
                                Method = "GET",
                                Route = "boom",
                                Operation = "Boom",
                                ContainFailures = containFailures
                            },
                            _ => throw new InvalidOperationException("detonated")));
                }))
            .StartAsync();
}

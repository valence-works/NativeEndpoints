using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>A request contract with no members, for operations that bind nothing.</summary>
public sealed record AuthProbeEmpty;

/// <summary>
/// 401 and 403 are documented only where the completed metadata actually carries authorization, and
/// the explicit force flag overrides that inference in both directions.
/// </summary>
public class AuthResponseDocumentationTests
{
    [Fact]
    public void An_authorized_endpoint_documents_401_and_403()
    {
        var endpoint = Build(group => group
            .MapHandler<string>("GET", "secured", "Secured", (_, _) => Task.FromResult("ok"))
            .RequireAuthorization());

        Assert.Equal([401, 403], AuthStatuses(endpoint));
    }

    [Fact]
    public void An_endpoint_without_authorization_documents_neither()
    {
        var endpoint = Build(group => group
            .MapHandler<string>("GET", "open", "Open", (_, _) => Task.FromResult("ok")));

        Assert.Empty(AuthStatuses(endpoint));
    }

    [Fact]
    public void AllowAnonymous_suppresses_the_documented_pair()
    {
        var endpoint = Build(group => group
            .MapHandler<string>("GET", "anonymous", "Anonymous", (_, _) => Task.FromResult("ok"))
            .RequireAuthorization()
            .AllowAnonymous());

        Assert.Empty(AuthStatuses(endpoint));
    }

    [Fact]
    public void Forcing_documentation_on_documents_the_pair_without_authorization()
    {
        var endpoint = Build(group => group.MapOperation<AuthProbeEmpty>(
            "GET", "forced-on", "ForcedOn", EndpointBodyMode.None, null, typeof(string),
            StatusCodes.Status200OK, null, (_, _, _) => Task.CompletedTask,
            documentAuthResponses: true));

        Assert.Equal([401, 403], AuthStatuses(endpoint));
    }

    [Fact]
    public void Forcing_documentation_off_suppresses_the_pair_despite_authorization()
    {
        var endpoint = Build(group => group.MapOperation<AuthProbeEmpty>(
                "GET", "forced-off", "ForcedOff", EndpointBodyMode.None, null, typeof(string),
                StatusCodes.Status200OK, null, (_, _, _) => Task.CompletedTask,
                documentAuthResponses: false)
            .RequireAuthorization());

        Assert.Empty(AuthStatuses(endpoint));
    }

    /// <summary>Maps one operation and returns the built endpoint from the host's data sources.</summary>
    private static Endpoint Build(Func<EndpointGroup, IEndpointConventionBuilder> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNativeEndpoints();
        builder.Services.AddAuthorization();

        var app = builder.Build();
        var routes = (IEndpointRouteBuilder)app;
        map(routes.MapEndpointGroup("Auth"));

        return routes.DataSources.SelectMany(source => source.Endpoints).Single();
    }

    private static int[] AuthStatuses(Endpoint endpoint) =>
    [
        .. endpoint.Metadata.OfType<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .Where(status => status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
            .Order()
    ];
}

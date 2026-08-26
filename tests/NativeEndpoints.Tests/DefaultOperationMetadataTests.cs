using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The metadata every mapped operation carries by default: the unique name, the group tag and group
/// marker, the documented success response, and the stable API Explorer description method.
/// </summary>
public class DefaultOperationMetadataTests
{
    private readonly Endpoint _endpoint;

    public DefaultOperationMetadataTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNativeEndpoints();

        var app = builder.Build();
        var routes = (IEndpointRouteBuilder)app;
        routes.MapEndpointGroup("Meta")
            .MapHandler<string>("GET", "things", "ThingsList", (_, _) => Task.FromResult("ok"));

        _endpoint = routes.DataSources.SelectMany(source => source.Endpoints).Single();
    }

    [Fact]
    public void The_endpoint_name_is_group_underscore_operation() =>
        Assert.Equal("Meta_ThingsList", _endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);

    [Fact]
    public void The_group_name_is_the_tag() =>
        Assert.Contains("Meta", _endpoint.Metadata.GetMetadata<ITagsMetadata>()!.Tags);

    [Fact]
    public void The_group_membership_is_recorded() =>
        Assert.Equal("Meta", _endpoint.Metadata.GetMetadata<EndpointGroupMetadata>()?.Name);

    [Fact]
    public void The_success_status_is_documented_with_the_response_type()
    {
        var produces = Assert.Single(_endpoint.Metadata.OfType<IProducesResponseTypeMetadata>());

        Assert.Equal(StatusCodes.Status200OK, produces.StatusCode);
        Assert.Equal(typeof(string), produces.Type);
        Assert.Contains("application/json", produces.ContentTypes);
    }

    [Fact]
    public void The_api_explorer_description_method_is_RequestDelegate_Invoke_and_is_the_last_MethodInfo()
    {
        var invoke = typeof(RequestDelegate).GetMethod(nameof(RequestDelegate.Invoke));

        // EndpointMetadataCollection.GetMetadata<T>() selects the last match, so both must agree.
        Assert.Equal(invoke, _endpoint.Metadata.OfType<MethodInfo>().Last());
        Assert.Equal(invoke, _endpoint.Metadata.GetMetadata<MethodInfo>());
    }
}

using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// Publishing a bare RequestDelegate leaves API Explorer nothing to infer parameters from, so the
/// library states them itself. These pin what it states.
/// </summary>
public class ParameterDescriptionTests
{
    private sealed record Query(
        string? Search,
        string[] Tag,
        int Take,
        [property: FromHeader("X-Tenant")] string? Tenant,
        [property: FromClaim("sub")] string? Subject);

    private sealed record Routed(Guid NoteId, string Title);

    [Fact]
    public void Route_parameters_use_the_templates_casing()
    {
        // A path parameter whose name does not match the template is invalid OpenAPI.
        var described = EndpointParameterDescriber.Describe(typeof(Routed), "notes/{noteId}", readsBody: true);

        var route = Assert.Single(described, p => p.Source is EndpointBindingSource.Route);
        Assert.Equal("noteId", route.Name);
        Assert.True(route.Required);
    }

    [Fact]
    public void Body_members_are_not_repeated_as_parameters()
    {
        var described = EndpointParameterDescriber.Describe(typeof(Routed), "notes/{noteId}", readsBody: true);

        Assert.DoesNotContain(described, p => p.Name == "Title");
    }

    [Fact]
    public void Everything_not_in_the_route_or_body_is_a_query_parameter()
    {
        var described = EndpointParameterDescriber.Describe(typeof(Query), "notes", readsBody: false);

        Assert.Equal(EndpointBindingSource.Query, Assert.Single(described, p => p.Name == "Search").Source);
        Assert.Equal(EndpointBindingSource.Query, Assert.Single(described, p => p.Name == "Tag").Source);
    }

    [Fact]
    public void Optionality_follows_nullability()
    {
        var described = EndpointParameterDescriber.Describe(typeof(Query), "notes", readsBody: false);

        Assert.False(Assert.Single(described, p => p.Name == "Search").Required);
        Assert.True(Assert.Single(described, p => p.Name == "Take").Required);
    }

    [Fact]
    public void Declared_sources_are_described_under_their_declared_key()
    {
        var described = EndpointParameterDescriber.Describe(typeof(Query), "notes", readsBody: false);

        Assert.Equal(EndpointBindingSource.Header, Assert.Single(described, p => p.Name == "X-Tenant").Source);
        Assert.Equal(EndpointBindingSource.Claim, Assert.Single(described, p => p.Name == "sub").Source);
    }

    [Fact]
    public void An_operation_with_no_contract_describes_nothing() =>
        Assert.Empty(EndpointParameterDescriber.Describe(contract: null, "notes", readsBody: false));
}

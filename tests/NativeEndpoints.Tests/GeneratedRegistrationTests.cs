using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NativeEndpoints.Generated;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The generator's output, compiled and executed rather than compared against a string.
/// </summary>
/// <remarks>
/// A snapshot test would pin the formatting; this pins the behavior. If the generated registration
/// does not compile, this project does not build, so the test existing at all is part of the check.
/// </remarks>
public class GeneratedRegistrationTests
{
    [Fact]
    public void Generated_registration_maps_the_same_endpoints_as_the_reflective_scan()
    {
        var generated = Map(group => group.Map(routePrefix: "/g"));
        var scanned = Map(group => group.MapEndpointsFrom(typeof(GeneratedRegistrationTests).Assembly, "/g"));

        Assert.NotEmpty(generated);
        Assert.Equal(scanned.Length, generated.Length);
        Assert.Equal(scanned, generated);
    }

    [Fact]
    public void Generated_registration_names_endpoints_the_same_way()
    {
        var names = Map(group => group.Map(routePrefix: "/g"));

        // Derived from the fixture's namespace, exactly as the reflective path derives it.
        Assert.Contains("Gen_InvoicesGet", names);
    }

    private static string[] Map(Action<EndpointGroup> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNativeEndpoints();

        var app = builder.Build();
        var routes = (IEndpointRouteBuilder)app;
        map(routes.MapEndpointGroup("Gen"));

        return [.. routes.DataSources
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .Where(name => name is not null)
            .OrderBy(name => name, StringComparer.Ordinal)!];
    }
}

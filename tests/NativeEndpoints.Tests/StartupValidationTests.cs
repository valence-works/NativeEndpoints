using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// A host that forgot <c>AddNativeEndpoints()</c> fails at mapping time with the remedy in the
/// message, instead of surfacing on the first binding failure at runtime — where the missing
/// problem writer turned the caller's real 400 into an opaque 500.
/// </summary>
public class StartupValidationTests
{
    [Fact]
    public void Mapping_without_AddNativeEndpoints_fails_at_map_time_naming_the_remedy()
    {
        var builder = WebApplication.CreateBuilder();
        // Deliberately no AddNativeEndpoints().
        using var app = builder.Build();
        var routes = (IEndpointRouteBuilder)app;

        var failure = Assert.Throws<InvalidOperationException>(() => routes.MapEndpointGroup("Startup"));

        Assert.Contains("AddNativeEndpoints()", failure.Message);
        Assert.Contains("IEndpointProblemWriter", failure.Message);
    }

    [Fact]
    public void A_scoped_problem_writer_registration_passes_the_map_time_check()
    {
        var builder = WebApplication.CreateBuilder();
        // Scope validation is what made an instantiating probe throw here: a scoped writer is a
        // legitimate lifetime for a request-coupled writer, and the check must observe the
        // registration without resolving it from the root provider. Deliberately scoped-only —
        // no AddNativeEndpoints — so the scoped registration is the one satisfying the check.
        builder.Host.UseDefaultServiceProvider(options => options.ValidateScopes = true);
        builder.Services.AddScoped<IEndpointProblemWriter, ScopedWriter>();
        using var app = builder.Build();
        var routes = (IEndpointRouteBuilder)app;

        var group = routes.MapEndpointGroup("Startup");

        Assert.Equal("Startup", group.Name);
    }

    private sealed class ScopedWriter : IEndpointProblemWriter
    {
        public Task WriteAsync(Microsoft.AspNetCore.Http.HttpContext context, EndpointProblem problem) =>
            Task.CompletedTask;
    }

    [Fact]
    public void Mapping_with_AddNativeEndpoints_succeeds()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNativeEndpoints();
        using var app = builder.Build();
        var routes = (IEndpointRouteBuilder)app;

        var group = routes.MapEndpointGroup("Startup");

        Assert.Equal("Startup", group.Name);
    }
}

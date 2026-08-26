using FastEndpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NativeEndpoints.Generated;

namespace NativeEndpoints.Benchmarks;

/// <summary>A started in-process host and a client into it, torn down together after a run.</summary>
public sealed class Stack(IHost host) : IDisposable
{
    /// <summary>The client for the host's TestServer. One per stack, reused across iterations.</summary>
    public HttpClient Client { get; } = host.GetTestClient();

    public void Dispose()
    {
        Client.Dispose();
        host.Dispose();
    }
}

/// <summary>
/// One factory per measured stack. Every host is the same shape - TestServer, AddRouting,
/// UseRouting, UseEndpoints - so the only thing that differs between benchmarks is the framework
/// layer under measurement, not the hosting around it. No Kestrel and no sockets: requests go
/// through the ASP.NET Core pipeline in memory.
/// </summary>
public static class Stacks
{
    /// <summary>The baseline: hand-written minimal API lambdas, no framework on top.</summary>
    public static Stack RawMinimalApi() =>
        Start(
            services => { },
            endpoints =>
            {
                endpoints.MapGet(
                    "/items/{id:int}",
                    (int id, string[] tag, int page) => Results.Ok(new ItemView(id, tag, page)));

                endpoints.MapPost(
                    "/items",
                    (CreateItem body) => Results.Ok(
                        new ItemCreated(body.Name, body.Sku, body.Quantity, body.Price, body.Active)));
            });

    /// <summary>NativeEndpoints with the reflective binder: the endpoint classes found by scanning.</summary>
    public static Stack NativeReflective() =>
        Start(
            services => services.AddNativeEndpoints(),
            endpoints => endpoints.MapEndpointGroup("Bench").MapEndpointsFrom(typeof(Stacks).Assembly));

    /// <summary>NativeEndpoints with the generated binder: the same classes through the emitted Map().</summary>
    public static Stack NativeGenerated() =>
        Start(
            services => services.AddNativeEndpoints(),
            endpoints => endpoints.MapEndpointGroup("Bench").Map());

    /// <summary>
    /// FastEndpoints. Discovery is pinned to this assembly because the benchmark child process's
    /// entry assembly is BenchmarkDotNet's generated host, where auto-discovery would find nothing.
    /// </summary>
    public static Stack FastEndpoints() =>
        Start(
            services => services.AddFastEndpoints(options =>
            {
                options.DisableAutoDiscovery = true;
                options.Assemblies = [typeof(Stacks).Assembly];
            }),
            endpoints => endpoints.MapFastEndpoints());

    private static Stack Start(
        Action<IServiceCollection> add,
        Action<IEndpointRouteBuilder> map)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    add(services);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(map);
                }))
            .Start();

        return new Stack(host);
    }
}

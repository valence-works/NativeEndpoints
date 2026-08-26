using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NativeEndpoints.Generated;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// Strict parsing through the full mapping pipeline, asserted against expected outcomes.
/// </summary>
/// <remarks>
/// The conformance suite proves the two binders agree; it would pass just as happily if both
/// dropped <c>options.StrictTypedParsing</c> on the floor — which is exactly the bug this suite
/// exists to catch. These tests pin what a strict endpoint actually returns, on the reflective
/// mapper and the generated registration alike.
/// </remarks>
public class StrictParsingEndToEndTests : IAsyncDisposable
{
    private readonly IHost _reflective;
    private readonly IHost _generated;

    public StrictParsingEndToEndTests()
    {
        _reflective = Host(group => group.MapEndpointsFrom(typeof(StrictParsingEndToEndTests).Assembly));
        _generated = Host(group => group.Map());
    }

    public static TheoryData<string, int, string[]> Requests() => new()
    {
        // The built-in converters, per docs/Binding.md's strict-parsing contract.
        { "/strict?page=7", 200, new[] { "\"page\":7" } },
        { "/strict?page=notanumber", 400, new[] { "\"page\"", "Value [notanumber] is not valid for a [Int32] property!" } },

        // A non-nullable typed member with no value at all is a strict failure too.
        { "/strict", 400, new[] { "\"page\"", "Value [] is not valid for a [Int32] property!" } },

        // Absence is not a failure for a registered value binder or a collection, strict or not.
        { "/strict-items", 200, new[] { "\"price\":0", "\"ids\":[]" } },

        // A registered value binder rejects what it cannot read, named by its wire key.
        { "/strict-items?price=notmoney&ids=1", 400, new[] { "\"price\"", "Value [notmoney] is not valid for a [Money] property!" } },

        // A collection element is as strict as a scalar.
        { "/strict-items?price=12.50&ids=1&ids=notanumber", 400, new[] { "\"ids\"", "Value [notanumber] is not valid for a [Int32] property!" } },
        { "/strict-items?price=12.50&ids=1&ids=2", 200, new[] { "\"price\":12.50", "\"ids\":[1,2]" } },

        // The same unreadable values through the lenient contract fall back to defaults.
        { "/lenient-items?price=notmoney&ids=1&ids=notanumber", 200, new[] { "\"price\":0", "\"ids\":[1,0]" } },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Both_mapping_paths_enforce_the_configured_strictness(string url, int status, string[] fragments)
    {
        foreach (var (label, host) in new[] { ("reflective", _reflective), ("generated", _generated) })
        {
            var (actual, body) = await Send(host, url);

            Assert.True(status == actual, $"{label} mapper returned {actual} for '{url}', expected {status}. Body: {body}");
            foreach (var fragment in fragments)
                Assert.Contains(fragment, body, StringComparison.Ordinal);
        }
    }

    private static async Task<(int Status, string Body)> Send(IHost host, string url)
    {
        using var client = host.GetTestClient();
        var response = await client.GetAsync(url);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static IHost Host(Action<EndpointGroup> map) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints(o => o.ValueBinders.Add<Money>(Money.TryParse));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => map(endpoints.MapEndpointGroup("Outcome")));
                }))
            .Start();

    public async ValueTask DisposeAsync()
    {
        await _reflective.StopAsync();
        _reflective.Dispose();
        await _generated.StopAsync();
        _generated.Dispose();
        GC.SuppressFinalize(this);
    }
}

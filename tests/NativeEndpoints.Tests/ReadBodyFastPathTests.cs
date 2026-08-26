using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>A contract whose every member may fall back from the body to the query.</summary>
public sealed record FallbackProbe(string? Name, int Count);

/// <summary>
/// A contract whose every member binds from a declared source, which is what lets the reflective
/// binder's plan prove the supplied-property set can never be consulted.
/// </summary>
public sealed record RoutedProbe([property: FromRoute] int Id, [property: FromQuery] int Page);

public sealed record ProbeEcho(string? Name, int Count);

public sealed record RoutedEcho(int Id, int Page);

/// <summary>
/// Pins the two ways <see cref="EndpointRequestBinder.ReadBodyAsync{T}(HttpContext, JsonSerializerOptions, EndpointBodyMode, bool)"/>
/// reads a body: the buffered pass that records supplied properties, and the streaming pass taken
/// when no bound member can consult them. The two must agree on everything a caller can observe
/// except the supplied set itself.
/// </summary>
public class ReadBodyFastPathTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public ReadBodyFastPathTests()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints();
                    // The default problem writer stamps a per-request traceId; stripped so two
                    // failures can be compared byte-for-byte across endpoints.
                    services.AddProblemDetails(options => options.CustomizeProblemDetails =
                        context => context.ProblemDetails.Extensions.Remove("traceId"));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        var group = endpoints.MapEndpointGroup("FastPath");

                        // Every member may consult the supplied set: the buffered pass stays.
                        group.MapHandler<FallbackProbe, ProbeEcho>(
                            "POST", "fallback", "Fallback",
                            (_, request, _) => Task.FromResult(new ProbeEcho(request.Name, request.Count)),
                            EndpointBodyMode.Required);

                        // No member can consult the supplied set: the body streams in one pass.
                        group.MapHandler<RoutedProbe, RoutedEcho>(
                            "POST", "routed/{id}", "Routed",
                            (_, request, _) => Task.FromResult(new RoutedEcho(request.Id, request.Page)),
                            EndpointBodyMode.Required);
                    });
                }))
            .Start();

        _client = _host.GetTestClient();
    }

    // ---- The buffered pass must survive for contracts that need the supplied set ----

    [Fact]
    public async Task Omitted_body_property_still_falls_through_to_the_query()
    {
        var response = await _client.PostAsync("/fallback?count=9", Json("""{"name":"x"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"name":"x","count":9}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Explicit_null_body_property_stays_null_over_a_matching_query_key()
    {
        var response = await _client.PostAsync("/fallback?name=zzz", Json("""{"name":null,"count":1}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"name":null,"count":1}""", await response.Content.ReadAsStringAsync());
    }

    // ---- The streaming pass must be observably identical ----

    [Fact]
    public async Task Fully_routed_contract_binds_and_still_validates_the_body()
    {
        var response = await _client.PostAsync("/routed/5?page=2", Json("""{"anything":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"id":5,"page":2}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Root_level_null_is_a_missing_body_on_the_streaming_pass()
    {
        var response = await _client.PostAsync("/routed/5?page=2", Json("null"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("A request body is required.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Malformed_body_produces_the_same_document_on_both_passes()
    {
        var buffered = await _client.PostAsync("/fallback", Json("{invalid"));
        var streamed = await _client.PostAsync("/routed/5?page=2", Json("{invalid"));

        Assert.Equal(HttpStatusCode.BadRequest, buffered.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, streamed.StatusCode);
        Assert.Equal(
            await buffered.Content.ReadAsStringAsync(),
            await streamed.Content.ReadAsStringAsync());
    }

    // ---- The two passes, compared directly on the same contract ----

    public static TheoryData<string> Payloads() =>
    [
        """{"name":"a","count":2}""",
        """{"name":null}""",
        "null",
        "[1,2]",
        "\"scalar\"",
        "{invalid",
        "",
        """{"name":"a","count":"notanumber"}""",
        """{"name":"a"} trailing""",
    ];

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Streamed_and_buffered_reads_agree(string payload)
    {
        var buffered = await Read(payload, JsonSerializerOptions.Web, needsSuppliedProperties: true);
        var streamed = await Read(payload, JsonSerializerOptions.Web, needsSuppliedProperties: false);

        Assert.Equal(buffered.Succeeded, streamed.Succeeded);
        Assert.Equal(buffered.Body, streamed.Body);
        Assert.Equal(buffered.Failure.Failure, streamed.Failure.Failure);
        Assert.Equal(buffered.Failure.Message, streamed.Failure.Message);

        // The one designed difference: the streaming pass records nothing, which counts every
        // property as supplied - exactly the semantics when nothing consults the set.
        Assert.Null(streamed.SuppliedProperties);
    }

    [Fact]
    public async Task Options_diverging_from_document_defaults_keep_the_buffered_pass()
    {
        // The buffered pass has always parsed with default document options, rejecting trailing
        // commas whatever the serializer options say. Options that would read different syntax must
        // keep that behaviour even when nothing consults the supplied set.
        var lenient = new JsonSerializerOptions(JsonSerializerOptions.Web) { AllowTrailingCommas = true };
        var read = await Read("""{"name":"a",}""", lenient, needsSuppliedProperties: false);

        Assert.False(read.Succeeded);
        Assert.Equal(EndpointBindingFailure.MalformedBody, read.Failure.Failure);
    }

    private static async Task<EndpointBodyResult<FallbackProbe>> Read(
        string payload, JsonSerializerOptions jsonOptions, bool needsSuppliedProperties)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        return await EndpointRequestBinder.ReadBodyAsync<FallbackProbe>(
            context, jsonOptions, EndpointBodyMode.Required, needsSuppliedProperties);
    }

    private static StringContent Json(string payload) => new(payload, Encoding.UTF8, "application/json");

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}

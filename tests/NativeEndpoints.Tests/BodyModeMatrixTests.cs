using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

public sealed record BodyModeProbe(string? Name, int Count);

public sealed record BodyModeView(string? Name, int Count);

/// <summary>
/// Every <see cref="EndpointBodyMode"/> crossed with the content-type shapes a caller can actually
/// send, through the full pipeline: status codes, which failures carry a problem document, and which
/// are a bare status by published contract.
/// </summary>
/// <remarks>
/// The matrix endpoints declare <c>accepts</c> spanning <c>text/plain</c> so a declared non-JSON
/// content type actually reaches the binder's media-type rules: with the default accepts of
/// <c>application/json</c> alone, routing's <c>AcceptsMatcherPolicy</c> enforces the endpoint's
/// <c>AcceptsMetadata</c> first and rejects such a request with a bare 415 before binding runs.
/// That default-accepts behavior is pinned by its own test below.
/// </remarks>
public class BodyModeMatrixTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public BodyModeMatrixTests()
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
                        var group = endpoints.MapEndpointGroup("BodyModes");
                        Map(group, "required", "Required", EndpointBodyMode.Required);
                        Map(group, "optional", "Optional", EndpointBodyMode.Optional);
                        Map(group, "required-ct", "RequiredWithContentType", EndpointBodyMode.RequiredWithContentType);
                        Map(group, "optional-ct", "OptionalWithContentType", EndpointBodyMode.OptionalWithContentType);
                        Map(group, "required-ct-payload", "RequiredWithContentTypeAndPayload", EndpointBodyMode.RequiredWithContentTypeAndPayload);

                        // Default accepts: routing enforces AcceptsMetadata before the binder runs.
                        group.MapHandler<BodyModeProbe, BodyModeView>(
                            "POST", "required-default-accepts", "RequiredDefaultAccepts",
                            (_, request, _) => Task.FromResult(new BodyModeView(request.Name, request.Count)),
                            EndpointBodyMode.Required);
                    });
                }))
            .Start();

        _client = _host.GetTestClient();
    }

    private static void Map(EndpointGroup group, string pattern, string operation, EndpointBodyMode bodyMode) =>
        group.MapHandler<BodyModeProbe, BodyModeView>(
            "POST", pattern, operation,
            (_, request, _) => Task.FromResult(new BodyModeView(request.Name, request.Count)),
            bodyMode,
            accepts: ["application/json", "text/plain"]);

    // ---- Required ----

    [Fact]
    public async Task Required_with_json_body_binds_from_the_body()
    {
        var response = await _client.PostAsync("/required", Json("""{"name":"body","count":3}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("body", 3), view);
    }

    [Fact]
    public async Task Required_with_text_plain_is_415_as_a_problem_document()
    {
        var response = await _client.PostAsync("/required", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("generalErrors", body);
    }

    [Fact]
    public async Task Required_with_no_content_type_still_reads_the_json_payload()
    {
        var response = await _client.PostAsync("/required", Untyped("""{"name":"undeclared","count":5}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("undeclared", 5), view);
    }

    [Fact]
    public async Task Required_with_null_json_body_is_400_missing_body()
    {
        var response = await _client.PostAsync("/required", Json("null"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("A request body is required.", body);
        Assert.Contains("generalErrors", body);
    }

    [Fact]
    public async Task Required_with_malformed_json_is_400_under_serializerErrors()
    {
        var response = await _client.PostAsync("/required", Json("{not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("serializerErrors", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Required_with_no_body_at_all_is_400_under_serializerErrors()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/required"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("serializerErrors", await response.Content.ReadAsStringAsync());
    }

    // ---- Optional ----

    [Fact]
    public async Task Optional_with_json_body_binds_from_the_body()
    {
        var response = await _client.PostAsync("/optional", Json("""{"name":"body","count":9}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("body", 9), view);
    }

    [Fact]
    public async Task Optional_with_no_body_binds_from_the_query()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/optional?name=query&count=7"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("query", 7), view);
    }

    [Fact]
    public async Task Optional_with_text_plain_skips_the_body_and_binds_from_the_query()
    {
        var response = await _client.PostAsync(
            "/optional?name=query&count=2", new StringContent("ignored", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("query", 2), view);
    }

    [Fact]
    public async Task Optional_with_no_content_type_skips_the_json_payload_and_binds_from_the_query()
    {
        var response = await _client.PostAsync(
            "/optional?name=query&count=4", Untyped("""{"name":"body","count":99}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("query", 4), view);
    }

    [Fact]
    public async Task Optional_with_malformed_json_is_400_under_serializerErrors()
    {
        var response = await _client.PostAsync("/optional", Json("{not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("serializerErrors", await response.Content.ReadAsStringAsync());
    }

    // ---- RequiredWithContentType ----

    [Fact]
    public async Task RequiredWithContentType_with_json_body_binds_from_the_body()
    {
        var response = await _client.PostAsync("/required-ct", Json("""{"name":"gated","count":1}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("gated", 1), view);
    }

    [Fact]
    public async Task RequiredWithContentType_with_text_plain_is_a_bare_415_with_an_empty_body()
    {
        var response = await _client.PostAsync("/required-ct", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequiredWithContentType_with_no_content_type_is_a_bare_415_with_an_empty_body()
    {
        var response = await _client.PostAsync("/required-ct", Untyped("""{"name":"undeclared","count":5}"""));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequiredWithContentType_with_null_json_body_is_400_missing_body_as_a_problem_document()
    {
        var response = await _client.PostAsync("/required-ct", Json("null"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("A request body is required.", body);
        Assert.Contains("generalErrors", body);
    }

    // ---- OptionalWithContentType ----

    [Fact]
    public async Task OptionalWithContentType_with_json_body_binds_from_the_body()
    {
        var response = await _client.PostAsync("/optional-ct", Json("""{"name":"gated","count":6}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("gated", 6), view);
    }

    [Fact]
    public async Task OptionalWithContentType_with_no_content_type_is_a_bare_415_with_an_empty_body()
    {
        var response = await _client.PostAsync("/optional-ct?name=query&count=7", Untyped("""{"name":"body","count":1}"""));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OptionalWithContentType_with_text_plain_is_a_bare_415_with_an_empty_body()
    {
        var response = await _client.PostAsync("/optional-ct", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OptionalWithContentType_with_null_json_body_binds_from_the_query()
    {
        var response = await _client.PostAsync("/optional-ct?name=query&count=8", Json("null"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("query", 8), view);
    }

    // ---- RequiredWithContentTypeAndPayload ----

    [Fact]
    public async Task RequiredWithContentTypeAndPayload_with_json_body_binds_from_the_body()
    {
        var response = await _client.PostAsync("/required-ct-payload", Json("""{"name":"gated","count":9}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<BodyModeView>();
        Assert.Equal(new BodyModeView("gated", 9), view);
    }

    [Fact]
    public async Task RequiredWithContentTypeAndPayload_with_text_plain_is_a_bare_415_with_an_empty_body()
    {
        var response = await _client.PostAsync("/required-ct-payload", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequiredWithContentTypeAndPayload_with_no_content_type_is_a_bare_415_with_an_empty_body()
    {
        var response = await _client.PostAsync("/required-ct-payload", Untyped("""{"name":"undeclared","count":5}"""));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The one case that separates this mode from <c>RequiredWithContentType</c>.</summary>
    [Fact]
    public async Task RequiredWithContentTypeAndPayload_with_null_json_body_is_a_bare_415_with_an_empty_body()
    {
        var response = await _client.PostAsync("/required-ct-payload", Json("null"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequiredWithContentTypeAndPayload_with_a_malformed_body_is_still_a_400_problem_document()
    {
        var response = await _client.PostAsync("/required-ct-payload", Json("{"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("serializerErrors", await response.Content.ReadAsStringAsync());
    }

    // ---- Default accepts ----

    [Fact]
    public async Task With_the_default_accepts_routing_rejects_text_plain_with_a_bare_415_before_binding()
    {
        var response = await _client.PostAsync(
            "/required-default-accepts", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    private static StringContent Json(string payload) => new(payload, Encoding.UTF8, "application/json");

    /// <summary>A payload sent with no Content-Type header at all.</summary>
    private static ByteArrayContent Untyped(string payload)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
        content.Headers.ContentType = null;
        return content;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Billing.Flat;
using NativeEndpoints.OpenApi;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The OpenAPI document a form endpoint produces.
/// </summary>
/// <remarks>
/// Asserted against the served document rather than against the transformers in isolation, because
/// the thing worth pinning is what a client generator will read.
/// </remarks>
public class OpenApiDocumentTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public OpenApiDocumentTests()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints();
                    services.AddOpenApi();
                    services.AddNativeEndpointsOpenApi();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapOpenApi();
                        var group = endpoints.MapEndpointGroup("Docs");
                        group.MapEndpoint<UploadEndpoint>();
                        group.MapEndpoint<UploadFilesEndpoint>();
                    });
                }))
            .Start();

        _client = _host.GetTestClient();
    }

    private async Task<JsonNode> Operation(string path, string method)
    {
        var document = JsonNode.Parse(await _client.GetStringAsync("/openapi/v1.json"))!;
        var operation = document["paths"]?[path]?[method];

        Assert.True(operation is not null, $"No {method} {path} in:\n{document.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
        return operation!;
    }

    private static JsonNode Multipart(JsonNode operation) =>
        operation["requestBody"]?["content"]?["multipart/form-data"]?["schema"]
        ?? throw new Xunit.Sdk.XunitException($"No multipart schema in:\n{operation.ToJsonString()}");

    [Fact]
    public async Task Form_fields_are_documented_in_the_request_body()
    {
        var schema = Multipart(await Operation("/upload/{id}", "post"));

        Assert.Equal("object", schema["type"]?.GetValue<string>());
        Assert.Equal("string", schema["properties"]?["Title"]?["type"]?.GetValue<string>());
        Assert.Equal("integer", schema["properties"]?["Count"]?["type"]?.GetValue<string>());
        Assert.Equal("array", schema["properties"]?["Tag"]?["type"]?.GetValue<string>());
    }

    [Fact]
    public async Task Form_fields_are_not_also_documented_as_query_parameters()
    {
        // OpenAPI has no `in: form`, and the parameter transformer's default arm maps anything it
        // does not recognise to the query string. Without the filter a generated client would put
        // these in the URL.
        var operation = await Operation("/upload/{id}", "post");
        var parameters = operation["parameters"]?.AsArray() ?? [];

        Assert.DoesNotContain(parameters, p => p?["name"]?.GetValue<string>() == "Title");
        Assert.DoesNotContain(parameters, p => p?["name"]?.GetValue<string>() == "Count");
    }

    [Fact]
    public async Task A_route_value_is_still_a_path_parameter_on_a_form_endpoint()
    {
        var operation = await Operation("/upload/{id}", "post");
        var parameters = operation["parameters"]!.AsArray();

        var id = Assert.Single(parameters, p => p?["name"]?.GetValue<string>() == "id");
        Assert.Equal("path", id!["in"]?.GetValue<string>());
        Assert.True(id["required"]?.GetValue<bool>());
    }

    [Fact]
    public async Task A_renamed_form_field_is_documented_under_its_wire_name()
    {
        var schema = Multipart(await Operation("/upload/{id}", "post"));

        Assert.NotNull(schema["properties"]?["legacy_name"]);
        Assert.Null(schema["properties"]?["LegacyName"]);
    }

    [Fact]
    public async Task A_file_member_is_documented_as_binary()
    {
        var schema = Multipart(await Operation("/files", "post"));

        Assert.Equal("string", schema["properties"]?["Required"]?["type"]?.GetValue<string>());
        Assert.Equal("binary", schema["properties"]?["Required"]?["format"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_file_collection_is_documented_as_an_array_of_binary()
    {
        var schema = Multipart(await Operation("/files", "post"));

        foreach (var member in (string[])["Pages", "Docs", "Everything"])
        {
            var property = schema["properties"]?[member];
            Assert.Equal("array", property?["type"]?.GetValue<string>());
            Assert.Equal("binary", property?["items"]?["format"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task The_request_body_covers_both_form_media_types()
    {
        var content = (await Operation("/upload/{id}", "post"))["requestBody"]?["content"];

        Assert.NotNull(content?["multipart/form-data"]);
        Assert.NotNull(content?["application/x-www-form-urlencoded"]);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}

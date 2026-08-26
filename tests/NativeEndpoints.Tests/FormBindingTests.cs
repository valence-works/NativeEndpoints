using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Billing.Flat;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// What a form endpoint actually binds, over real HTTP.
/// </summary>
/// <remarks>
/// The conformance suite proves the two binders agree; it cannot prove either is right, because two
/// hosts both answering 404 agree perfectly. These pin the values.
/// </remarks>
public class FormBindingTests : IAsyncDisposable
{
    private static readonly Guid Id = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly IHost _host;
    private readonly HttpClient _client;

    public FormBindingTests()
    {
        _host = Host();
        _client = _host.GetTestClient();
    }

    private static HttpRequestMessage Multipart(string url, params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent("test-boundary");
        foreach (var (key, value) in fields)
            content.Add(new StringContent(value), key);

        return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
    }

    private async Task<UploadView?> Upload(params (string Key, string Value)[] fields) =>
        await (await _client.SendAsync(Multipart($"/upload/{Id}", fields))).Content.ReadFromJsonAsync<UploadView>();

    [Fact]
    public async Task Every_form_shape_binds()
    {
        var view = await Upload(
            ("Title", "hello"), ("Count", "3"),
            ("Tag", "x"), ("Tag", "y"),
            ("Note", "n"), ("legacy_name", "old"));

        Assert.NotNull(view);
        Assert.Equal(Id, view.Id);                  // route, not the form
        Assert.Equal("hello", view.Title);          // scalar field
        Assert.Equal(3, view.Count);                // typed field
        Assert.Equal(["x", "y"], view.Tag);         // repeated key into an array
        Assert.Equal("n", view.Note);
        Assert.Equal("old", view.LegacyName);       // [FromForm("legacy_name")], a renamed field
    }

    [Fact]
    public async Task A_route_value_beats_a_form_field_of_the_same_name()
    {
        var view = await Upload(
            ("Title", "t"), ("Count", "1"),
            ("Id", "99999999-9999-9999-9999-999999999999"));

        Assert.NotNull(view);
        Assert.Equal(Id, view.Id);
    }

    [Fact]
    public async Task A_field_absent_from_the_form_falls_through_to_the_query()
    {
        var response = await _client.SendAsync(
            Multipart($"/upload/{Id}?Note=from-query", ("Title", "t"), ("Count", "1")));
        var view = await response.Content.ReadFromJsonAsync<UploadView>();

        Assert.NotNull(view);
        Assert.Equal("from-query", view.Note);
    }

    [Fact]
    public async Task Absent_form_collections_bind_empty_rather_than_null()
    {
        var view = await Upload(("Title", "t"), ("Count", "1"));

        Assert.NotNull(view);
        Assert.Empty(view.Tag);
        Assert.Null(view.Note);
    }

    [Fact]
    public async Task A_url_encoded_body_binds_the_same_as_multipart()
    {
        var response = await _client.PostAsync($"/upload/{Id}", new FormUrlEncodedContent(
        [
            new("Title", "t"), new("Count", "2"), new("Tag", "a"), new("Tag", "b")
        ]));
        var view = await response.Content.ReadFromJsonAsync<UploadView>();

        Assert.NotNull(view);
        Assert.Equal("t", view.Title);
        Assert.Equal(2, view.Count);
        Assert.Equal(["a", "b"], view.Tag);
    }

    [Fact]
    public async Task An_empty_field_is_the_empty_string_not_null()
    {
        // A form has no way to encode null, so this is the whole of the distinction: present and
        // empty, exactly as in the query string.
        var view = await Upload(("Title", ""), ("Count", "1"), ("Note", ""));

        Assert.NotNull(view);
        Assert.Equal(string.Empty, view.Title);
        Assert.Equal(string.Empty, view.Note);
    }

    [Fact]
    public async Task A_json_body_is_unsupported_media_on_a_form_endpoint()
    {
        var response = await _client.PostAsync($"/upload/{Id}",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Strict_parsing_over_a_form_reports_the_offending_field()
    {
        var response = await _client.SendAsync(Multipart("/strict-form", ("Page", "notanumber")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Value [notanumber] is not valid for a [Int32] property!",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_over_the_multipart_limit_is_a_caller_error_not_a_server_error()
    {
        // FormReader raises InvalidDataException for an exceeded multipart limit and for a malformed
        // boundary alike, with nothing but the message to tell them apart. Both are the caller's
        // problem, so both are a 400 — the property worth pinning is that neither reaches the
        // mapper's catch-all and becomes a 500 disclosing a stack trace.
        using var host = Host(services => services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 64));
        using var client = host.GetTestClient();

        var content = new MultipartFormDataContent("test-boundary");
        content.Add(new StringContent(new string('x', 4096)), "Title");
        content.Add(new StringContent("1"), "Count");

        var response = await client.PostAsync($"/upload/{Id}", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("limit", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await host.StopAsync();
    }

    private async Task<FileView?> UploadFiles(params (string Key, string FileName)[] files)
    {
        var content = new MultipartFormDataContent("test-boundary");
        content.Add(new StringContent("batch"), "Label");
        foreach (var (key, fileName) in files)
            content.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(fileName + "-body")), key, fileName);

        return await (await _client.PostAsync("/files", content)).Content.ReadFromJsonAsync<FileView>();
    }

    [Fact]
    public async Task Every_file_shape_binds()
    {
        var view = await UploadFiles(
            ("Required", "req.txt"), ("Avatar", "face.png"),
            ("Pages", "p1.pdf"), ("Pages", "p2.pdf"),
            ("Docs", "d1.doc"));

        Assert.NotNull(view);
        Assert.Equal("batch", view.Label);                       // a field alongside the files
        Assert.Equal("req.txt", view.RequiredName);              // IFormFile
        Assert.Equal("req.txt-body".Length, view.RequiredLength);
        Assert.Equal("face.png", view.AvatarName);               // IFormFile?
        Assert.Equal(["p1.pdf", "p2.pdf"], view.PageNames);      // IFormFile[], repeated key
        Assert.Equal(["d1.doc"], view.DocNames);                 // List<IFormFile>
        Assert.Equal(5, view.TotalFiles);                        // IFormFileCollection: every file
    }

    [Fact]
    public async Task An_absent_file_binds_null_and_absent_collections_bind_empty()
    {
        var view = await UploadFiles(("Required", "req.txt"));

        Assert.NotNull(view);
        Assert.Null(view.AvatarName);
        Assert.Empty(view.PageNames);
        Assert.Empty(view.DocNames);
        Assert.Equal(1, view.TotalFiles);
    }

    [Fact]
    public async Task A_file_absent_from_a_non_nullable_member_binds_null_rather_than_throwing()
    {
        // The generated binder suppresses the null to satisfy the non-nullable declaration; the
        // reflective one produces the same null. Neither invents a file, and neither throws — the
        // contract's own nullability is what says whether that is acceptable.
        var view = await UploadFiles();

        Assert.NotNull(view);
        Assert.Equal("<none>", view.RequiredName);
        Assert.Equal(-1, view.RequiredLength);
        Assert.Equal(0, view.TotalFiles);
    }

    [Fact]
    public async Task A_file_collection_takes_every_file_whatever_key_it_arrived_under()
    {
        var view = await UploadFiles(("Required", "r.txt"), ("Pages", "p.pdf"), ("Unrelated", "x.bin"));

        Assert.NotNull(view);
        Assert.Equal(3, view.TotalFiles);

        // ...while a named member still takes only its own.
        Assert.Equal(["p.pdf"], view.PageNames);
    }

    [Fact]
    public async Task A_url_encoded_body_has_no_files_and_binds_them_empty()
    {
        var response = await _client.PostAsync("/files", new FormUrlEncodedContent([new("Label", "batch")]));
        var view = await response.Content.ReadFromJsonAsync<FileView>();

        Assert.NotNull(view);
        Assert.Equal("batch", view.Label);
        Assert.Empty(view.PageNames);
        Assert.Equal(0, view.TotalFiles);
    }

    [Fact]
    public void A_form_endpoint_with_no_antiforgery_stance_will_not_map()
    {
        // Driven through MapOperation rather than an endpoint class, because a class declaring a form
        // body and no stance cannot exist in this assembly: it would either carry a route attribute
        // and be mapped by the generated registration every other test uses, or carry none and fail
        // the build under NE0001.
        var failure = Assert.Throws<InvalidOperationException>(() => Host(map: group =>
            group.MapOperation<StrictForm>(
                new EndpointOperationDescriptor
                {
                    Method = "POST",
                    Pattern = "undeclared",
                    Operation = "Undeclared",
                    BodyKind = EndpointBodyKind.Form
                },
                dispatch: (_, _, _) => Task.CompletedTask)));

        Assert.Contains("declares no antiforgery stance", failure.Message, StringComparison.Ordinal);
        Assert.Contains("options.RequireAntiforgery", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_opted_out_form_endpoint_carries_the_framework_antiforgery_metadata()
    {
        var source = _host.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var endpoint = source.Endpoints.Single(item => item.DisplayName?.Contains("upload/") == true);

        var metadata = endpoint.Metadata
            .OfType<Microsoft.AspNetCore.Antiforgery.IAntiforgeryMetadata>()
            .Single();

        Assert.False(metadata.RequiresValidation);
    }

    private static IHost Host(
        Action<IServiceCollection>? configure = null,
        Action<EndpointGroup>? map = null,
        Action<IApplicationBuilder>? configureApp = null) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints();
                    configure?.Invoke(services);
                })
                .Configure(app =>
                {
                    configureApp?.Invoke(app);
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        var group = endpoints.MapEndpointGroup("Form");
                        if (map is not null)
                        {
                            map(group);
                            return;
                        }

                        group.MapEndpoint<UploadEndpoint>();
                        group.MapEndpoint<StrictFormEndpoint>();
                        group.MapEndpoint<UploadFilesEndpoint>();
                    });
                }))
            .Start();

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NativeEndpoints.Generated;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The same requests through the reflective binder and the generated one, asserted identical.
/// </summary>
/// <remarks>
/// Two implementations of the same semantics is the real risk in generating a binder: a divergence
/// would be silent, and would appear only in whichever environment happened to have the generator
/// on. This is the suite that makes the claim "both produce identical results" checkable rather than
/// aspirational.
/// </remarks>
public class BinderConformanceTests : IAsyncDisposable
{
    private readonly IHost _reflective;
    private readonly IHost _generated;

    public BinderConformanceTests()
    {
        _reflective = Host(group => group.MapEndpointsFrom(typeof(BinderConformanceTests).Assembly));
        _generated = Host(group => group.Map());
    }

    private static readonly Guid FormId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    /// <summary>
    /// The form cases, keyed by name. A factory rather than a message, because every case is sent
    /// twice — once per host — and an HttpRequestMessage cannot be reused.
    /// </summary>
    /// <remarks>
    /// The boundary is fixed rather than the default random GUID, so both hosts receive byte-identical
    /// requests and a failure reproduces.
    /// </remarks>
    private static readonly Dictionary<string, Func<HttpRequestMessage>> FormCases = new()
    {
        ["form:every-field"] = () => Multipart($"/upload/{FormId}",
            ("Title", "hello"), ("Count", "3"), ("Tag", "x"), ("Note", "n"), ("legacy_name", "old")),

        // Repeated keys. The reflective binder claims collection members before it reaches the query,
        // so this is the case that catches a form step ordered after that claim.
        ["form:repeated-key"] = () => Multipart($"/upload/{FormId}",
            ("Title", "t"), ("Count", "1"), ("Tag", "a"), ("Tag", "b"), ("Tag", "c")),

        // A repeated key bound to a scalar takes the first value, exactly as a repeated query key
        // does — never the comma-join.
        ["form:repeated-scalar"] = () => Multipart($"/upload/{FormId}",
            ("Title", "FIRST"), ("Title", "second"), ("Count", "1")),

        ["form:absent-collection"] = () => Multipart($"/upload/{FormId}", ("Title", "t"), ("Count", "1")),

        // A form field cannot be null, so an empty field is the empty string as in the query string.
        ["form:empty-value"] = () => Multipart($"/upload/{FormId}", ("Title", ""), ("Count", "")),

        ["form:unreadable-number"] = () => Multipart($"/upload/{FormId}", ("Title", "t"), ("Count", "nonsense")),

        // Mixed casing, to pin that a form collection matches keys the way the query one does.
        ["form:mixed-case-key"] = () => Multipart($"/upload/{FormId}", ("TITLE", "shouty"), ("count", "2")),

        // The route carries {id} and the form sends one too. Route precedence must still win.
        ["form:route-beats-form"] = () => Multipart($"/upload/{FormId}",
            ("Title", "t"), ("Count", "1"), ("Id", "99999999-9999-9999-9999-999999999999")),

        // Absent from the form, present in the query: the fallthrough after the body step.
        ["form:falls-through-to-query"] = () => Multipart($"/upload/{FormId}?Note=from-query",
            ("Title", "t"), ("Count", "1")),

        ["form:urlencoded"] = () => new(HttpMethod.Post, $"/upload/{FormId}")
        {
            Content = new FormUrlEncodedContent(
                [new("Title", "t"), new("Count", "2"), new("Tag", "a"), new("Tag", "b")])
        },

        ["form:strict-ok"] = () => Multipart("/strict-form", ("Page", "7")),
        ["form:strict-unreadable"] = () => Multipart("/strict-form", ("Page", "notanumber")),
        ["form:strict-unreadable-guid"] = () => Multipart("/strict-form", ("Page", "7"), ("Filter", "not-a-guid")),

        // A content type the endpoint does not accept. Routing answers this one, before the binder.
        ["form:wrong-content-type"] = () => new(HttpMethod.Post, $"/upload/{FormId}")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        },

        // Files. Each shape, and the absent case for each.
        ["files:every-shape"] = () => Files(
            ("Required", "req.txt"), ("Avatar", "face.png"),
            ("Pages", "p1.pdf"), ("Pages", "p2.pdf"), ("Docs", "d1.doc")),

        ["files:only-required"] = () => Files(("Required", "req.txt")),
        ["files:none-at-all"] = () => Files(),
        ["files:repeated-under-one-key"] = () => Files(
            ("Required", "req.txt"), ("Pages", "a.pdf"), ("Pages", "b.pdf"), ("Pages", "c.pdf")),
    };

    private static HttpRequestMessage Multipart(string url, params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent("conformance-boundary");
        foreach (var (key, value) in fields)
            content.Add(new StringContent(value), key);

        return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
    }

    /// <summary>A multipart request carrying a Label field and the named files.</summary>
    private static HttpRequestMessage Files(params (string Key, string FileName)[] files)
    {
        var content = new MultipartFormDataContent("conformance-boundary");
        content.Add(new StringContent("batch"), "Label");
        foreach (var (key, fileName) in files)
        {
            // Content derived from the name, so a file bound under the wrong key shows up as a
            // different length rather than passing by coincidence.
            content.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(fileName + "-body")), key, fileName);
        }

        return new HttpRequestMessage(HttpMethod.Post, "/files") { Content = content };
    }

    public static TheoryData<string> Requests() =>
    [
        "/probe/abc?tag=x&tag=y&page=1&page=2&slug=HELLO&price=12.50",
        "/probe/abc?slug=a&price=1",
        "/probe/abc?tag=only&slug=z&price=0",
        "/probe/WITH-CASE?slug=MiXeD&price=99.99&page=7",
        "/probe/abc?slug=a&price=nonsense",
        "/probe/abc?slug=&price=1",
        "/probe/abc?page=notanumber&slug=a&price=1",
        "/probe/abc?page=1&page=notanumber&slug=a&price=1",

        // A repeated key bound to a scalar takes the first value, in both binders alike: slug is a
        // scalar IParsable and price goes through a registered parser.
        "/probe/abc?slug=FIRST&slug=second&price=1",
        "/probe/abc?slug=a&price=12.50&price=99",

        // Strict parsing: the path where a generated binder could most easily disagree with the
        // reflective one, since each decides independently what an unreadable value means.
        "/strict?page=7",
        "/strict?page=notanumber",
        "/strict?page=7&filter=not-a-guid",
        "/strict?page=7&filter=11111111-2222-3333-4444-555555555555&term=x",
        "/strict?page=",
        "/strict?page=7&filter=",
        "/strict",

        // Forms and files. Every branch of the inference chain, on both binders.
        "form:every-field",
        "form:repeated-key",
        "form:repeated-scalar",
        "form:absent-collection",
        "form:empty-value",
        "form:unreadable-number",
        "form:mixed-case-key",
        "form:route-beats-form",
        "form:falls-through-to-query",
        "form:urlencoded",
        "form:strict-ok",
        "form:strict-unreadable",
        "form:strict-unreadable-guid",
        "form:wrong-content-type",
        "files:every-shape",
        "files:only-required",
        "files:none-at-all",
        "files:repeated-under-one-key",

        // A repeated key on a strict scalar parses the first value; an unreadable first value is
        // rejected naming it, never the comma-join.
        "/strict?page=1&page=2",
        "/strict?page=notanumber&page=2",

        // Strict parsing over a registered value binder and collection elements, and the same
        // requests through the lenient contract, so both the failure and the fallback are compared.
        "/strict-items?price=12.50&ids=1&ids=2",
        "/strict-items?price=notmoney&ids=1",
        "/strict-items?price=12.50&ids=1&ids=notanumber",
        "/strict-items?price=12.50&ids=",
        "/strict-items?price=",
        "/strict-items",
        "/lenient-items?price=notmoney&ids=1&ids=notanumber",
        "/lenient-items?price=&ids=",
        "/lenient-items",

        // A nullable reference-type IParsable member: absence binds null even under strict
        // parsing, while a blank or unparseable value is rejected strictly and defaulted leniently.
        "/strict-phone",
        "/strict-phone?phone=",
        "/strict-phone?phone=notaphone",
        "/strict-phone?phone=555-0100",
        "/lenient-phone",
        "/lenient-phone?phone=",
        "/lenient-phone?phone=notaphone",
        "/lenient-phone?phone=555-0100",

        // Contracts with constructor-parameter defaults fall back to the reflective mapper on the
        // generated host; the responses must still be identical, absent and present alike.
        "/defaulted",
        "/defaulted?page=9",
        "/declared-default",
        "/declared-default?page=9",
        "/declared-default?page=notanumber",
        "/strict-declared-default",
        "/strict-declared-default?page=9",
        "/strict-declared-default?page=notanumber",
    ];

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Both_binders_produce_the_same_response(string url)
    {
        var reflective = await Send(_reflective, url);
        var generated = await Send(_generated, url);

        Assert.Equal(reflective.Status, generated.Status);
        Assert.Equal(reflective.Body, generated.Body);
    }

    [Fact]
    public async Task A_property_bound_contract_keeps_the_body_through_both_binders()
    {
        // The exact probe from the review of the discarded-body bug: a contract with a
        // parameterless constructor and settable properties must echo the body's values from both
        // hosts, not the type's defaults. On the generated host this also proves the endpoint fell
        // back to the reflective mapper rather than being emitted as `new TRequest()`.
        var results = new List<(int Status, string Body)>();
        foreach (var host in new[] { _reflective, _generated })
        {
            using var client = host.GetTestClient();
            var response = await client.PostAsync("/widget-form", new StringContent(
                """{"name":"widget","count":7}""", System.Text.Encoding.UTF8, "application/json"));
            results.Add(((int)response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.Equal(results[0], results[1]);
        Assert.Equal(200, results[0].Status);
        Assert.Contains("\"name\":\"widget\"", results[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"count\":7", results[0].Body, StringComparison.Ordinal);
    }

    private static async Task<(int Status, string Body)> Send(IHost host, string url)
    {
        using var client = host.GetTestClient();
        var request = FormCases.TryGetValue(url, out var factory)
            ? factory()
            : new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant", "acme");

        var response = await client.SendAsync(request);
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
                    // The default problem writer stamps a per-request traceId, so two identical
                    // binders would still produce different bytes on a 400. Stripped here so the
                    // comparison sees only what the binders decided.
                    services.AddProblemDetails(options => options.CustomizeProblemDetails =
                        context => context.ProblemDetails.Extensions.Remove("traceId"));
                    services.AddAuthentication("test")
                        .AddScheme<AuthenticationSchemeOptions, ConformanceAuth>("test", null);
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseEndpoints(endpoints => map(endpoints.MapEndpointGroup("Conformance")));
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

    private sealed class ConformanceAuth(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("sub", "user-1")], "test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}

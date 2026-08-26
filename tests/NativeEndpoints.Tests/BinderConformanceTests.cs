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
        var request = new HttpRequestMessage(HttpMethod.Get, url);
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

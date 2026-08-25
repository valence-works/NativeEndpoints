using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>A domain type the binder cannot know about, reached through IParsable.</summary>
public readonly record struct Slug(string Value) : IParsable<Slug>
{
    public static Slug Parse(string s, IFormatProvider? provider) => new(s.ToLowerInvariant());

    public static bool TryParse(string? s, IFormatProvider? provider, out Slug result)
    {
        result = new Slug(s?.ToLowerInvariant() ?? string.Empty);
        return !string.IsNullOrWhiteSpace(s);
    }
}

/// <summary>A domain type reached through a registered parser instead.</summary>
public readonly record struct Money(decimal Amount)
{
    public static bool TryParse(string raw, IFormatProvider? provider, out Money result)
    {
        var parsed = decimal.TryParse(raw, System.Globalization.NumberStyles.Number, provider, out var amount);
        result = new Money(amount);
        return parsed;
    }
}

public sealed record Probe(
    string Id,
    string[] Tag,
    List<int> Page,
    Slug Slug,
    Money Price,
    [property: FromHeader("X-Tenant")] string? Tenant,
    [property: FromClaim("sub")] string? Subject);

public sealed record ProbeView(string Id, string[] Tag, int[] Page, string Slug, decimal Price, string? Tenant, string? Subject);

[Get("probe/{id}")]
public sealed class ProbeEndpoint : ApiEndpoint<Probe, ProbeView>
{
    public override Task<ProbeView> HandleAsync(Probe request, CancellationToken cancellationToken) =>
        Task.FromResult(new ProbeView(
            request.Id, request.Tag, [.. request.Page], request.Slug.Value,
            request.Price.Amount, request.Tenant, request.Subject));
}

/// <summary>Every binding source the library claims to support, exercised over real HTTP.</summary>
public class BindingTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public BindingTests()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints(o => o.ValueBinders.Add<Money>(Money.TryParse));
                    services.AddAuthentication("test").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, StubAuth>("test", null);
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapEndpointGroup("Probe").MapEndpoint<ProbeEndpoint>());
                }))
            .Start();

        _client = _host.GetTestClient();
    }

    [Fact]
    public async Task Every_source_binds()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/probe/abc?tag=x&tag=y&page=1&page=2&slug=HELLO&price=12.50");
        request.Headers.Add("X-Tenant", "acme");

        var view = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<ProbeView>();

        Assert.NotNull(view);
        Assert.Equal("abc", view.Id);                       // route
        Assert.Equal(["x", "y"], view.Tag);                 // repeated query keys into an array
        Assert.Equal([1, 2], view.Page);                    // and into a List<int>
        Assert.Equal("hello", view.Slug);                   // IParsable, no registration
        Assert.Equal(12.50m, view.Price);                   // registered parser
        Assert.Equal("acme", view.Tenant);                  // header
        Assert.Equal("user-1", view.Subject);               // claim
    }

    [Fact]
    public async Task Absent_collections_bind_empty_rather_than_null()
    {
        var view = await (await _client.GetAsync("/probe/abc?slug=a&price=1")).Content.ReadFromJsonAsync<ProbeView>();

        Assert.NotNull(view);
        Assert.Empty(view.Tag);
        Assert.Empty(view.Page);
        Assert.Null(view.Tenant);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class StubAuth(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("sub", "user-1")], "test");
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(
                new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}

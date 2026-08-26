using Microsoft.AspNetCore.Http;
using NativeEndpoints;

namespace Billing.Flat;

/// <summary>No Endpoints namespace segment, so the class name is used instead.</summary>
[NativeEndpoints.Post("ledger")]
public sealed class PostLedgerEntry : ApiEndpoint<AddEntry>
{
    public override Task HandleAsync(AddEntry request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The request contract.</summary>
public sealed record AddEntry(string Account);

/// <summary>Declares its operation explicitly, overriding derivation.</summary>
[NativeEndpoints.Get("declared")]
public sealed class DeclaredOperation : ApiEndpoint<AddEntry>
{
    public override void Configure(ApiEndpointOptions options) => options.Operation = "Explicit";

    public override Task HandleAsync(AddEntry request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Strict parsing, so conformance covers the path where the two binders could diverge.</summary>
public sealed record StrictQuery(int Page, Guid? Filter, string? Term);

/// <summary>The response, echoed so a divergence shows up as different bytes.</summary>
public sealed record StrictView(int Page, string? Filter, string? Term);

[NativeEndpoints.Get("strict")]
public sealed class StrictEndpoint : ApiEndpoint<StrictQuery, StrictView>
{
    public override void Configure(ApiEndpointOptions options) => options.StrictTypedParsing = true;

    public override Task<StrictView> HandleAsync(StrictQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new StrictView(request.Page, request.Filter?.ToString(), request.Term));
}

/// <summary>
/// A registered value binder type and a collection, so strict parsing covers the converter paths
/// the built-in types do not reach.
/// </summary>
public sealed record ItemsQuery(NativeEndpoints.Tests.Money Price, int[] Ids);

/// <summary>The response, echoed so a divergence shows up as different bytes.</summary>
public sealed record ItemsView(decimal Price, int[] Ids);

[NativeEndpoints.Get("strict-items")]
public sealed class StrictItemsEndpoint : ApiEndpoint<ItemsQuery, ItemsView>
{
    public override void Configure(ApiEndpointOptions options) => options.StrictTypedParsing = true;

    public override Task<ItemsView> HandleAsync(ItemsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new ItemsView(request.Price.Amount, request.Ids));
}

/// <summary>The same contract without strict parsing, pinning the lenient fallbacks.</summary>
[NativeEndpoints.Get("lenient-items")]
public sealed class LenientItemsEndpoint : ApiEndpoint<ItemsQuery, ItemsView>
{
    public override Task<ItemsView> HandleAsync(ItemsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new ItemsView(request.Price.Amount, request.Ids));
}

/// <summary>
/// A reference type reached through IParsable, so conformance covers the nullable-reference
/// converter path: absent binds null even under strict parsing, blank and unparseable do not.
/// </summary>
public sealed class ReviewPhone : IParsable<ReviewPhone>
{
    private ReviewPhone(string number) => Number = number;

    public string Number { get; }

    public static ReviewPhone Parse(string s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException(s);

    public static bool TryParse(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ReviewPhone result)
    {
        var valid = !string.IsNullOrWhiteSpace(s) && s.All(c => char.IsDigit(c) || c == '-');
        result = valid ? new ReviewPhone(s!) : null!;
        return valid;
    }
}

/// <summary>A nullable reference-type IParsable member; omitting it must bind null, strict or not.</summary>
public sealed record PhoneQuery(ReviewPhone? Phone);

/// <summary>The response, echoed so a divergence shows up as different bytes.</summary>
public sealed record PhoneView(string? Phone);

[NativeEndpoints.Get("strict-phone")]
public sealed class StrictPhoneEndpoint : ApiEndpoint<PhoneQuery, PhoneView>
{
    public override void Configure(ApiEndpointOptions options) => options.StrictTypedParsing = true;

    public override Task<PhoneView> HandleAsync(PhoneQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new PhoneView(request.Phone?.Number));
}

/// <summary>The same contract without strict parsing, pinning the lenient fallbacks.</summary>
[NativeEndpoints.Get("lenient-phone")]
public sealed class LenientPhoneEndpoint : ApiEndpoint<PhoneQuery, PhoneView>
{
    public override Task<PhoneView> HandleAsync(PhoneQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new PhoneView(request.Phone?.Number));
}

/// <summary>
/// A constructor-parameter default. The generator marks the contract not generatable — defaults
/// are compile-time constants the emitter cannot re-literalize safely — so the generated Map()
/// registers this endpoint through the reflective mapper, which honors the default.
/// </summary>
public sealed record DefaultedQuery(int Page = 3);

/// <summary>The response, echoed so a divergence shows up as different bytes.</summary>
public sealed record DefaultedView(int Page);

[NativeEndpoints.Get("defaulted")]
public sealed class DefaultedEndpoint : ApiEndpoint<DefaultedQuery, DefaultedView>
{
    public override Task<DefaultedView> HandleAsync(DefaultedQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new DefaultedView(request.Page));
}

/// <summary>A declared-source member with a default: absence binds 1, lenient and strict alike.</summary>
public sealed record DeclaredDefaultQuery([NativeEndpoints.FromQuery] int Page = 1);

[NativeEndpoints.Get("declared-default")]
public sealed class DeclaredDefaultEndpoint : ApiEndpoint<DeclaredDefaultQuery, DefaultedView>
{
    public override Task<DefaultedView> HandleAsync(DeclaredDefaultQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new DefaultedView(request.Page));
}

[NativeEndpoints.Get("strict-declared-default")]
public sealed class StrictDeclaredDefaultEndpoint : ApiEndpoint<DeclaredDefaultQuery, DefaultedView>
{
    public override void Configure(ApiEndpointOptions options) => options.StrictTypedParsing = true;

    public override Task<DefaultedView> HandleAsync(DeclaredDefaultQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new DefaultedView(request.Page));
}

/// <summary>
/// A property-bound contract: a parameterless constructor with settable properties. The generator
/// marks it not generatable — the emitted <c>new TRequest()</c> would discard the deserialized
/// body — so both mapping paths bind it through the reflective BindProperties path.
/// </summary>
public sealed class WidgetForm
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>The response, echoed so a discarded body shows up as different bytes.</summary>
public sealed record WidgetFormView(string Name, int Count);

[NativeEndpoints.Post("widget-form")]
public sealed class WidgetFormEndpoint : ApiEndpoint<WidgetForm, WidgetFormView>
{
    public override Task<WidgetFormView> HandleAsync(WidgetForm request, CancellationToken cancellationToken) =>
        Task.FromResult(new WidgetFormView(request.Name, request.Count));
}

/// <summary>The response returned by <see cref="StatusEndpoint"/>.</summary>
public sealed record ServiceStatus(string State, int UptimeSeconds);

/// <summary>The no-request shape: nothing binds, and the response is written as JSON.</summary>
[NativeEndpoints.Get("status")]
public sealed class StatusEndpoint : ApiEndpointWithoutRequest<ServiceStatus>
{
    public override Task<ServiceStatus> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ServiceStatus("healthy", 42));
}

/// <summary>The raw shape: no contract, and the handler writes a non-JSON response itself.</summary>
[NativeEndpoints.Get("raw-export")]
public sealed class RawExportEndpoint : NativeEndpoints.ApiEndpoint
{
    public override async Task HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.StatusCode = StatusCodes.Status202Accepted;
        HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await HttpContext.Response.WriteAsync("raw export", cancellationToken);
    }
}

/// <summary>Thrown by <see cref="RawFailureEndpoint"/>; translated to a 409 in RawEndpointTests.</summary>
public sealed class RawConflictException : Exception;

/// <summary>A raw endpoint that throws, proving the shared failure path still applies.</summary>
[NativeEndpoints.Get("raw-throw/{kind}")]
public sealed class RawFailureEndpoint : NativeEndpoints.ApiEndpoint
{
    public override Task HandleAsync(CancellationToken cancellationToken) =>
        throw HttpContext.Request.RouteValues["kind"] switch
        {
            "conflict" => new RawConflictException(),
            _ => (Exception)new InvalidOperationException("sensitive connection string detail")
        };
}

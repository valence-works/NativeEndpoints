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

public sealed record UploadForm(
    Guid Id,
    string Title,
    int Count,
    string[] Tag,
    string? Note,
    [property: FromForm("legacy_name")] string? LegacyName);

/// <summary>Echoed in full, so a divergence between the two binders shows up as different bytes.</summary>
public sealed record UploadView(Guid Id, string Title, int Count, string[] Tag, string? Note, string? LegacyName);

/// <summary>
/// The route carries <c>{id}</c> while the form also sends an <c>id</c> field, so this pins that
/// route precedence still wins over the body when the body is a form.
/// </summary>
[NativeEndpoints.Post("upload/{id}")]
public sealed class UploadEndpoint : ApiEndpoint<UploadForm, UploadView>
{
    public override void Configure(ApiEndpointOptions options)
    {
        options.BodyKind = EndpointBodyKind.Form;

        // The conformance host composes its pipeline by hand and so has no antiforgery middleware.
        // Declaring the stance is mandatory for a form endpoint; this one opts out explicitly.
        options.RequireAntiforgery = false;
    }

    public override Task<UploadView> HandleAsync(UploadForm request, CancellationToken cancellationToken) =>
        Task.FromResult(new UploadView(
            request.Id, request.Title, request.Count, request.Tag, request.Note, request.LegacyName));
}

/// <summary>Strict parsing over a form, where the two binders decide independently what a bad field means.</summary>
public sealed record StrictForm(int Page, Guid? Filter, string? Term);

[NativeEndpoints.Post("strict-form")]
public sealed class StrictFormEndpoint : ApiEndpoint<StrictForm, StrictView>
{
    public override void Configure(ApiEndpointOptions options)
    {
        options.BodyKind = EndpointBodyKind.Form;
        options.StrictTypedParsing = true;
        options.RequireAntiforgery = false;
    }

    public override Task<StrictView> HandleAsync(StrictForm request, CancellationToken cancellationToken) =>
        Task.FromResult(new StrictView(request.Page, request.Filter?.ToString(), request.Term));
}

/// <summary>
/// Every file shape the binder supports, including a non-nullable one so the emitter's null
/// suppression is exercised rather than assumed.
/// </summary>
public sealed record UploadFiles(
    string Label,
    IFormFile Required,
    IFormFile? Avatar,
    IFormFile[] Pages,
    List<IFormFile> Docs,
    IFormFileCollection Everything);

/// <summary>Names and lengths rather than the files, so a divergence shows up as different bytes.</summary>
public sealed record FileView(
    string Label,
    string RequiredName,
    long RequiredLength,
    string? AvatarName,
    string[] PageNames,
    string[] DocNames,
    int TotalFiles);

[NativeEndpoints.Post("files")]
public sealed class UploadFilesEndpoint : ApiEndpoint<UploadFiles, FileView>
{
    public override void Configure(ApiEndpointOptions options)
    {
        options.BodyKind = EndpointBodyKind.Form;
        options.RequireAntiforgery = false;
    }

    public override Task<FileView> HandleAsync(UploadFiles request, CancellationToken cancellationToken) =>
        Task.FromResult(new FileView(
            request.Label,
            request.Required?.FileName ?? "<none>",
            request.Required?.Length ?? -1,
            request.Avatar?.FileName,
            [.. request.Pages.Select(file => file.FileName)],
            [.. request.Docs.Select(file => file.FileName)],
            request.Everything.Count));
}

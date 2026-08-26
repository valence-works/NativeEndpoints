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

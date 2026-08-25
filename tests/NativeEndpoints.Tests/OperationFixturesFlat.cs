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

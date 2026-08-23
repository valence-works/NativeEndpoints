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

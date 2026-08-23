using NativeEndpoints;

namespace Billing.Endpoints.Invoices.Get;

/// <summary>Named by its folder, so the operation derives as InvoicesGet.</summary>
[NativeEndpoints.Get("invoices/{invoiceId}")]
public sealed class Endpoint : ApiEndpoint<GetInvoice>
{
    public override Task HandleAsync(GetInvoice request, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The request contract, bound from the route.</summary>
public sealed record GetInvoice(string InvoiceId);

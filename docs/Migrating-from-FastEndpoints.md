# Migrating from FastEndpoints

FastEndpoints is mature and does considerably more than this library. Migrate only if you want the
endpoint-class shape without the surrounding framework, or if you need endpoint assemblies to unload.

## Measure first

Before changing anything, find out whether you actually have the problem this library solves. The
test kit works against any endpoint framework:

```bash
dotnet add package NativeEndpoints.Testing
```

If your assemblies already collect, unload safety is not a reason to move, and the rest of this page
is about ergonomics rather than correctness.

## Shape mapping

| FastEndpoints | NativeEndpoints |
|---|---|
| `Endpoint<TRequest, TResponse>` | `ApiEndpoint<TRequest, TResponse>` |
| `Endpoint<TRequest>` | `ApiEndpoint<TRequest>` (204) or `ApiEndpointWithResult<,>` |
| `EndpointWithoutRequest<TResponse>` | `ApiEndpointWithoutRequest<TResponse>` |
| `Configure()` with `Verbs()` / `Routes()` | `[Get]`, `[Post]`, … attributes |
| `AllowAnonymous()` in `Configure` | `options.Convention(b => b.AllowAnonymous())` |
| `Policies()`, `Permissions()` | `options.Convention(b => b.RequireAuthorization(...))`, or an `IEndpointConventionAttribute` |
| `HandleAsync(req, ct)` | `HandleAsync(request, cancellationToken)` |
| `SendAsync`, `SendOkAsync` | Return the response; the mapper writes it |
| `SendNoContentAsync` | Derive from `ApiEndpoint<TRequest>` |
| `SendAsync(x, statusCode)` | `ApiEndpointWithResult<,>` returning `EndpointResult.Status(...)` |
| `app.UseFastEndpoints()` | `app.MapEndpointGroup().MapEndpointsFrom(assembly)` |
| Global discovery | Explicit `MapEndpointsFrom(assembly)` per group |
| `Summary`, `Description` | `options.Convention(b => b.WithSummary(...).WithDescription(...))` |
| Validators (FluentValidation) | No equivalent. Validate in the handler, or add FluentValidation yourself |
| Pre/post processors | No equivalent. Use ASP.NET Core endpoint filters |
| Command bus, job queues, versioning | No equivalent, and not planned |

## Worked example

Before:

```csharp
public class GetInvoiceEndpoint : Endpoint<GetInvoiceRequest, InvoiceResponse>
{
    public IInvoiceStore Store { get; set; } = default!;

    public override void Configure()
    {
        Get("/api/invoices/{invoiceId}");
        Policies("invoices:read");
    }

    public override async Task HandleAsync(GetInvoiceRequest req, CancellationToken ct)
    {
        var invoice = await Store.GetAsync(req.InvoiceId, ct);
        await SendAsync(InvoiceResponse.From(invoice), cancellation: ct);
    }
}
```

After:

```csharp
namespace Billing.Endpoints.Invoices.Get;

[Get("invoices/{invoiceId}")]
public sealed class Endpoint(IInvoiceStore store) : ApiEndpoint<GetInvoiceRequest, InvoiceResponse>
{
    public override void Configure(ApiEndpointOptions options) =>
        options.Convention(b => b.RequireAuthorization("invoices:read"));

    public override async Task<InvoiceResponse> HandleAsync(GetInvoiceRequest request, CancellationToken ct) =>
        InvoiceResponse.From(await store.GetAsync(request.InvoiceId, ct));
}
```

Four differences worth noting. Dependencies move from property injection to the constructor. The
route loses its prefix, which now belongs to the group. The response is returned rather than sent.
And the operation identifier comes from the namespace, so it is not written down anywhere.

## Things that will bite

**Binding is narrower.** Route, body, and query only, over seven scalar types. Headers, claims, query
collections, and forms have no equivalent yet. Check your contracts before committing to the move.

**No validation.** FastEndpoints wires FluentValidation for you. Here, validate in the handler or
register FluentValidation yourself and call it.

**Operation identifiers change.** They become `{Group}_{Operation}`, which changes generated client
method names. Pin the old ones with `options.Operation` if you have published clients.

**Registration is explicit.** There is no global discovery. Every group is mapped by a call you
write, which is the point, but it does mean an endpoint you forget to map simply does not exist.

## Migrating incrementally

Both can run in the same host. Map NativeEndpoints groups alongside `UseFastEndpoints()` and move one
resource at a time; nothing in either library is process-global in a way that conflicts.

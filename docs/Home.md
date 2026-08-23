# NativeEndpoints

A structured programming model for ASP.NET Core Minimal APIs. One class per endpoint, carrying its
route, its metadata, and its handling. Ordinary ASP.NET Core underneath, all the way down.

> **Pre-release.** The API is still settling. These pages describe what is implemented today, and
> mark what is planned rather than describing it as though it already exists.

```bash
dotnet add package NativeEndpoints
```

```csharp
namespace Billing.Endpoints.Invoices.Get;

public sealed record GetInvoice(string InvoiceId);

[Get("invoices/{invoiceId}")]
public sealed class Endpoint(IInvoiceStore store) : ApiEndpoint<GetInvoice, InvoiceView>
{
    public override async Task<InvoiceView> HandleAsync(GetInvoice request, CancellationToken ct) =>
        InvoiceView.From(await store.GetAsync(request.InvoiceId, ct));
}
```

## Pages

- **[[Getting-Started]]** — install, wire up, write the first endpoint.
- **[[Endpoint-Classes]]** — the five base types, `Configure`, and conventions.
- **[[Binding]]** — precedence, supported types, body modes, and failures.
- **[[Problem-Details]]** — turning exceptions into responses.
- **[[Unload-Safety]]** — what it means, how it is enforced, how to verify it.
- **[[Migrating-from-FastEndpoints]]** — a shape-by-shape mapping.

## Design positions

Three choices explain most of the library.

**Nothing is process-global.** No static registry, no discovery you did not ask for. A group scans
only the assembly you hand it, inside your own mapping call, and keeps nothing afterwards. This is
what makes endpoint assemblies collectible, and it is enforced by tests rather than by intent.

**Every escape hatch is ASP.NET Core's.** Each mapping call returns an `IEndpointConventionBuilder`.
There is no parallel result type, no parallel validation stack, and no custom container. If you know
how to do something on a `MapPost`, you already know how to do it here.

**The narrow parts are narrow on purpose.** The binder covers a small, predictable set of shapes and
throws loudly on anything else rather than binding silently to a default. Widening it is a
deliberate act, not an accident.

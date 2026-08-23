# Getting Started

## Install

```bash
dotnet add package NativeEndpoints
```

Targets `net10.0`. The package takes a framework reference and no `PackageReference`, so it adds
nothing to your dependency graph.

## Wire it up

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNativeEndpoints();

var app = builder.Build();
app.MapEndpointGroup().MapEndpointsFrom(typeof(Program).Assembly, routePrefix: "/api");
app.Run();
```

`AddNativeEndpoints` registers the default problem writer, so a binding failure returns a sensible
body without further configuration.

`MapEndpointGroup` opens a group. Every argument is optional:

| Argument | Default | Purpose |
|---|---|---|
| `name` | The calling assembly's simple name | Prefixes endpoint names, supplies the default OpenAPI tag, identifies endpoints in lifetime violation reports |
| `jsonContext` | The host's configured `JsonOptions` | A source-generated serializer context governing both binding and writing |
| `jsonContentType` | `application/json; charset=utf-8` | The exact Content-Type written on success |

## Write an endpoint

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

Constructor injection is ordinary constructor injection: the endpoint is built per request from the
request services, so scoped dependencies work as they do anywhere else.

## Where the operation id comes from

The operation identifier becomes both the OpenAPI `operationId` and the ASP.NET Core endpoint name,
so it is wire-visible. It is derived from where the class lives:

| Class | Operation | Endpoint name |
|---|---|---|
| `Billing.Endpoints.Invoices.Get.Endpoint` | `InvoicesGet` | `Billing_InvoicesGet` |
| `Billing.Endpoints.Invoices.Create.Endpoint` | `InvoicesCreate` | `Billing_InvoicesCreate` |
| `Billing.Flat.PostLedgerEntry` | `PostLedgerEntry` | `Billing_PostLedgerEntry` |

Segments after an `Endpoints` namespace segment are concatenated. Without such a segment the class
name is used. This is why the convention puts one operation per folder, and why the class can simply
be called `Endpoint`.

Pin a published identifier explicitly when you need to:

```csharp
public override void Configure(ApiEndpointOptions options) => options.Operation = "InvoicesFetch";
```

## Route prefixes

Attributes stay literal. The prefix is applied once, where the group is mapped:

```csharp
app.MapEndpointGroup().MapEndpointsFrom(assembly, routePrefix: "/api/billing");
// [Get("invoices/{invoiceId}")] serves /api/billing/invoices/{invoiceId}
```

## Mapping a single endpoint

```csharp
var group = app.MapEndpointGroup("Billing");
group.MapEndpoint<Billing.Endpoints.Invoices.Get.Endpoint>(routePrefix: "/api");
```

## Several groups in one host

Give each its own name. Names keep endpoint identifiers unique and tag the OpenAPI document:

```csharp
app.MapEndpointGroup("Billing").MapEndpointsFrom(typeof(BillingMarker).Assembly, "/api/billing");
app.MapEndpointGroup("Shipping").MapEndpointsFrom(typeof(ShippingMarker).Assembly, "/api/shipping");
```

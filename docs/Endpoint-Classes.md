# Endpoint Classes

## The five base types

| Base | Handler signature | Response |
|---|---|---|
| `ApiEndpoint<TRequest, TResponse>` | `Task<TResponse> HandleAsync(TRequest, CancellationToken)` | JSON body, `SuccessStatus` |
| `ApiEndpoint<TRequest>` | `Task HandleAsync(TRequest, CancellationToken)` | `204 No Content` |
| `ApiEndpointWithoutRequest<TResponse>` | `Task<TResponse> HandleAsync(CancellationToken)` | JSON body, no bound contract |
| `ApiEndpointWithResult<TRequest, TResponse>` | `Task<EndpointResult<TResponse>> HandleAsync(TRequest, CancellationToken)` | JSON body, status from the result |
| `ApiEndpointBase` | none | You write the response yourself |

### Status decided by the handler

```csharp
public override async Task<EndpointResult<InvoiceView>> HandleAsync(CreateInvoice cmd, CancellationToken ct)
{
    var (invoice, created) = await store.UpsertAsync(cmd, ct);
    return created
        ? EndpointResult.Status(StatusCodes.Status201Created, InvoiceView.From(invoice))
        : EndpointResult.Ok(InvoiceView.From(invoice));
}
```

The documented schema stays `InvoiceView`. The wrapper never reaches the wire.

### Writing the response yourself

Derive from `ApiEndpointBase` and use `HttpContext` directly. Reach for this when the response is not
JSON at all: a file stream, an event stream, a redirect.

## Route attributes

`[Get]`, `[Post]`, `[Put]`, `[Patch]`, and `[Delete]` all derive from `EndpointRouteAttribute`. The
route is literal and relative to whatever prefix the group was mapped with.

```csharp
[Post("invoices/{invoiceId}/lines")]
```

## Configure

Most endpoints need no `Configure` override at all: the attribute carries the route and the namespace
carries the operation. Override it to refine anything else.

```csharp
public override void Configure(ApiEndpointOptions options)
{
    options.BodyMode = EndpointBodyMode.RequiredWithContentType;
    options.Accepts = ["application/json"];
    options.SuccessStatus = StatusCodes.Status202Accepted;
}
```

| Option | Meaning |
|---|---|
| `Method`, `Route` | Set by the route attribute; override to compute a route |
| `Operation` | Pins the operation identifier, overriding derivation |
| `Accepts` | Content types the request is accepted as; also decides whether a request schema is documented |
| `BodyMode` | How the request body is treated. See [[Binding]] |
| `SuccessStatus` | The status written at runtime on success |
| `DocumentedStatus` | The status the document declares, when it deliberately differs from the runtime one |
| `DocumentAuthResponses` | Forces the documented 401/403 pair on or off |
| `Convention(...)` | Registers an ordinary ASP.NET Core convention |

> **`Configure` runs on an uninitialized instance.** It is invoked once at map time, before any
> constructor runs, so constructor-injected fields are null inside it. Read only the `options`
> argument. A planned analyzer will make touching instance state a build error.

## Reaching ASP.NET Core

`options.Convention` hands you the standard builder:

```csharp
public override void Configure(ApiEndpointOptions options)
{
    options.Convention(b => b.RequireRateLimiting("invoices").CacheOutput().WithSummary("Fetch an invoice"));
}
```

## Documented versus runtime status

An endpoint can return one status and document another. This exists for published contracts that
declared a status the implementation does not actually produce, where changing the document would
break clients:

```csharp
options.SuccessStatus = StatusCodes.Status201Created;   // what callers receive
options.DocumentedStatus = StatusCodes.Status200OK;     // what the document says
```

## Authorization

Authorization is applied with ordinary ASP.NET Core conventions:

```csharp
options.Convention(b => b.RequireAuthorization("invoices:read"));
```

Implement `IEndpointConventionAttribute` to contribute authorization as a class-level attribute
without the framework knowing anything about your policy model:

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequirePermissionAttribute(string permission) : Attribute, IEndpointConventionAttribute
{
    public void Apply(IEndpointConventionBuilder builder) => builder.RequireAuthorization(permission);
}
```

```csharp
[Get("invoices/{invoiceId}")]
[RequirePermission("invoices:read")]
public sealed class Endpoint : ApiEndpoint<GetInvoice, InvoiceView> { }
```

The 401 and 403 responses are then documented automatically, because the completed metadata carries
authorization. Endpoints marked `AllowAnonymous` do not document them.

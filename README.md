<p align="center">
  <img src="branding/banner.png" alt="NativeEndpoints — structured endpoints, native ASP.NET Core" width="100%">
</p>

# NativeEndpoints

**A structured programming model for ASP.NET Core Minimal APIs.**

Build vertical-slice APIs without leaving Minimal APIs. One class per endpoint, carrying its route,
its metadata, and its handling. Ordinary ASP.NET Core underneath, all the way down.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)

> **Status: preview.** `1.0.0-preview.1` is on nuget.org. The API is settling but no longer moving
> weekly; breaking changes before 1.0 are possible and are listed in the [changelog](CHANGELOG.md).

---

## An endpoint

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

That is the whole file. The attribute declares the route. The namespace supplies the operation id
(`InvoicesGet`), so nothing has to be named twice. Constructor injection is ordinary constructor
injection. The request record is bound from the route, and the response is serialized and documented.

Wire it up once:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNativeEndpoints();

var app = builder.Build();
app.MapEndpointGroup().MapEndpointsFrom(typeof(Program).Assembly, routePrefix: "/api");
app.Run();
```

```bash
dotnet add package NativeEndpoints
```

Generating an OpenAPI document? Add the integration too, so the route, query, and header parameters
your endpoints bind appear in it:

```bash
dotnet add package NativeEndpoints.OpenApi
```

```csharp
builder.Services.AddOpenApi();
builder.Services.AddNativeEndpointsOpenApi();
```

## Why

Minimal APIs are a good runtime and an awkward organizing principle. Past a few dozen routes you are
choosing between a `Program.cs` nobody wants to open, a pile of extension methods that hide the route
table, or a framework that replaces ASP.NET Core with its own parallel universe.

NativeEndpoints takes the middle path. It gives you a place to put an endpoint and takes nothing away.

**Vertical slices, not layers.** An operation is one folder: its contract, its handler, its
permissions, its tests. Changing an endpoint means opening one directory, not tracing a request
through a controller, a service, and a handler registry.

**Ordinary ASP.NET Core underneath.** Routing, serialization, filters, results, CORS, rate limiting,
output caching, authorization, and OpenAPI are all still ASP.NET Core's. There is no parallel result
type, no parallel validation stack, no custom container. Every escape hatch is an
`IEndpointConventionBuilder` you already know how to use:

```csharp
public override void Configure(ApiEndpointOptions options)
{
    options.Convention(b => b.RequireRateLimiting("invoices").CacheOutput());
}
```

**Nothing is prescribed about how you handle a request.** `HandleAsync` is a method. Call a service,
query a store, dispatch to whatever you already use. The framework owns the route, the binding, and
the metadata, and stops there.

**Metadata that is correct by default.** API Explorer needs a `MethodInfo` in endpoint metadata to
produce an `ApiDescription`; without one your endpoint silently vanishes from the OpenAPI document
and any test that inspects the document passes vacuously. NativeEndpoints handles that once, for
every endpoint. So is the split between the status you return and the status you document, and the
`401`/`403` pair, which is documented only where authorization metadata is actually present rather
than stamped onto public endpoints that can never return it.

**And it unloads.** See below. If you host plugins, this is the reason to be here.

## Base types

| Type | For |
|---|---|
| `ApiEndpoint<TRequest, TResponse>` | A request in, a response body out |
| `ApiEndpoint<TRequest>` | A request in, `204 No Content` out |
| `ApiEndpointWithoutRequest<TResponse>` | No contract, a response body out |
| `ApiEndpointWithResult<TRequest, TResponse>` | The status code is decided by the handler |
| `ApiEndpoint` | Write the response yourself |

`ApiEndpointWithResult` covers operations whose status depends on what happened:

```csharp
public override async Task<EndpointResult<InvoiceView>> HandleAsync(CreateInvoice cmd, CancellationToken ct)
{
    var (invoice, created) = await store.UpsertAsync(cmd, ct);
    return created
        ? EndpointResult.Status(StatusCodes.Status201Created, InvoiceView.From(invoice))
        : EndpointResult.Ok(InvoiceView.From(invoice));
}
```

The documented schema stays `InvoiceView`; the wrapper never reaches the wire.

## Binding

Precedence is **route, then body, then query**. Route wins over the body so a resource identifier in
the URL cannot be contradicted by the payload.

Built in: `string`, `bool`, `int`, `long`, `Guid`, `enum`, `DateTimeOffset`, anything implementing
`IParsable<T>`, and arrays or lists of those from repeated query keys. Headers and claims bind on
request with `[FromHeader]` and `[FromClaim]`, never implicitly.

Anything else throws, loudly, rather than binding silently to a default. The source generator ships in the package and reports it at build time instead:

```
NE0002: Contract 'Transfer' has parameter 'amount' of unsupported type 'Money'.
        Implement IParsable<Money>, or register a parser with
        AddNativeEndpoints(o => o.ValueBinders.Add<Money>(...)).
```

Register a parser for your own types:

```csharp
builder.Services.AddNativeEndpoints(o => o.ValueBinders.Add<Money>(Money.TryParse));
```

Body handling is explicit per endpoint via `options.BodyMode`: `None`, `Optional`, `Required`, or
`RequiredWithContentType`, the last rejecting a non-JSON content type with a bare `415` before the
body is read.

A typed value the caller sent but the binder cannot read falls back to the parameter's default,
which is what most published contracts already do. Opt in to `options.StrictTypedParsing` per
endpoint to report it as a `400` naming the value instead — the better setting for a new endpoint.

This binder is deliberately narrower than `RequestDelegateFactory`'s. That is a design position, not
a gap: predictable binding you can hold in your head, and a loud failure instead of a quiet one.

## Errors

Domain exceptions become responses through translators you register. The mapping is domain
knowledge, so it lives with the code that owns the exception rather than in a global filter every
part of the application has to agree on.

```csharp
public sealed class BillingExceptionTranslator : IEndpointExceptionTranslator
{
    public EndpointProblem? Translate(Exception exception) => exception switch
    {
        InvoiceNotFoundException  => EndpointProblem.General(404, "Invoice not found"),
        InvoiceLockedException e  => EndpointProblem.General(409, e.Message),
        _ => null
    };
}
```

Out of the box, problems are written as RFC 9457 `ProblemDetails` through ASP.NET Core's
`IProblemDetailsService`. Implement `IEndpointProblemWriter` to control the wire shape, or
`IEndpointFaultRenderer` for failure contracts that carry structured payloads and need to write the
whole response themselves.

## Unload safety

If you host plugins in collectible `AssemblyLoadContext`s, endpoint frameworks are usually where
unloading goes to die. Process-global registries, static configuration, and handler `MethodInfo`
captured into endpoint metadata all root the assembly you are trying to release, and none of it is
visible until you measure.

NativeEndpoints is built so that does not happen:

- No process-global discovery and no static registry. Registration is generated per assembly; the
  reflective fallback scans only the assembly you hand it, inside your own mapping call.
- No framework static holds a reference to your types. Caches are weak-keyed or scoped to the
  endpoint generation.
- Handlers publish as bare `RequestDelegate`, so `RequestDelegateFactory` never puts your handler's
  `MethodInfo` or its async state machine into metadata that API Explorer retains for host lifetime.
- Endpoint metadata is validated as the final convention, fail-closed, and rejects any collectible
  type, member, delegate, serializer context, or `JsonTypeInfo`.

Do not take our word for it. `NativeEndpoints.Testing` compiles a synthetic endpoint assembly, loads
it collectibly, maps it, serves it, disposes the host, unloads, and reports which stage still roots
the context:

```csharp
[Fact]
public void Endpoint_assemblies_are_collected()
{
    var evidence = CollectibleEndpointFixture.RunCycles(cycles: 3);
    UnloadEvidence.AssertAllCollected(evidence);
}
```

```bash
dotnet add package NativeEndpoints.Testing
```

The kit has no dependency on NativeEndpoints itself. What the harness measures today is its own
synthetic endpoint assembly, which is what proves the pattern rather than the library's marketing;
it can also be asked to introduce a deliberate leak, so you can confirm it still detects one.
Measuring *your* host means the shape in [`samples/PluginHost`](samples/PluginHost), where a real
plugin is loaded, served, unloaded, and counted. A hook for driving another framework's registration
inside the harness is planned, not shipped.

## Compared to FastEndpoints

FastEndpoints is a mature, popular, and genuinely good library, and it does considerably more than
this one: validation, versioning, job queues, response caching integration, and a large testing
surface. If you want a batteries-included framework, use it.

Choose NativeEndpoints when you want the endpoint-class shape and nothing else.

| | NativeEndpoints | FastEndpoints |
|---|---|---|
| Endpoint classes | Yes | Yes |
| Underlying stack | Minimal APIs, unmodified | Its own layer over Minimal APIs |
| Escape hatch | `IEndpointConventionBuilder` | Framework-specific |
| Registration | Generated, or explicit local scan | Process-global discovery |
| Collectible unloading | Verified by a test you can run | Not supported |
| Binding sources | Route, body, query, header, claim | Route, query, claim, form, body, header |
| Forms and file upload | Not supported | Supported |
| Validation | Bring your own | FluentValidation, built in |
| Package dependencies | None | Several |
| Target frameworks | `net10.0` | Broad |
| License | MIT | Apache 2.0 |

On unloading specifically: in a harness that compiled three endpoint assemblies, loaded each into
its own collectible context, served a request, disposed the host, unloaded, and forced repeated full
collections, **0 of 3 contexts were collected** with FastEndpoints 7.2.0, and registrations
accumulated across disposed hosts. That result isolates a composition-level retention problem; it
does not attribute it to a specific static root, and it is not a claim about FastEndpoints in any
other respect. It is the reason this library exists.

On speed: [`benchmarks/`](benchmarks) holds a BenchmarkDotNet suite comparing a raw minimal API,
NativeEndpoints through both mapping paths, and FastEndpoints on the same two operations, in
process. Run it yourself rather than trusting a table; the in-repo runs show the generated path at
parity with a hand-written minimal API on both time and allocations.

## What it does not do

- **Forms, multipart, and file upload.** Not in 1.0. Use a plain `MapPost` beside your endpoints.
- **Validation.** Bring FluentValidation, `DataAnnotations`, or hand-written guards.
- **Older target frameworks.** `net10.0` only. .NET 8 leaves support in November 2026 and a new
  library targeting it would ship dead code.

Native AOT **is** supported, through the source generator. See
[`samples/Aot`](samples/Aot) and the [source generator](docs/Source-Generator.md) page.

## Documentation

Full documentation lives in the [wiki](https://github.com/valence-works/NativeEndpoints/wiki),
published from [`docs/`](docs) on every push to `main`.

[Getting started](docs/Getting-Started.md) &middot;
[Source generator](docs/Source-Generator.md) &middot;
[Endpoint classes](docs/Endpoint-Classes.md) &middot;
[Binding](docs/Binding.md) &middot;
[Problem details](docs/Problem-Details.md) &middot;
[Unload safety](docs/Unload-Safety.md) &middot;
[Migrating from FastEndpoints](docs/Migrating-from-FastEndpoints.md)

## Contributing

Issues and pull requests are welcome. The library is deliberately narrow, so proposals that widen
its surface should say what they make possible that is not possible today, and what they cost the
people who will never use them.

## License

MIT. See [LICENSE](LICENSE).

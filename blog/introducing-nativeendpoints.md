# Introducing NativeEndpoints

For the past few weeks I've been pulling a small library out of a much larger codebase, and it's now
public. **NativeEndpoints** is a structured programming model for ASP.NET Core Minimal APIs: one
class per endpoint, carrying its route, its metadata, and its handling, with ordinary ASP.NET Core
underneath all the way down. Build vertical-slice APIs without leaving Minimal APIs.

The problem it exists for is one I suspect a lot of people have run into. Minimal APIs are a good
runtime and an awkward organizing principle. They're fast, they compose, and the metadata model is
genuinely nice once you know it. But past a few dozen routes you're choosing between a `Program.cs`
nobody wants to open, a pile of extension methods that hide the route table from you, or a framework
that replaces ASP.NET Core with its own parallel universe. I wanted the middle path: a place to put
an endpoint, with nothing taken away.

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

That's the whole file. The attribute declares the route. The namespace supplies the operation id
(`InvoicesGet`), so nothing has to be named twice, and most endpoints need no `Configure` override at
all. Constructor injection is ordinary constructor injection: the endpoint is built per request from
the request services, so scoped dependencies behave the way they do anywhere else. The request record
is bound from the route, and the response is serialized and documented.

Wiring it up is four lines:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNativeEndpoints();

var app = builder.Build();
app.MapEndpointGroup().MapEndpointsFrom(typeof(Program).Assembly, routePrefix: "/api");
app.Run();
```

What you get out of that is vertical slices rather than layers. An operation is one folder: its
contract, its handler, its permissions, its tests. Changing an endpoint means opening one directory
instead of tracing a request through a controller, a service, and a handler registry.

## Ordinary ASP.NET Core underneath

Routing, serialization, filters, results, CORS, rate limiting, output caching, authorization, and
OpenAPI are all still ASP.NET Core's. There's no parallel result type, no parallel validation stack,
and no custom container. Every escape hatch is an `IEndpointConventionBuilder` you already know how
to use:

```csharp
public override void Configure(ApiEndpointOptions options)
{
    options.Convention(b => b.RequireRateLimiting("invoices").CacheOutput());
}
```

Nothing is prescribed about how you handle a request, either. `HandleAsync` is a method. Call a
service, query a store, dispatch to whatever you already use. The framework owns the route, the
binding, and the metadata, and stops there.

One detail I think is worth calling out: the core package has zero NuGet dependencies. It takes
`FrameworkReference Microsoft.AspNetCore.App` and nothing else, which is unusual for something in
this space, and it means the library never drags an OpenAPI package version into your graph for you
to reconcile. It writes ordinary endpoint metadata, and whatever OpenAPI generator you use reads it.

## Where this came from

Now for the part that explains why the library looks the way it does.

This started as a replacement for FastEndpoints in a large modular .NET codebase. The modules there
ship as assemblies that are supposed to be dynamically unloadable, which means every module gets its
own collectible `AssemblyLoadContext`, and anything holding on to a module's types has to let go of
them when the module goes away. Endpoint frameworks are a good place for that to go wrong, because
registrations, static configuration, and endpoint metadata all end up referencing your types.

So we measured. The harness compiled three endpoint assemblies, loaded each one into its own
collectible context, mapped and served a request, disposed the host, unloaded, and forced repeated
full collections. With FastEndpoints 7.2.0, **0 of 3 contexts were collected**, and registrations
accumulated across disposed hosts.

I want to be careful about what that result says. It isolates a composition-level retention problem,
and that's all. It doesn't identify a specific static root, and it isn't a claim about FastEndpoints
in any other respect. If you're not hosting plugins in collectible contexts, it has no bearing on
you whatsoever.

And before this reads as point scoring: my own binder had the same class of bug. A
`static ConcurrentDictionary<Type, ConstructorInfo>` cache, living in the framework assembly and
keyed by contract type, which cheerfully rooted every contract type it had ever bound for the
lifetime of the host. It's a `ConditionalWeakTable` now. That kind of retention is invisible in code
review. It only shows up when you go looking for it.

## Unload safety

So the design takes it seriously. There's no process-global discovery and no static registry:
registration is generated per assembly, and the reflective fallback scans only the assembly you hand
it, inside your own mapping call. No framework static holds a reference to your types, because the
caches are either weak-keyed or scoped to the endpoint generation. Handlers publish as a bare
`RequestDelegate`, since a typed lambda would make `RequestDelegateFactory` put the handler's
`MethodInfo` and its async state machine into metadata that API Explorer then retains for the
lifetime of the host. And completed endpoint metadata is validated as the final convention,
fail-closed, rejecting any collectible type, member, delegate, serializer context, or `JsonTypeInfo`.

You don't have to take my word for any of that. `NativeEndpoints.Testing` compiles a synthetic
endpoint assembly, loads it collectibly, maps it, serves it, disposes the host, unloads, and reports
which stage still roots the context:

```csharp
[Fact]
public async Task Module_unloads()
{
    var evidence = await CollectibleEndpointFixture.RunCyclesAsync(cycles: 3);
    UnloadEvidence.Verify(evidence, gcRounds: 32);
}
```

Here's the honest limit. The guarantee is proven by tests and enforced at map time, and neither of
those is production evidence. In most hosts nothing ever creates a collectible load context, so
nothing is ever unloaded, and none of this machinery does anything for you. What the library gives
you is that when you do create one, the endpoint layer isn't what keeps it alive.

The harness has no dependency on NativeEndpoints, which I think makes it the most useful thing in the
repo for people who aren't going to adopt the library. Point it at whatever you use today and see
what you get. If your assemblies already collect, then unload safety is no reason to move, and you've
saved yourself a migration.

## About FastEndpoints

FastEndpoints has somewhere around 16.3 million NuGet downloads, and it deserves them. It's mature,
it's popular, it's genuinely good, and it does considerably more than this library does: validation,
versioning, job queues, response caching integration, and a large testing surface. If you want a
batteries-included framework, use it. I mean that plainly rather than as a courtesy.

Choose NativeEndpoints when you want the endpoint-class shape and nothing else.

| | NativeEndpoints | FastEndpoints |
|---|---|---|
| Underlying stack | Minimal APIs, unmodified | Its own layer over Minimal APIs |
| Escape hatch | `IEndpointConventionBuilder` | Framework-specific |
| Registration | Generated, or explicit local scan | Process-global discovery |
| Collectible unloading | Verified by a test you can run | Not supported |
| Binding sources | Route, body, query | Route, query, claim, form, body, header |
| Validation | Bring your own | FluentValidation, built in |
| Package dependencies | None | Several |
| Target frameworks | `net10.0` | Broad |

## What isn't there

Plenty, and some of it permanently.

Binding today covers route, body, and query, over seven scalar types (`string`, `bool`, `int`,
`long`, `Guid`, `enum`, and `DateTimeOffset`) plus their nullable forms. Headers, claims, query
collections, and `IParsable<T>` are planned but not shipped, so check your contracts before you
commit to anything. Forms, multipart, and file upload are deliberately out of scope for 1.0. Use a
plain `MapPost` beside your endpoints for those.

There's no built-in validation and no mediator, both by design. Validation is FluentValidation,
`DataAnnotations`, or hand-written guards, whichever you already have. A mediator bridge over the
public mapping seam is about forty lines, and I'd rather document it than own compatibility with a
mediator landscape that's currently anything but settled.

The source generator is designed but not implemented. When it lands it brings AOT and trimming
support, generated registration with no scan at all, and the thing I most want out of it: unsupported
binder types becoming build errors instead of exceptions on the first request that hits that route.
Until then, the reflective path is what you're using.

And the API will move before 1.0. Anything you pin today is a prerelease, and I'd rather be upfront
about that than have someone find out during an upgrade.

## Where to find it

The repo is at [valence-works/NativeEndpoints](https://github.com/valence-works/NativeEndpoints),
MIT licensed, targeting `net10.0`. There are two packages, `NativeEndpoints` and
`NativeEndpoints.Testing`, published as prereleases to GitHub Packages. nuget.org isn't open yet;
that happens once the documentation is complete and the API has held still across two consecutive
milestones. Full documentation lives in the
[wiki](https://github.com/valence-works/NativeEndpoints/wiki).

So there you have it. The library is deliberately narrow, so if you try it and something's missing,
I'd like to hear which thing and what you were trying to do, especially where the binder gets in your
way. That's the part I most expect to be wrong about.

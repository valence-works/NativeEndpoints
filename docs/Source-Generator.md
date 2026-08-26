# Source Generator

Ships inside the `NativeEndpoints` package as an analyzer, so `dotnet add package NativeEndpoints`
brings it along. Nothing to configure.

## What it generates

An explicit registration naming every endpoint class in your assembly:

```csharp
// generated
public static class BillingEndpoints
{
    public static EndpointGroup Map(this EndpointGroup group, string? routePrefix = null)
    {
        group.MapEndpoint<Billing.Endpoints.Invoices.Get.Endpoint>(routePrefix);
        group.MapEndpoint<Billing.Endpoints.Invoices.Create.Endpoint>(routePrefix);
        // ...
        return group;
    }
}
```

Call it instead of the reflective scan:

```csharp
using NativeEndpoints.Generated;

app.MapEndpointGroup().Map(routePrefix: "/api");
```

The class is named after your assembly, so two assemblies in one host do not collide.

Both paths produce identical endpoints, which the test suite pins by mapping the same assembly each
way and comparing. The reflective `MapEndpointsFrom` is not going away: it is the documented fallback
for anyone who cannot run the generator.

## What it tells you at build time

| Rule | Meaning |
|---|---|
| `NE0001` | Endpoint declares no route attribute, and the generator cannot see whether `Configure` supplies one |
| `NE0002` | A contract parameter has a type the binder cannot produce from a request string |
| `NE0003` | `Configure` reads constructor-injected state, which is null at map time |
| `NE0004` | A contract has more than one public constructor, so the binder will throw when the route is first called |
| `NE0005` | Endpoint derives `ApiEndpointBase` directly, which no mapper can dispatch; derive the non-generic `ApiEndpoint` or one of the four contract shapes |

`NE0002` is the one that earns the generator its place. Without it, a contract parameter the binder
cannot convert throws on the first request that reaches the route, in whichever environment reaches
it first. With it, the build says so.

It is reported only for `GET` and `HEAD`. Everywhere else a contract member may come from the JSON
body, where any serializable type is fine, and `Configure` can change the body mode in ways the
generator cannot see. Reporting there would be noise.

## Registered value binders

A value binder is registered at runtime, and no analyzer can see that. Tell the build:

```csharp
[assembly: EndpointValueBinder(typeof(Money))]
```

```csharp
builder.Services.AddNativeEndpoints(o => o.ValueBinders.Add<Money>(Money.TryParse));
```

The attribute carries no runtime behavior. It exists so `NE0002` stays quiet for a type you have
deliberately taught the binder about. Without it the warning is a false positive, and in a project
treating warnings as errors it is a build break for correct code.

Types implementing `IParsable<T>` need no declaration; the generator sees the interface.

## Inside this repository

Analyzers flow to consumers through a package's `analyzers/` folder, not through a `ProjectReference`
chain. Projects in this repository that want the generator reference it directly:

```xml
<ProjectReference Include="../../src/NativeEndpoints.Generator/NativeEndpoints.Generator.csproj"
                  ReferenceOutputAssembly="false"
                  OutputItemType="Analyzer" />
```

## What else it generates

A **binder** per contract, reading each member by name with no reflection, and an **activator** that
news the endpoint up directly from request services. Together with generated registration that
removes every reflection path from the request flow.

```csharp
// generated
return new(new GetWidget(
    EndpointValue.Guid(EndpointValue.Route(context, "widgetId")),
    body is not null ? body.Search : EndpointValue.String(EndpointValue.Query(context, "Search")),
    EndpointValue.Array<int>(EndpointValue.QueryValues(context, "Tag"),
        static raw => EndpointValue.Int32(raw)!)
), null, null);
```

Body reading is *not* generated: it calls the same `EndpointRequestBinder.ReadBodyAsync` the
reflective binder uses, so the media-type rules have one implementation and cannot drift.

An endpoint whose shape is not statically resolvable falls back to reflective mapping, and the
generated file says which ones and why. That covers, besides a contract with more than one public
constructor:

- **A property-bound contract** — a parameterless constructor with settable properties. The emitted
  construction would discard the deserialized body; the reflective binder keeps the body and lays
  route, query, and declared sources over it.
- **A contract with a constructor-parameter default** (`int Page = 3`). Defaults are compile-time
  constants the emitter would have to re-literalize correctly for every supported type; the
  reflective binder reads them at bind time and honors them today.

The fallback registers the same endpoint through `MapEndpoint<T>`, so nothing disappears — a slower
correct path, not a missing one.

## Native AOT

**Supported.** [`samples/Aot`](../samples/Aot) publishes as an 11 MB native binary with zero IL trim
or AOT warnings, and CI republishes it on every push with those warnings escalated to errors.

Three things are required, and the build tells you if any is missing:

1. **Call the generated `Map()`**, not `MapEndpointsFrom`. The reflective mapper is annotated
   `RequiresUnreferencedCode` and `RequiresDynamicCode`, so using it in a trimmed or AOT project
   produces `IL2026` and `IL3050` at the call site rather than a failure after deployment.
2. **Pass a `JsonSerializerContext`** to `MapEndpointGroup`. Without one the group falls back to the
   host's options and JSON goes through runtime reflection.
3. **Use bindable contract types.** `IParsable<T>` compiles to a constrained call on a static
   abstract interface member, which is fully AOT-safe. This is why it is the supported way to add a
   type.

A `JsonSerializerContext` applies no naming policy unless given one, while the fallback uses
`JsonSerializerOptions.Web`, which is camelCase. Adopting a context can therefore change your JSON
casing, which is wire-visible; set `JsonSourceGenerationOptions` deliberately.

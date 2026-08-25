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

## Native AOT

Generated registration removes the assembly scan and the `MakeGenericMethod` call, which are two of
the reflection paths that block trimming. Activation and binding still use reflection, so **AOT is
not yet supported**. Generating those is the remaining work, and the API is shaped for it: the
registration seam this generator already emits is where a generated binder and activator will attach.

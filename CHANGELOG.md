# Changelog

## 1.0.0-preview.2

Two features the first real consumer needed, found by starting the migration rather than by guessing.

**Strict typed parsing.** `options.StrictTypedParsing` rejects a route or query value that does not
parse, with a 400 naming it, instead of falling back to the parameter's default. Opt-in, because
turning it on changes what an existing API returns. It also closes a hole in this library's own
argument: the binder promised nothing misbinds silently, and then silently defaulted an unreadable
number.

**Explicit nulls are distinguishable from absent values.** The binder records which properties a
request body actually contained, so a member sent as `null` stays null while an omitted one falls
through to the query string. Previously both looked the same.

**The write-your-own-response shape actually exists.** The docs promised five base types; the fifth
was documented as deriving `ApiEndpointBase` directly, which the reflective scan rejected at startup
and the generator silently omitted. The shape is now the non-generic `ApiEndpoint`:
`Task HandleAsync(CancellationToken)`, with the handler writing the response through `HttpContext`
itself — nothing is bound, nothing is written on success, no JSON response body is documented, and
the shared failure path (fault renderers, translators, sanitized 500) still applies. Both mapping
paths support it, via the new public `EndpointGroup.MapRaw`. A class that still derives
`ApiEndpointBase` directly gets diagnostic `NE0005` at build time, and the reflective scan's error
now names the offending type and the five supported bases instead of failing opaquely.

### Breaking

- `EndpointGroup.MapOperation<TMessage>` takes an `EndpointOperationDescriptor` record rather than
  thirteen positional parameters; the old overload is gone. Every typed Map method now builds its
  descriptor from `ApiEndpointOptions` in one place, so a new option can no longer be silently
  dropped by a forwarding overload — which is exactly how `StrictTypedParsing` failed to apply in
  preview.1. `MapHandler` and the generated `MapGenerated*` entry points are unchanged.
- `EndpointBinder<T>` and `EndpointRequestBinder.BindAsync` take an `EndpointBindingOptions` record
  rather than loose parameters. Binding has gained settings twice now; a record stops each addition
  being a signature break.
- `EndpointRequestBinder.ReadBodyAsync` returns `EndpointBodyResult<T>`, which carries the supplied
  property names alongside the body.
- Regenerate: binders emitted by preview.1 do not match the new delegate shape.

### Fixed

- A routed `ApiEndpointWithoutRequest<TResponse>` endpoint made the generator emit a registration
  that did not compile. The shape now has a first-class generated path through the new public
  `EndpointGroup.MapGeneratedUnbound`, producing the same endpoint as the reflective mapper.

## 1.0.0-preview.1

First public preview. The API is settling but no longer moving weekly; breaking changes before 1.0
are possible and will be listed here.

### The programming model

One class per endpoint, carrying its route, its metadata, and its handling. Five base types cover
request/response, no-content, no-request, handler-decided status, and write-your-own-response.
Operation identifiers derive from where the class lives, so most endpoints need no `Configure`
override at all.

Every mapping call returns an `IEndpointConventionBuilder`. There is no parallel result type, no
parallel validation stack, and no custom container.

### Binding

Route, then body, then query. Headers and claims bind on request through `[FromHeader]` and
`[FromClaim]`, never implicitly. Built-in types are `string`, `bool`, `int`, `long`, `Guid`, `enum`,
`DateTimeOffset`, anything implementing `IParsable<T>`, and arrays or lists of those from repeated
query keys. Register a parser for anything else. Everything unsupported throws loudly rather than
binding to a default.

Forms, multipart, and file upload are deliberately out of scope.

### Native AOT

Supported. The source generator ships inside the package and emits explicit registration, a binder
per contract, and an activator per endpoint, removing every reflection path from the request flow.
`samples/Aot` publishes as an 11 MB native binary with zero IL trim or AOT warnings, verified in CI
on every push.

Using the reflective mapper in a trimmed or AOT project reports `IL2026` and `IL3050` at the call
site rather than failing after deployment.

### Build-time diagnostics

- `NE0001` endpoint declares no route
- `NE0002` a contract parameter cannot be bound from a request string
- `NE0003` `Configure` reads constructor-injected state, which is null at map time
- `NE0004` a contract has more than one public constructor

### Unload safety

Endpoint assemblies in collectible load contexts are released. No process-global state, no static
registry, handlers published as bare `RequestDelegate`, and completed metadata validated as the final
convention, fail-closed. `NativeEndpoints.Testing` lets you assert it in your own suite;
`samples/PluginHost` demonstrates it in a real host across repeated load and unload cycles.

### Release

Published through NuGet Trusted Publishing: GitHub Actions requests a short-lived OIDC token, which
nuget.org exchanges for a temporary key valid for one hour. There is no long-lived publishing
credential in this repository or its organization secrets.

### Packages

| Package | Dependencies |
|---|---|
| `NativeEndpoints` | none, beyond the ASP.NET Core shared framework |
| `NativeEndpoints.OpenApi` | `NativeEndpoints`, `Microsoft.AspNetCore.OpenApi` |
| `NativeEndpoints.Testing` | `Microsoft.AspNetCore.TestHost`, `Microsoft.CodeAnalysis.CSharp` |

### Known gaps

- Route and query parameters appear in the OpenAPI document only with `NativeEndpoints.OpenApi`.
- The unload harness measures its own synthetic assembly; pointing it at another framework needs a
  registration hook that is not shipped.
- The compatibility manifest builder did not travel from the originating codebase; its ownership
  vocabulary needs an abstraction first.

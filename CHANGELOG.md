# Changelog

## 1.0.0-preview.3

A correctness release, driven by a full review of preview.2 rather than by new features.

**Strict typed parsing now actually applies.** Preview.2 introduced `options.StrictTypedParsing`,
but no mapping wrapper forwarded it, so the flag set in `Configure` never reached either binder —
and the conformance suite could not see it, because it asserted only that the two binders agree,
which they did, on being lenient. The flag is forwarded everywhere now, pinned by end-to-end tests
asserting outcomes (`?page=notanumber` under strict is a 400 naming `page`), and strict mode is one
rule across both binders: registered value binders, `IParsable<T>` fallbacks, and collection
elements reject unreadable values the same way a scalar `int` does, where each previously defaulted
silently on one path or the other. Strictness covers every source — route, query, header, and
claim. Absence keeps its documented meaning on both binders: a nullable reference-type
`IParsable<T>` member the caller omitted binds null even under strict parsing (the generated path
gains the dedicated `EndpointValue.ParsableOrDefault<T>` converter for it, instead of `Parsable<T>`
rejecting the absence), and a constructor-parameter default binds on absence whether or not the
member declares a `[From...]` source — `[FromQuery] int Page = 1` now agrees with `int Page = 1`,
lenient and strict alike. Contracts declaring constructor-parameter defaults are registered through
the reflective mapper by the generated `Map()`, which honors them, rather than being emitted with
the defaults silently dropped.

**The write-your-own-response shape actually exists.** The docs promised five base types; the fifth
was documented as deriving `ApiEndpointBase` directly, which the reflective scan rejected at startup
and the generator silently omitted. The shape is now the non-generic `ApiEndpoint`:
`Task HandleAsync(CancellationToken)`, with the handler writing the response through `HttpContext`
itself — nothing is bound, nothing is written on success, no JSON response body is documented, and
the shared failure path (fault renderers, translators, sanitized 500) still applies. Both mapping
paths support it, via the new public `EndpointGroup.MapRaw`. A class that still derives
`ApiEndpointBase` directly gets diagnostic `NE0005` at build time, and the reflective scan's error
now names the offending type and the five supported bases instead of failing opaquely.

### Performance

- The per-request hot path sheds work it can do once per endpoint instead, with no observable
  behaviour change — the conformance suite and the full test suite pin that responses are
  identical. Success responses resolve the response type's `JsonTypeInfo` once per endpoint and
  write through `WriteAsJsonAsync` rather than allocating a `Results.Json` result per response;
  the group's exact configured Content-Type and status semantics are preserved, and a null or
  runtime-divergent value keeps the original path. Route and query lookups use the collections'
  own case-insensitive `TryGetValue` instead of scanning every entry per parameter. The reflective
  binder memoizes a per-contract binding plan — constructor, parameter attributes, defaults, and
  property getters — in its existing weak-keyed cache instead of re-reflecting per request. When a
  contract proves no member can fall back from the body to the query (every member binds from a
  route value or a declared source), the body streams through the serializer in one pass instead
  of buffering a `JsonDocument` to record supplied properties: the new
  `EndpointRequestBinder.ReadBodyAsync` overload takes `needsSuppliedProperties`, and both binders
  pass it from what they know statically. Generated binders also hoist their per-element
  collection converters into per-endpoint delegates instead of allocating one per request.

### Breaking

- `EndpointGroup.MapOperation<TMessage>` takes an `EndpointOperationDescriptor` record rather than
  thirteen positional parameters; the old overload is gone. Every typed Map method now builds its
  descriptor from `ApiEndpointOptions` in one place, so a new option can no longer be silently
  dropped by a forwarding overload — which is exactly how `StrictTypedParsing` failed to apply in
  preview.2. `MapHandler` and the generated `MapGenerated*` entry points are unchanged.

### Changed

- A repeated query key bound to a scalar (`?page=1&page=2` into an `int`) binds the first value.
  Previously the values were comma-joined ("1,2"), which failed to parse and — under the lenient
  default — silently bound the type's zero, contradicting the promise that nothing misbinds
  silently; under strict parsing the join was rejected naming "1,2", a value the caller never
  sent. A single value binds exactly as before, and both binders agree — the conformance suite
  pins it. For comparison, minimal APIs comma-join here too, so a typed scalar answers a bare 400
  and a `string` binds "1,2" (verified against a TestHost app on .NET 10); that join is an
  accident of `StringValues.ToString()`, not behavior worth matching. Multi-valued headers keep
  the comma-join deliberately: HTTP defines a repeated field as one comma-separated field, so the
  join is the header's value.

### Fixed

- A routed `ApiEndpointWithoutRequest<TResponse>` endpoint made the generator emit a registration
  that did not compile. The shape now has a first-class generated path through the new public
  `EndpointGroup.MapGeneratedUnbound`, producing the same endpoint as the reflective mapper.
- A host that never called `AddNativeEndpoints()` now fails at `MapEndpointGroup` time with the
  remedy in the message, instead of surfacing on the first binding failure or handler exception at
  runtime — where the unresolvable `IEndpointProblemWriter` turned the caller's real 400 into an
  opaque 500. The check accepts either the unkeyed registration or a writer keyed by the group's
  own name — exactly the pair the failure path resolves per request — so a host composing only
  keyed per-group writers keeps mapping as it always did. The registration is probed through
  `IServiceProviderIsService` rather than resolved, so a scoped writer — a legitimate lifetime for
  a request-coupled writer — passes the check without being constructed from the root provider,
  which scope validation would rightly refuse; a container that cannot answer the probe skips the
  check rather than guessing.
- The generated binder for a property-bound contract — a parameterless constructor with settable
  properties — constructed an empty instance and silently discarded every deserialized body value,
  answering the type's defaults where the reflective binder answered the caller's payload. Such
  contracts are no longer generated: the generated `Map()` registers them through the reflective
  mapper, whose property-assignment path binds them correctly, and the conformance suite now posts
  a body through both mapping paths to pin it.
- A handler (or the serializer mid-write) that throws after the response has started streaming no
  longer triggers a secondary `InvalidOperationException` from the problem writer setting the
  status on a started response. The pipeline logs the original exception and aborts the connection
  — the same choice ASP.NET Core's exception middleware makes when it cannot re-execute — so the
  truncated response is not mistaken for a complete one. Fault renderers and exception translators
  are consulted only while the response has not started, since they write responses.
- `MapEndpointGroup` is marked `NoInlining` so the default group name, taken from
  `Assembly.GetCallingAssembly()`, cannot misreport the caller under JIT inlining.

## 1.0.0-preview.2

Two features the first real consumer needed, found by starting the migration rather than by guessing.

**Strict typed parsing.** `options.StrictTypedParsing` rejects a route or query value that does not
parse, with a 400 naming it, instead of falling back to the parameter's default. Opt-in, because
turning it on changes what an existing API returns. It also closes a hole in this library's own
argument: the binder promised nothing misbinds silently, and then silently defaulted an unreadable
number. *(Preview.3 note: the flag was not forwarded by the mapping wrappers in this release, so it
only took effect through a direct `MapOperation`/`BindAsync` call; setting it in `Configure` did
nothing until preview.3.)*

**Explicit nulls are distinguishable from absent values.** The binder records which properties a
request body actually contained, so a member sent as `null` stays null while an omitted one falls
through to the query string. Previously both looked the same.

### Breaking

- `EndpointBinder<T>` and `EndpointRequestBinder.BindAsync` take an `EndpointBindingOptions` record
  rather than loose parameters. Binding has gained settings twice now; a record stops each addition
  being a signature break.
- `EndpointRequestBinder.ReadBodyAsync` returns `EndpointBodyResult<T>`, which carries the supplied
  property names alongside the body.
- Regenerate: binders emitted by preview.1 do not match the new delegate shape.

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

# Changelog

## Unreleased

What a host needs to reproduce a document it did not originally generate, found by migrating a real
API with a frozen OpenAPI contract onto the library. Every addition is inert unless asked for, so an
existing host's document does not move.

**An operation can declare its own name.** `options.Name` — and `EndpointOperationDescriptor.Name` —
pins the endpoint name outright instead of deriving `{group}_{operation}`. This is for operation
identifiers published before a naming scheme existed and already frozen in documents clients
generate from: no `Operation` value produces `AspNetCoreIdentityLoginPage` under a
`{Owner}Endpoints{Operation}` rule, so deriving cannot be the only option. Deriving stays the
default and stays the better choice for anything new.

**A response-owning endpoint can describe the response it owns.** `options.SuccessContentType`
documents a body that is not JSON — a server-sent event stream, a rendered page — and
`options.ResponseType` documents the body's type, which `MapRaw` previously hard-coded to none.
Together with the `DocumentedStatus` fix below, this is what lets a raw endpoint be documented at
all: owning the response is not the same as having nothing to say about it. Both describe what the
handler writes rather than changing what it writes, so setting either moves the document and never
the response. An unset `ResponseType` documents no body, and an unset `SuccessContentType` documents
JSON once a body has been declared — so a raw endpoint that declares neither stays exactly as
undescribed as it was. `ResponseType` is consulted only by the raw shape: the typed base classes take
their response type from their own type argument, which is always more accurate than a restatement.

**A group's tag is separable from its name.** `MapEndpointGroup(name, tag: ...)` publishes
operations under a tag that is not the group name, and `EndpointOperationContext.Tag` carries it.
The two answer different questions — the name keeps endpoint identifiers unique across a host, the
tag groups operations for a reader — so several groups can share a tag while keeping distinct names.
Defaults to the group name, as before.

**A response-owning endpoint can decline failure containment.** `options.ContainFailures = false`
lets an unhandled exception propagate to the host's exception pipeline instead of being answered by
the group. Containment remains the default and remains right for almost everything; the opt-out is
for an owner whose published contract makes the host responsible for unexpected failures — one
already running its own exception middleware, or serving a UI whose error page is not a problem
document. Only the response-owning shape honours it: a bound operation always contains, because its
failure translation is what produces the documented status.

### Fixed

- **The unbound path dropped `DocumentedStatus` and `DocumentAuthResponses`.** `MapRaw`,
  `MapUnboundBody`, and `MapGeneratedUnbound` built their operation context without either field, so
  an explicit documented status was ignored and `DocumentAuthResponses` could not force the 401/403
  pair on or off — a `bool?` whose documented "forces the pair on or off" contract was unreachable on
  that path. The bound path forwarded both. The status drop was previously described as deliberate,
  reasoning that an operation with no request contract has no result-carried status to diverge from;
  that conflated `ApiEndpointWithResult`'s runtime status with an author's explicit declaration,
  which an unbound operation is as entitled to make as any other. No reason was ever stated for the
  auth-response drop.

  This is the second time a setting reached one mapping path and not another —
  `EndpointOperationDescriptor` was introduced in preview.3 precisely to stop it, and the unbound
  path was not converted with the rest. So the fix is structural rather than two added lines: every
  path now builds its context through one `Contextualize` method, the mirror of the single `Describe`
  method every path already builds its descriptor through. `DescriptorForwardingTests` asserts
  against the descriptor's own shape — a new field that reaches neither the context nor a documented
  exemption list fails the build, rather than waiting to be noticed by a consumer.
## 1.0.0-preview.4

Forms, in the shape the binder already had.

**Form bodies.** `options.BodyKind = EndpointBodyKind.Form` binds a contract from a
`multipart/form-data` or `application/x-www-form-urlencoded` body. A form is not a fifth binding
source: it *is* the body, so it occupies the body's place in the existing route → body → query
order, and an unattributed member on a form endpoint binds from a field without ceremony.
`[FromForm]` overrides that for one member, exactly as `[FromQuery]` does — for a renamed field, or
one a route value would otherwise shadow.

The form path needs no equivalent of the supplied-property set. JSON needs the payload read twice to
tell an explicit null from an omitted property; a form collection answers presence directly, and has
no null to distinguish. An empty field is the empty string, exactly as in the query string, so the
lenient and strict rules carry over unchanged — including preview.3's repeated-scalar rule, where a
repeated key reads its first value rather than the comma-join.

**File upload.** `IFormFile`, `IFormFile[]`, `List<IFormFile>`, and `IFormFileCollection` members
bind, on the generated path as well as the reflective one. A file takes none of the precedence chain
— it is not parsed from a string — and an absent file binds null rather than failing, so a
non-nullable member can still be null at runtime. Reading a form buffers it, so streaming multipart
stays out of scope. `samples/Aot` now carries a file-bearing form endpoint, so CI's native-AOT
publish proves the generated file path stays reflection-free.

**Form endpoints must declare an antiforgery stance.** `options.RequireAntiforgery` has no default,
and mapping a form endpoint without one throws at startup naming the operation. A form is the one
request shape a browser can be induced to send cross-origin with the user's cookies attached, so
defaulting either way is wrong for somebody. The stance is published as ASP.NET Core's own
`IAntiforgeryMetadata`, which means the host still has to run `UseAntiforgery()` for it to do
anything — stated in the docs rather than pretended otherwise.

`Accepts` follows the kind, defaulting a form endpoint to the two form media types. That default is
load-bearing: `AcceptsMatcherPolicy` reads it during routing, so a JSON default left in place would
reject every form request with a bare 415 before the binder ran.

**NE0006.** A form field or file member on a `GET` or `HEAD` contract is reported at build time. The
member binds perfectly well; there is simply never a body for it to bind from. Reported under the
same conservatism as `NE0002` — bodyless methods only, because `Configure` can change the body kind
in ways the generator reads only shallowly. There is deliberately no rule for a missing antiforgery
stance: that is also set in `Configure`, so mapping throws instead, which covers the reflective path
too.

**OpenAPI.** Form fields are documented as a multipart request body rather than as parameters —
OpenAPI has no `in: form`, and the parameter transformer's default arm would otherwise have
published them as query parameters, which a generated client would put in the URL. `IFormFile`
renders as `string`/`binary`, file collections as arrays of it, and the media types come from the
endpoint's own `Accepts` rather than a constant.

### Breaking

- `EndpointBindingSource` gains `Form`. A consumer switching exhaustively over it gets a new case.
- `EndpointParameterDescriber.Describe` takes an `EndpointBodyKind`, defaulted to `Json`.
- `EndpointBindingOptions` gains `BodyKind`, `EndpointOperationDescriptor` gains `BodyKind` and
  `RequireAntiforgery`, and `EndpointRequestBinder.ReadBodyAsync` an optional `bodyKind`. All are
  defaulted, so existing calls compile unchanged.
- `EndpointBindingFailure` gains `RequestTooLarge`, reported as 413.
- Regenerate: binders emitted by preview.3 carry no form branch.

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

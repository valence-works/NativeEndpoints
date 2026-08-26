# Binding

## Precedence

**Route, then body, then query, then the parameter's own default.**

Route wins over the body so a resource identifier in the URL cannot be contradicted by the payload.

## Contract shapes

A contract is bound either through a single public constructor, which is what a positional `record`
gives you, or by assignment to writable properties.

```csharp
public sealed record GetInvoice(string InvoiceId, bool IncludeLines = false);
```

A type with more than one public constructor is rejected at bind time; the binder will not guess.

A constructor parameter's declared default (`IncludeLines = false`) binds when no source supplies a
value — whether or not the member declares its source with a `[From...]` attribute, and under
strict parsing too: absence takes the default before strictness gets a say.

## Sources

Route, body, and query participate in the default precedence. Headers and claims never do: reading
them implicitly would let an unrelated header populate a parameter that happened to share its name.
Ask for them:

```csharp
public sealed record ListInvoices(
    string? Search,
    string[] Tag,
    [property: FromHeader("X-Tenant")] string? Tenant,
    [property: FromClaim("sub")] string? Subject);
```

`[FromRoute]`, `[FromQuery]`, `[FromHeader]`, and `[FromClaim]` each take an optional key when it
differs from the member's own name. On a positional record the attribute works on the parameter or,
with `[property: ...]`, on the generated property. An unauthenticated request has no claims, so a
claim-bound member simply binds absent; whether that is allowed is authorization's decision.

## Supported types

`string`, `bool`, `int`, `long`, `Guid`, `enum` (case-insensitive), and `DateTimeOffset`, plus
`Nullable<T>` of any of them. Route and query values are converted with invariant culture.

**Explicit nulls.** A property the caller sent as `null` stays null; one they omitted falls through
to the query string. The binder records which properties were actually present in the JSON, so those
two cases are distinguishable rather than both looking like "no value".

**Collections.** `T[]`, `List<T>`, and the read-only interfaces over them collect repeated keys:
`?tag=a&tag=b` binds two elements. A comma inside one value is part of that value, because guessing
that commas separate is the kind of implicit behavior that makes a binder unpredictable. A collection
with no values present binds empty rather than null, so a handler can enumerate without a null check.

**Repeated keys on a scalar.** A repeated query key bound to a scalar takes the first value:
`?page=1&page=2` binds `1`. Comma-joining the values into "1,2" — which is what minimal APIs do —
produces something no scalar sensibly means: it fails to parse, and a lenient fallback would then
silently bind zero. Multi-valued headers are different: HTTP defines a repeated header field as
equivalent to one comma-separated field, so a scalar bound with `[FromHeader]` from a repeated
header receives the comma-joined value.

**`IParsable<T>`.** Any type implementing it binds with no registration:

```csharp
public readonly record struct Slug(string Value) : IParsable<Slug> { /* ... */ }
```

**Your own types.** Register a parser for anything else:

```csharp
builder.Services.AddNativeEndpoints(o => o.ValueBinders.Add<Money>(Money.TryParse));
```

A registered parser wins over the built-in fallbacks, so a host can override how one of its own
types is read without forking the binder.

Anything else throws:

```
Request parameter 'amount' has unsupported type 'Money'. Implement IParsable<Money>, or register
a parser with AddNativeEndpoints(o => o.ValueBinders.Add<T>(...)), rather than widening the
binder implicitly.
```

That is a feature. A silent fallback to `default` is a bug you find in production; an exception is a
bug you find on the first request.

> Forms, multipart, and file upload are deliberately out of scope for 1.0. Use a plain `MapPost`
> alongside your endpoints.

## Body modes

`options.BodyMode` decides how the request body is treated. The default depends on the HTTP method:
`None` for GET and HEAD, `Optional` for DELETE, `Required` for everything else.

| Mode | Behavior |
|---|---|
| `None` | No body is read. Values come from route and query only |
| `Required` | A JSON body is required. An absent content type still attempts the body, so an empty or malformed payload is a 400 |
| `RequiredWithContentType` | As `Required`, but an absent content type is also unsupported media |
| `Optional` | A JSON body is read when present; its absence binds from route and query instead |
| `OptionalWithContentType` | As `Optional`, but the content type must match when one is declared |

## Who answers a 415

Mostly not this library, and that is worth knowing before you go looking for the code.

Declaring `options.Accepts` puts an `AcceptsMetadata` item on the endpoint, and ASP.NET Core's own
`AcceptsMatcherPolicy` uses it **during routing**. A request whose Content-Type does not match is
rejected there, before any handler runs, with a bare `415` and no body. That is the same behavior you
get from `MapPost(...).Accepts<T>("application/json")`, and it is ordinary ASP.NET Core doing its job.

The binder's own media-type check only comes into play when routing let the request through — for
example when `Accepts` includes `*/*`, or when it is not declared at all. Then the failure runs
through your `IEndpointProblemWriter` and carries a problem document.

So: if you want a bare 415 from routing, declare a narrow `Accepts`. If you want a problem body,
widen `Accepts` (`["*/*", "application/json"]` is the usual shape) and let the binder answer.

## Documented request schemas

Declaring `options.Accepts` is what decides whether a request schema appears in the document, not the
body mode. A GET that binds from the query can still advertise its request shape:

```csharp
options.Accepts = ["*/*", "application/json"];
```

## Strict parsing

By default a typed route or query value that does not parse falls back to the parameter's default:
`?page=notanumber` binds `0`. That is what most published contracts already do, so it is the default
here too. It is also the one place the binder does bind silently, which sits awkwardly beside
everything else on this page.

Turn it off per endpoint:

```csharp
public override void Configure(ApiEndpointOptions options) => options.StrictTypedParsing = true;
```

Now the same request is a `400` naming the value:

```json
{ "page": ["Value [notanumber] is not valid for a [Int32] property!"] }
```

The reported key is the wire name the query string documents (`page`), not the constructor parameter
it binds into (`Page`).

Strictness follows every conversion: a registered value binder that cannot read its value and a
collection element that does not parse are rejected the same way a scalar `int` is.

Absent is not the same as unreadable. A nullable member the caller omitted is simply null, strict or
not — `Guid?` and a nullable reference type implementing `IParsable<T>` alike. A non-nullable typed
member with no value is a failure under strict parsing, because the caller was required to send
something readable and did not; a constructor-parameter default counts as a value, so `int Page = 1`
binds `1` on absence instead of failing. A blank value (`?page=`) is not absent — the
caller did send it — so it is rejected even for a nullable member. Types read only through a
registered parser are the exception on absence: an absent value binds the type's default, and only a
value the caller actually sent can fail.

For a new endpoint this is the better setting. It is opt-in only because turning it on changes what
an existing API returns.

## Failures

| Failure | Status |
|---|---|
| `UnsupportedMediaType` | 415 |
| `MissingBody` | 400 |
| `MalformedBody` | 400, with the serializer's message under `serializerErrors` |
| `InvalidTypedValue` | 400, naming the value. Only under strict parsing |

Failures are written through the configured `IEndpointProblemWriter`. See [[Problem-Details]].

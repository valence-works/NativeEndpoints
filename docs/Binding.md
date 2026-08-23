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

## Supported types

Today: `string`, `bool`, `int`, `long`, `Guid`, `enum` (case-insensitive), and `DateTimeOffset`,
plus `Nullable<T>` of any of them. Route and query values are converted with invariant culture.

Anything else throws:

```
Request parameter 'amount' has unsupported type 'Money'.
Extend EndpointRequestBinder deliberately rather than widening it implicitly.
```

That is a feature. A silent fallback to `default` is a bug you find in production; an exception is a
bug you find on the first request.

> **Planned.** Headers, claims, query collections (`T[]`, `List<T>`), `IParsable<T>`, and a
> registration seam for your own types. Forms, multipart, and file upload are deliberately out of
> scope for 1.0 — use a plain `MapPost` alongside your endpoints.

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

## Failures

| Failure | Status |
|---|---|
| `UnsupportedMediaType` | 415 |
| `MissingBody` | 400 |
| `MalformedBody` | 400, with the serializer's message under `serializerErrors` |

Failures are written through the configured `IEndpointProblemWriter`. See [[Problem-Details]].

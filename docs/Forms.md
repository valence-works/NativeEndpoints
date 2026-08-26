# Forms

A form endpoint binds its contract from a `multipart/form-data` or
`application/x-www-form-urlencoded` body instead of from JSON. Declare the kind, declare an
antiforgery stance, and the rest is the binding you already know.

```csharp
public sealed record ImportWidgets(Guid BatchId, string Name, int Count, string[] Tag);

[Post("widgets/{batchId}/import")]
public sealed class Endpoint : ApiEndpoint<ImportWidgets, ImportView>
{
    public override void Configure(ApiEndpointOptions options)
    {
        options.BodyKind = EndpointBodyKind.Form;
        options.RequireAntiforgery = false;
    }

    public override Task<ImportView> HandleAsync(ImportWidgets request, CancellationToken token) => ...;
}
```

## A form is the body, not a fifth source

Precedence does not grow a step. A form *is* the request body, for a request whose content type says
so, and it occupies the body's place in the existing order:

```
route → body → query → the parameter's own default
          ↑
   a JSON property, or a form field
```

So a route value still beats a field of the same name, and a member the form did not carry still
falls through to the query string. `[FromForm]` overrides that order for one member, in exactly the
way `[FromQuery]` does — it is not the only way to reach a form field, because on a form endpoint an
unattributed member already binds from one. Reach for it in the two cases inference cannot express:

```csharp
public sealed record Upload(
    Guid Id,                                                  // from the route, not the form
    string Title,                                             // from the form
    [property: FromForm("legacy_name")] string? LegacyName,   // a field whose wire name differs
    [property: FromForm("id")] string? FormId);               // a field the route would otherwise shadow
```

## A form field is never null

JSON distinguishes three states — a property sent as `null`, a property sent as `""`, and a property
omitted. Form encoding has only two, because there is no null to send:

| | JSON | form |
|---|---|---|
| omitted | falls through to the query | falls through to the query |
| sent empty | `""` | `""` |
| sent as null | stays null | not representable |

This is the same shape the query string has always had, so the rules carry over unchanged: an empty
field binds the type's default under the lenient parser, and is reported under
`options.StrictTypedParsing`. Repeated keys collect into an array or list, one value per key —
`Tag=a&Tag=b`, never a comma split inside a single value.

There is one asymmetry worth knowing. An empty JSON body under `Required` is a `MissingBody` failure,
because it produced no contract at all. An empty *form* is a form with no fields, and whether that is
acceptable is a question about your contract's own nullability rather than about the body — so it
binds and your handler decides.

## Content types and who answers a 415

Declaring `BodyKind = Form` defaults `options.Accepts` to the two form media types. That default is
load-bearing rather than cosmetic: ASP.NET Core's `AcceptsMatcherPolicy` reads `Accepts` **during
routing**, so a JSON default left in place would reject every form request with a bare `415` before
the binder ever ran. Set `Accepts` yourself only if you want to narrow or widen that, and see
[Binding](Binding.md#who-answers-a-415) for which layer then answers.

The binder's own check uses `HttpRequest.HasFormContentType` — the framework's definition of "this is
a form" — so it accepts exactly what the server accepts.

## Antiforgery

A form endpoint must declare a stance. There is no default, and mapping one without an answer throws
at startup naming the operation:

```csharp
options.RequireAntiforgery = false;   // a token-authenticated API with no cookies to forge
options.RequireAntiforgery = true;    // a browser-submitted form
```

A form is the one request shape a browser can be induced to send cross-origin with the user's cookies
attached. Guessing either way is wrong for somebody: defaulting off is a CSRF hole, defaulting on
breaks machine-to-machine uploads with a 400 nobody expected. So this library asks, once, in one line.

The stance is published as ASP.NET Core's own `IAntiforgeryMetadata`, the same metadata
`DisableAntiforgery()` and `[RequireAntiforgeryToken]` produce, which means **it is inert unless your
pipeline runs the antiforgery middleware**. `WebApplication` adds `UseAntiforgery()` for you once
`IAntiforgery` is registered; a hand-composed pipeline does not. Middleware presence is not something
an endpoint convention can observe, so this library states the requirement and cannot enforce it.

## Limits

Form limits are the framework's, not this library's:

```csharp
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 32 * 1024 * 1024;
    o.ValueCountLimit = 512;
});
```

An exceeded multipart limit and a malformed boundary both surface from `FormReader` as
`InvalidDataException`, with nothing but the message to tell them apart, so both are reported as a
`400` carrying your problem shape. What matters is that neither becomes a `500` disclosing a stack
trace. A body the *server* rejects — Kestrel's `MaxRequestBodySize` — is reported as a `413`.

Note that reading a form **buffers**. File sections above `FormOptions.MemoryBufferThreshold` spool to
a temporary file before your handler runs.

## Files

Four member shapes, matching what Minimal APIs accept:

| Member | Binds |
|---|---|
| `IFormFile? Avatar` | the first file sent under the field name `Avatar` |
| `IFormFile[] Pages` / `List<IFormFile> Pages` | every file sent under `Pages`, in order |
| `IFormFileCollection Everything` | **every** file in the request, whatever field name it arrived under |
| `IFormFile Required` | the same as the nullable form — an absent file is null, not an error |

A file is not parsed from a string, so it takes none of the precedence chain: no route value, no query
value, and no `IParsable<T>`. An absent file binds null and absent collections bind empty, exactly as
everywhere else in this binder, which means a non-nullable `IFormFile` member can still be null at
runtime. Your contract's nullability is a declaration of intent, not a guarantee the binder enforces
— if a missing upload should be a 400, say so in the handler.

```csharp
public sealed record ImportWidgets(
    Guid BatchId,              // route
    string Name,               // form field
    IFormFile? Manifest,       // one file
    IFormFile[] Attachment);   // every file under "Attachment"
```

Reading a form **buffers** it. Sections above `FormOptions.MemoryBufferThreshold` (64 KB by default)
spool to a temporary file before your handler runs, so `IFormFile.OpenReadStream()` never blocks on
the network. It also means the whole upload has landed before you see any of it.

## OpenAPI

Add the optional package and the form fields are documented as a multipart request body:

```csharp
builder.Services.AddOpenApi();
builder.Services.AddNativeEndpointsOpenApi();
```

```yaml
requestBody:
  required: true
  content:
    multipart/form-data:
      schema:
        type: object
        properties:
          Title:    { type: string }
          Count:    { type: integer, format: int32 }
          Manifest: { type: string, format: binary }
          Attachment:
            type: array
            items: { type: string, format: binary }
```

Form fields are deliberately **not** written as parameters. OpenAPI has no `in: form`, and a field
published as a query parameter would have generated clients putting it in the URL — worse than
omitting it. Route values on the same operation are still path parameters, and a renamed field is
documented under its wire name rather than its member name.

The content types come from the endpoint's own `Accepts`, so the document agrees with what routing
will actually accept instead of with a guess.

## What is not here

- **Streaming multipart.** Reading a form buffers it; a genuinely streaming upload wants
  `MultipartReader` directly, from a plain `MapPost` beside your endpoints.
- **Nested objects.** A form field binds to a contract member, not into a sub-object graph.

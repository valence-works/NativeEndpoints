# Minimal

Five endpoints over an in-memory store. No authentication, no database, no configuration.

```bash
dotnet run --project samples/Minimal
```

Then `http://localhost:5080/openapi/v1.json`, or:

```bash
curl localhost:5080/api/notes
curl -X POST localhost:5080/api/notes -H 'Content-Type: application/json' -d '{"title":"First","body":"hello"}'
curl -X DELETE localhost:5080/api/notes/{id}
```

## What each endpoint shows

| Endpoint | Base type | Shows |
|---|---|---|
| `GET /api/notes` | `ApiEndpoint<TRequest, TResponse>` | Binding from the query string |
| `GET /api/notes/{noteId}` | `ApiEndpoint<TRequest, TResponse>` | Binding from the route; a domain exception becoming a 404 |
| `POST /api/notes` | `ApiEndpointWithResult<TRequest, TResponse>` | A status decided by the handler, plus a `Location` header |
| `PUT /api/notes/{noteId}` | `ApiEndpoint<TRequest, TResponse>` | Route and body in one contract, route winning |
| `DELETE /api/notes/{noteId}` | `ApiEndpoint<TRequest>` | 204 No Content |

The whole composition root is one line in `Program.cs`. Operation identifiers are never written down:
`Minimal.Endpoints.Notes.Create.Endpoint` becomes `NotesCreate`, published as `Minimal_NotesCreate`.

## Three things this sample taught us

Samples exist to find the sharp edges before users do. These are real, and none of them are hidden.

**Route and query parameters needed solving separately.** Handlers are published as bare
`RequestDelegate` so that API Explorer never retains a handler `MethodInfo` — that is what makes
endpoint assemblies collectible. The cost is that API Explorer has nothing to infer parameters from.
The library states them itself instead, and `NativeEndpoints.OpenApi` turns them into document
parameters; this sample references it, which is why `GET /api/notes/{noteId}` documents `noteId`.
Without that package the schemas are still correct and the parameters are simply absent.

**A handler-decided status has to be declared.** `ApiEndpointWithResult` lets the handler choose the
status at runtime, which means the framework cannot know what to document. `Create` sets
`options.SuccessStatus = 201` for exactly this reason. Without it the document says 200 while callers
receive 201.

**DELETE advertises a request body unless you turn it off.** The default body mode for DELETE is
`Optional`, because some DELETEs do take bodies, and declaring a body mode is what makes an operation
document a request schema. For a route-only DELETE, set `options.BodyMode = EndpointBodyMode.None`.
Arguably the default is wrong; it is left alone for now rather than changed on the strength of one
sample.

## Where the 415 comes from

`POST /api/notes` with `Content-Type: text/plain` returns a bare `415` with no body. That is ASP.NET
Core's `AcceptsMatcherPolicy` rejecting the request during routing, using the `AcceptsMetadata` the
endpoint declared, exactly as it would for `MapPost(...).Accepts<T>("application/json")`. The
library's own media-type check never runs.

Widen `options.Accepts` to include `*/*` if you would rather answer with a problem document.

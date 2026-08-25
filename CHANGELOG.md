# Changelog

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

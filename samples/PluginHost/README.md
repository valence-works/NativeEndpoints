# PluginHost

A web host that loads endpoints from a plugin assembly at runtime, serves them, unloads the plugin,
and shows that the assembly is actually collected. This is the sample behind the claim in the
[README](../../README.md); everything here is measured, not asserted.

```bash
dotnet run --project samples/PluginHost/Host
```

```bash
curl localhost:5081/admin/status
curl localhost:5081/api/greetings/you
curl -X POST localhost:5081/admin/unload
curl -X POST localhost:5081/admin/collect     # the definitive answer, see below
```

## Three projects, and why

| Project | Loaded into | Holds |
|---|---|---|
| `Contracts` | The default context, once | `GetGreeting`, `GreetingView` — every type that reaches endpoint metadata |
| `Plugin` | A collectible context, per load | The endpoint classes and their handling |
| `Host` | The default context | The load context, the mutable route table, the admin endpoints |

**This split is the whole trick.** API Explorer retains an endpoint's request and response `Type` for
the host's lifetime. Put those types in the collectible assembly and it can never be released — and
NativeEndpoints will tell you so at map time, by rejecting the endpoint rather than letting you find
out in production. Contracts are shared and stable; only the implementation is collectible.

Note what the plugin's endpoint class does *not* contain: nothing plugin-aware, no lifecycle hooks,
no registration call. It is an ordinary endpoint class that happens to live in an assembly the host
intends to unload.

## Four things that will bite you

Every one of these was found by building this sample.

**1. Shared assemblies must resolve from the default context.** `PluginLoadContext.Load` returns
`null` for anything the host already has, which delegates to the default context. Load a second copy
of the contracts assembly and the host and plugin end up with two CLR identities for the same type,
so casting an endpoint to `ApiEndpointBase` fails for reasons that read as impossible.

**2. Endpoints must be dropped before the context is unloaded.** A published endpoint holds the
request delegate, and the delegate holds the plugin. `PluginRegistry.Unload` publishes an empty route
table first, then unloads.

**3. You cannot observe collection from the request that triggered the unload.** This is the one that
will waste your afternoon. Measured on this sample: **forty** forced, blocking, compacting collections
inside the unload request report `collected: false`, while **ten** in any subsequent request report
`true`. The triggering request is still on the stack and routing state for it still references the
generation being retired. `POST /admin/unload` is therefore honest about being best-effort, and
`POST /admin/collect` gives the real answer.

**4. Only a weak reference may survive.** `PluginRegistry` keeps a `WeakReference` and nothing else.
Holding the context strongly in order to report on it would be the thing keeping it alive, and the
report would be a tautology.

## The result

Three consecutive cycles, each generating the OpenAPI document, serving a request from the plugin,
unloading, and collecting:

```
cycle 1: 'Hello, Cycle1.' -> collected=True
cycle 2: 'Hello, Cycle2.' -> collected=True
cycle 3: 'Hello, Cycle3.' -> collected=True
```

The document matters: OpenAPI generation is what makes API Explorer retain endpoint metadata, so a
host that never builds one is the easy case. This sample builds one on every cycle.

## Dynamic routing

`PluginEndpointDataSource` is an ordinary `EndpointDataSource` whose change token fires when a new
generation is published; routing rebuilds its matcher in response. `AddDynamicEndpointApiExplorerRefresh()`
does the same for API Explorer, whose description collection is otherwise cached for the host's
lifetime and would keep serving routes that no longer exist.

`EndpointCollector` is a small `IEndpointRouteBuilder` that collects endpoints instead of publishing
them, so the host can map a plugin with the ordinary `MapEndpointGroup` call and then decide what to
do with the result.

## Endpoints

| Route | Purpose |
|---|---|
| `GET /admin/status` | What is loaded, how many endpoints, whether the last context was collected |
| `POST /admin/load` | Load the plugin and publish its endpoints |
| `POST /admin/unload` | Drop the endpoints and unload. Best-effort collection report |
| `POST /admin/collect` | Force collections and report definitively |
| `GET /api/greetings` | Served by the plugin |
| `GET /api/greetings/{name}` | Served by the plugin |

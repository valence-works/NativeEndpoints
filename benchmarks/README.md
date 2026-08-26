# Benchmarks

BenchmarkDotNet comparison of per-request cost across four ways of serving the same two
operations, all in-process via `Microsoft.AspNetCore.TestHost`:

| Stack | What it is |
|---|---|
| `RawMinimalApi` | Hand-written `MapGet`/`MapPost` lambdas. The baseline. |
| `NativeReflective` | NativeEndpoints endpoint classes mapped via `MapEndpointsFrom(assembly)` (the reflective binder). |
| `NativeGenerated` | The same endpoint classes mapped via the source-generated `Map()` (the emitted binder). |
| `FastEndpoints` | The same operations on [FastEndpoints](https://fast-endpoints.com/) 8.3.0. |

Two scenarios, kept semantically identical everywhere:

- **`GetRouteQueryBenchmarks`** — `GET /items/42?tag=a&tag=b&page=5`: an `int` bound from the
  route, a `string[]` and an `int` bound from the query, a small JSON DTO echoed back.
- **`PostJsonBodyBenchmarks`** — `POST /items`: a five-property JSON body deserialized into a
  request contract, a small JSON DTO echoed back.

Every benchmark sends one request over the TestServer's `HttpClient` and throws on any status
other than `200`, so a misconfigured stack fails instead of posting a fast number. On top of that,
`[GlobalSetup]` runs a conformance pass asserting all four stacks return semantically identical
JSON, so a stack that silently skipped binding work cannot "win". `[MemoryDiagnoser]` reports
allocations per request.

## Running

Full run (takes a while; BenchmarkDotNet decides iteration counts):

```bash
dotnet run -c Release --project benchmarks/NativeEndpoints.Benchmarks
```

Quick smoke, one cold iteration of everything (checks wiring, not performance):

```bash
dotnet run -c Release --project benchmarks/NativeEndpoints.Benchmarks -- --job dry --filter '*'
```

Subset by name:

```bash
dotnet run -c Release --project benchmarks/NativeEndpoints.Benchmarks -- --filter '*PostJsonBody*'
```

`--list flat` shows the available benchmark names; `--job short` is a faster-but-rougher
alternative to the default job.

## Caveats

- **This measures the framework layer, not a web server.** Requests go through
  `Microsoft.AspNetCore.TestHost` in memory: no Kestrel, no sockets, no network, no TLS. The
  numbers isolate routing + binding + handler dispatch + serialization, plus the constant cost of
  the in-memory client/server pair, which is identical across stacks. Absolute numbers say nothing
  about deployed throughput; only the differences between stacks are meaningful.
- **Results are machine-dependent.** Ratios move with CPU, OS, and .NET version. Compare stacks
  within one run on one machine; do not compare absolute numbers across machines or runs.
- **Dry/smoke output is not a measurement.** A `--job dry` run executes each benchmark once,
  cold, with `Error` reported as `NA`. It proves the stacks are wired correctly and nothing more.
- **Each stack is exercised as configured here.** FastEndpoints runs with default options and
  anonymous endpoints, discovery pinned to this assembly (a benchmark child process's entry
  assembly is BenchmarkDotNet's generated host, so auto-discovery would find nothing). A
  differently-tuned configuration of any stack could score differently.

# Unload Safety

If you host plugins in collectible `AssemblyLoadContext`s, endpoint frameworks are usually where
unloading goes to die. This page explains what roots an assembly, what the library does about it, and
how to verify the claim yourself rather than believing it.

## What roots a collectible assembly

A collectible load context stays alive while anything reachable from a GC root references a type in
it. For endpoints, the usual culprits are:

- **A static registry.** Configuration or registrations held in a static field of a
  non-collectible framework assembly, keyed by or holding your types.
- **Handler `MethodInfo` in endpoint metadata.** `RequestDelegateFactory` publishes the handler's own
  method and its async state machine when you pass a typed lambda. API Explorer then retains that for
  the host service-provider lifetime.
- **Contract types in documented metadata.** A request or response `Type` reachable from API Explorer
  is retained the same way, which is why this matters far more once an OpenAPI document exists.
- **Serializer caches.** A `JsonSerializerContext`, `JsonSerializerOptions`, or `JsonTypeInfo`
  reachable from metadata pins whatever it describes.

None of this is visible in code review. It is only visible if you measure.

## What the library does

**No process-global state.** There is no static registry. A group scans only the assembly you hand
it, inside your own mapping call, and retains nothing past the endpoint generation.

**No framework static references your types.** Caches inside the framework are weak-keyed or scoped
to the generation that created them.

**Handlers publish as bare `RequestDelegate`.** This is the reason the library hand-writes its
binder instead of delegating to `RequestDelegateFactory`: a typed lambda would put the handler's
`MethodInfo` and its `AsyncStateMachineAttribute` into metadata.

**Compiler metadata is stripped.** `AsyncStateMachineAttribute` and `DebuggerStepThroughAttribute` are
removed from completed metadata, whether or not enforcement is on, because a state machine pins its
owner even in a host that never builds a document.

**Completed metadata is validated.** `EndpointLifetimeValidator` runs as the final convention and
walks the whole metadata graph — types, members, delegates and their targets, serializer contexts,
`JsonTypeInfo`, and enumerables of any of those — rejecting anything collectible. Accepted endpoints
carry an `EndpointLifetimeMetadata` marker holding strings and enum values only, so the marker itself
can never be what pins an assembly.

## Enforcement

On by default and fail-closed: a host that configured nothing, or has no service provider yet, keeps
the guard.

```csharp
builder.Services.SuppressEndpointLifetimeEnforcement();
```

Suppression is only correct where nothing builds an OpenAPI document. The retention the boundary
guards against comes from API Explorer's host-lifetime caches, not from the endpoint metadata itself.
A suppressed endpoint carries no lifetime marker, because nothing verified it. Compiler metadata is
still stripped.

When enforcement rejects an endpoint you get a deterministic report naming the group, the endpoint,
the category, the exact artifact, and the load context it belongs to.

## Dynamic hosts

If routes come and go while the host runs, API Explorer needs telling that its description collection
is stale:

```csharp
builder.Services.AddDynamicEndpointApiExplorerRefresh();
```

## Verifying it yourself

```bash
dotnet add package NativeEndpoints.Testing
```

```csharp
[Fact]
public async Task Module_unloads()
{
    var evidence = await CollectibleEndpointFixture.RunCyclesAsync(cycles: 3);
    UnloadEvidence.Verify(evidence, gcRounds: 32);
}
```

The harness compiles a synthetic endpoint assembly, loads it into a collectible context, maps it,
serves a request, disposes the host, unloads, forces repeated full collections, and reports which
stage still roots the context.

It has no dependency on NativeEndpoints, so you can point it at whatever you use today and measure
that instead. Measuring before you migrate is a better reason to migrate than a README is.

## Honest limits

The guarantee is proven by tests, and the guard is enforced at map time. Neither is the same as
production evidence: in most hosts nothing ever creates a collectible load context, so nothing is
ever unloaded. What the library gives you is that when you do create one, the endpoint layer is not
what stops it being collected.

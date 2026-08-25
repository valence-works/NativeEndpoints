# Aot

The same endpoint shape as [Minimal](../Minimal), published as a native binary.

```bash
dotnet publish samples/Aot -r linux-x64   # or osx-arm64, win-x64
./bin/Release/net10.0/<rid>/publish/Aot
```

Measured on this sample: an 11 MB self-contained native binary, serving route, query, and collection
binding, with **zero IL trim or AOT warnings**.

## What makes it work

**Generated registration, binding, and activation.** `Aot.Map()` comes from the source generator. It
names every endpoint, constructs each contract directly, and news each endpoint up from request
services. Nothing resolves a constructor, makes a generic method, or asks a container to build a type
the trimmer cannot see.

**A source-generated serializer context.** `MapEndpointGroup("Aot", AotJson.Default)` hands the group
a `JsonSerializerContext`, so reading and writing JSON go through `JsonTypeInfo` rather than runtime
reflection. Without one, the group falls back to the host's options and AOT will warn.

**`IParsable<T>` for domain types.** It compiles to a constrained call on a static abstract interface
member, which is fully AOT-safe. This is why it is the supported way to add a bindable type.

## The one thing to watch

A `JsonSerializerContext` applies no naming policy unless you give it one, while the non-context
fallback uses `JsonSerializerOptions.Web`, which is camelCase. Adopting a context can therefore
change your JSON casing, which is wire-visible. Set it deliberately:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WidgetView))]
internal partial class AotJson : JsonSerializerContext;
```

## What is still reflective

Nothing on this path. The reflective binder and the reflective mapper both still exist and are
annotated `RequiresUnreferencedCode`, so a trimmed build tells you at the boundary if you reach them.
A project running the generator does not.

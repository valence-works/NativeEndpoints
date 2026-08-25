# Samples

- **[Minimal](Minimal)** — five endpoints over an in-memory store. No authentication, no database,
  no configuration. `dotnet run --project samples/Minimal`
- **[PluginHost](PluginHost)** — endpoints loaded from a collectible assembly at runtime, served,
  unloaded, and measurably collected. `dotnet run --project samples/PluginHost/Host`

- **[Aot](Aot)** — the same shape published as a native binary, with zero trim or AOT warnings.
  `dotnet publish samples/Aot -r linux-x64`

Planned:

- **VerticalSlice** — a fuller resource with permissions, problem translation, and a source-generated
  serializer context.

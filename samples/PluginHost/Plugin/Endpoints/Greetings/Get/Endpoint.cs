using NativeEndpoints;
using PluginHost.Contracts;

namespace PluginHost.Plugin.Endpoints.Greetings.Get;

/// <summary>
/// An ordinary endpoint class that happens to live in a collectible assembly.
/// </summary>
/// <remarks>
/// Nothing here is plugin-aware. The request and response types come from the shared contracts
/// assembly, which is what keeps this assembly collectible: those are the types that reach endpoint
/// metadata, and metadata is what API Explorer retains.
/// </remarks>
[Get("greetings/{name}")]
public sealed class Endpoint : ApiEndpoint<GetGreeting, GreetingView>
{
    public override Task<GreetingView> HandleAsync(GetGreeting request, CancellationToken cancellationToken) =>
        Task.FromResult(new GreetingView(
            $"Hello, {request.Name}.",
            typeof(Endpoint).Assembly.GetName().Name!));
}

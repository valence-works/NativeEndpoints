using NativeEndpoints;
using PluginHost.Contracts;

namespace PluginHost.Plugin.Endpoints.Greetings.List;

[Get("greetings")]
public sealed class Endpoint : ApiEndpointWithoutRequest<GreetingListView>
{
    public override Task<GreetingListView> HandleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new GreetingListView(
            ["formal", "casual"],
            typeof(Endpoint).Assembly.GetName().Name!));
}

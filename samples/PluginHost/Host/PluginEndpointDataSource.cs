using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace PluginHost.Host;

/// <summary>
/// A route table that can be replaced while the host runs.
/// </summary>
/// <remarks>
/// Ordinary ASP.NET Core: routing rebuilds its matcher whenever the change token fires. Endpoints
/// must be dropped from here <em>before</em> a context is unloaded, because a published endpoint
/// holds the request delegate, and the delegate holds the plugin.
/// </remarks>
public sealed class PluginEndpointDataSource : EndpointDataSource
{
    private readonly Lock _gate = new();
    private List<Endpoint> _endpoints = [];
    private CancellationTokenSource _cancellation = new();
    private IChangeToken _changeToken;

    public PluginEndpointDataSource() => _changeToken = new CancellationChangeToken(_cancellation.Token);

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get { lock (_gate) return _endpoints; }
    }

    public override IChangeToken GetChangeToken()
    {
        lock (_gate) return _changeToken;
    }

    /// <summary>Publishes a new generation of endpoints and signals routing to rebuild.</summary>
    public void Publish(IEnumerable<Endpoint> endpoints)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            _endpoints = [.. endpoints];
            previous = _cancellation;
            _cancellation = new CancellationTokenSource();
            _changeToken = new CancellationChangeToken(_cancellation.Token);
        }

        previous.Cancel();
        previous.Dispose();
    }
}

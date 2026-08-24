using System.Runtime.CompilerServices;
using NativeEndpoints;

namespace PluginHost.Host;

/// <summary>Loads, serves, and unloads one plugin at a time.</summary>
public sealed class PluginRegistry(PluginEndpointDataSource routes, IServiceProvider services, ILogger<PluginRegistry> logger)
{
    private WeakReference? _unloaded;
    private PluginLoadContext? _context;

    public string? LoadedName { get; private set; }

    /// <summary>True once a previously loaded plugin's context has actually been collected.</summary>
    public bool? PreviousContextCollected => _unloaded is null ? null : !_unloaded.IsAlive;

    /// <summary>
    /// Forces collections and re-reports. Unloading is asynchronous, and a context is released only
    /// once every reference to anything inside it has gone, so the honest answer can arrive several
    /// collections after the call to Unload.
    /// </summary>
    public bool? Collect(int collections = 10)
    {
        if (_unloaded is null)
            return null;

        for (var attempt = 0; attempt < collections && _unloaded.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return !_unloaded.IsAlive;
    }

    /// <summary>Loads the assembly at <paramref name="path"/> and publishes its endpoints.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Load(string path, string routePrefix)
    {
        if (_context is not null)
            throw new InvalidOperationException("A plugin is already loaded. Unload it first.");

        var context = new PluginLoadContext(path);
        var assembly = context.LoadFromAssemblyPath(path);

        // The ordinary mapping call. The group scans only this assembly, inside this call, and keeps
        // nothing afterwards - which is the property that makes the context collectible at all.
        var collector = new EndpointCollector(services);
        collector.MapEndpointGroup(assembly.GetName().Name!).MapEndpointsFrom(assembly, routePrefix);
        routes.Publish(collector.Build());

        _context = context;
        LoadedName = assembly.GetName().Name;
        logger.LogInformation("Loaded {Plugin} with {Count} endpoints.", LoadedName, routes.Endpoints.Count);
    }

    /// <summary>
    /// Drops the plugin's endpoints and unloads its context.
    /// </summary>
    /// <remarks>
    /// Endpoints go first: a published endpoint holds the request delegate, and the delegate holds
    /// the plugin. Only a weak reference to the context survives this method, which is what makes any
    /// later answer trustworthy - a strong reference held for reporting would be the thing keeping it
    /// alive.
    /// <para>
    /// The returned value is best-effort and will normally be <c>false</c>, which is not a failure.
    /// The request that triggered the unload is still on the stack, and routing state for the
    /// in-flight request still references the generation being retired. Measured on this sample:
    /// forty forced, blocking, compacting collections inside this call report <c>false</c>, while ten
    /// in any subsequent request report <c>true</c>. Call <see cref="Collect"/> from a later request
    /// for the real answer.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Unload(int collections = 4)
    {
        if (_context is null)
            throw new InvalidOperationException("No plugin is loaded.");

        routes.Publish([]);

        var reference = new WeakReference(_context);
        _context.Unload();
        _context = null;
        LoadedName = null;

        // Unloading is asynchronous. A plain GC.Collect() is not enough: the context is released
        // only once every reference to anything inside it has gone, and that can take several
        // forced, blocking, compacting collections. Reporting after four ordinary collections
        // produced a confident, wrong "false".
        for (var attempt = 0; attempt < collections && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        _unloaded = reference;
        var collected = !reference.IsAlive;
        logger.LogInformation(
            "Unloaded. Collected while the triggering request is still on the stack: {Collected}. " +
            "Check again from a later request for the definitive answer.", collected);
        return collected;
    }
}

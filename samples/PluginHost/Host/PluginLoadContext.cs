using System.Reflection;
using System.Runtime.Loader;

namespace PluginHost.Host;

/// <summary>
/// A collectible context holding exactly one plugin assembly.
/// </summary>
/// <remarks>
/// Everything the plugin shares with the host - the contracts assembly, NativeEndpoints, ASP.NET Core
/// itself - must resolve to the copy already loaded in the default context. Returning null from
/// <see cref="Load"/> is what delegates that. Loading a second copy would give the host and the
/// plugin two CLR identities for the same type, and casting an endpoint to
/// <c>ApiEndpointBase</c> would fail for reasons that read as impossible.
/// </remarks>
internal sealed class PluginLoadContext(string pluginPath)
    : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Anything the host already has, the plugin shares. Only assemblies genuinely private to the
        // plugin are loaded here, and those are the ones that become collectible.
        if (Default.Assemblies.Any(assembly => assembly.GetName().Name == assemblyName.Name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

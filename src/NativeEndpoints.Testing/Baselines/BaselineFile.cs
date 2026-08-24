using NativeEndpoints.Testing.Serialization;

namespace NativeEndpoints.Testing.Baselines;

/// <summary>Read-only access to a committed compatibility baseline.</summary>
public static class BaselineFile
{
    public static string Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.ReadAllText(path);
    }

    public static T Load<T>(string path) => CompatibilityJson.Deserialize<T>(Read(path));

    public static string LoadCanonical(string path) => CompatibilityJson.Canonicalize(Read(path));
}

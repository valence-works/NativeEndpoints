using System.Collections.Immutable;

namespace NativeEndpoints;

/// <summary>The closed set of metadata categories inspected by the unload-safety boundary.</summary>
public enum EndpointLifetimeValidationCategory
{
    /// <summary>A request contract type reachable from endpoint metadata.</summary>
    RequestType,
    /// <summary>A response body type reachable from endpoint metadata.</summary>
    ResponseType,
    /// <summary>A metadata object itself, or a value reachable from one.</summary>
    MetadataObject,
    /// <summary>A member or method handle held in metadata.</summary>
    MemberOrMethod,
    /// <summary>A delegate, its target, or a document transformer.</summary>
    DelegateOrTransformer,
    /// <summary>A serializer context, options object, or JsonTypeInfo.</summary>
    SerializerMetadata
}

/// <summary>
/// Immutable, value-only marker attached after an endpoint's completed metadata has passed the
/// unload-safety boundary. Deliberately not an endpoint framework abstraction: it holds strings and
/// enum values only, so the marker itself can never be what pins a collectible assembly.
/// </summary>
public sealed record EndpointLifetimeMetadata
{
    /// <summary>Records a validated endpoint and the categories that were inspected.</summary>
    public EndpointLifetimeMetadata(
        string group,
        string endpoint,
        ImmutableArray<EndpointLifetimeValidationCategory> checkedCategories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (checkedCategories.IsDefaultOrEmpty)
            throw new ArgumentException("At least one endpoint lifetime validation category is required.", nameof(checkedCategories));

        Group = group.Trim();
        Endpoint = endpoint.Trim();
        CheckedCategories = checkedCategories;
    }

    /// <summary>Records a validated endpoint inspected across every category.</summary>
    public EndpointLifetimeMetadata(string group, string endpoint)
        : this(group, endpoint, EndpointLifetimeValidationCategories.All)
    {
    }

    /// <summary>The group the validated endpoint was mapped in.</summary>
    public string Group { get; }

    /// <summary>The endpoint's display name at validation time.</summary>
    public string Endpoint { get; }

    /// <summary>The categories the accepted marker records as inspected.</summary>
    public ImmutableArray<EndpointLifetimeValidationCategory> CheckedCategories { get; }
}

/// <summary>Provides the fixed validation set recorded by accepted endpoint markers.</summary>
public static class EndpointLifetimeValidationCategories
{
    /// <summary>Every category an accepted marker records as inspected.</summary>
    public static ImmutableArray<EndpointLifetimeValidationCategory> All { get; } =
    [
        EndpointLifetimeValidationCategory.RequestType,
        EndpointLifetimeValidationCategory.ResponseType,
        EndpointLifetimeValidationCategory.MetadataObject,
        EndpointLifetimeValidationCategory.MemberOrMethod,
        EndpointLifetimeValidationCategory.DelegateOrTransformer,
        EndpointLifetimeValidationCategory.SerializerMetadata
    ];
}

namespace NativeEndpoints;

/// <summary>Identifies the exact unsafe reference found in completed endpoint metadata.</summary>
public sealed record UnsafeEndpointMetadataViolation
{
    /// <summary>Records one unsafe reference found in completed endpoint metadata.</summary>
    public UnsafeEndpointMetadataViolation(
        string group,
        string endpoint,
        EndpointLifetimeViolationCategory category,
        string artifactIdentity,
        string loadContextIdentity)
    {
        if (group is null)
            throw new ArgumentNullException(nameof(group));
        if (string.IsNullOrWhiteSpace(group))
            throw new ArgumentException("A violation group is required.", nameof(group));
        if (endpoint is null)
            throw new ArgumentNullException(nameof(endpoint));
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("A violation endpoint is required.", nameof(endpoint));
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category), category, "A violation category must be defined.");
        if (artifactIdentity is null)
            throw new ArgumentNullException(nameof(artifactIdentity));
        if (string.IsNullOrWhiteSpace(artifactIdentity))
            throw new ArgumentException("A violation artifact identity is required.", nameof(artifactIdentity));
        if (loadContextIdentity is null)
            throw new ArgumentNullException(nameof(loadContextIdentity));
        if (string.IsNullOrWhiteSpace(loadContextIdentity))
            throw new ArgumentException("A violation load-context identity is required.", nameof(loadContextIdentity));
        Group = group.Trim();
        Endpoint = endpoint.Trim();
        Category = category;
        ArtifactIdentity = artifactIdentity.Trim();
        LoadContextIdentity = loadContextIdentity.Trim();
    }

    /// <summary>The group the endpoint was mapped in.</summary>
    public string Group { get; }

    /// <summary>The endpoint's display name.</summary>
    public string Endpoint { get; }
    /// <summary>What kind of unsafe reference this is.</summary>
    public EndpointLifetimeViolationCategory Category { get; }
    /// <summary>The offending type, member, or path within the metadata graph.</summary>
    public string ArtifactIdentity { get; }
    /// <summary>The load context the artifact belongs to, and whether it is collectible.</summary>
    public string LoadContextIdentity { get; }
}

/// <summary>Categories used in deterministic unload-safe OpenAPI diagnostics.</summary>
public enum EndpointLifetimeViolationCategory
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
    SerializerMetadata,
    /// <summary>A metadata shape the validator could not prove safe.</summary>
    UnknownMetadataShape
}

/// <summary>
/// Thrown before endpoint publication when API Explorer-facing metadata crosses into a collectible
/// implementation generation.
/// </summary>
public sealed class UnsafeEndpointMetadataException : InvalidOperationException
{
    /// <summary>Reports a single violation.</summary>
    public UnsafeEndpointMetadataException(UnsafeEndpointMetadataViolation violation)
        : this([violation])
    {
    }

    /// <summary>Reports every violation found on one endpoint, ordered deterministically.</summary>
    public UnsafeEndpointMetadataException(IEnumerable<UnsafeEndpointMetadataViolation> violations)
        : base(BuildMessage(violations, out var ordered))
    {
        Violations = ordered;
    }

    /// <summary>Every violation, in deterministic order.</summary>
    public IReadOnlyList<UnsafeEndpointMetadataViolation> Violations { get; }

    /// <summary>The single violation, when there is exactly one.</summary>
    public UnsafeEndpointMetadataViolation Violation => Violations.Count == 1
        ? Violations[0]
        : throw new InvalidOperationException("The exception contains more than one violation.");

    private static string BuildMessage(
        IEnumerable<UnsafeEndpointMetadataViolation> violations,
        out IReadOnlyList<UnsafeEndpointMetadataViolation> ordered)
    {
        ArgumentNullException.ThrowIfNull(violations);
        var materialized = violations.ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("At least one endpoint lifetime violation is required.", nameof(violations));

        ordered = materialized
            .OrderBy(violation => violation.Category)
            .ThenBy(violation => violation.Group, StringComparer.Ordinal)
            .ThenBy(violation => violation.Endpoint, StringComparer.Ordinal)
            .ThenBy(violation => violation.ArtifactIdentity, StringComparer.Ordinal)
            .ThenBy(violation => violation.LoadContextIdentity, StringComparer.Ordinal)
            .ToArray();

        var lines = ordered.Select(FormatViolation);
        return "Unsafe endpoint metadata validation failed:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string FormatViolation(UnsafeEndpointMetadataViolation violation)
    {
        return $"- group='{violation.Group}'; endpoint='{violation.Endpoint}'; category={violation.Category}; artifact='{violation.ArtifactIdentity}'; loadContext='{violation.LoadContextIdentity}'";
    }
}

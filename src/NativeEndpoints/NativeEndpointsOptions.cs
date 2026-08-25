namespace NativeEndpoints;

/// <summary>Host-wide options for the endpoint pipeline.</summary>
public sealed class NativeEndpointsOptions
{
    /// <summary>
    /// Replaces the metadata convention applied to every mapped operation. Defaults to
    /// <see cref="EndpointConventionBuilderExtensions.ApplyDefaultOperationMetadata"/>.
    /// </summary>
    public EndpointOperationConvention OperationConvention { get; set; } =
        EndpointConventionBuilderExtensions.ApplyDefaultOperationMetadata;

    /// <summary>
    /// Parsers for types the binder does not know natively.
    /// </summary>
    /// <remarks>
    /// The binder covers a small, predictable set of shapes and throws on anything else. Registering
    /// a parser here is how a contract uses a domain type without the binder growing to guess at it.
    /// A type implementing <see cref="IParsable{TSelf}"/> needs no registration.
    /// </remarks>
    public EndpointValueBinders ValueBinders { get; } = new();
}

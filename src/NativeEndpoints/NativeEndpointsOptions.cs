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
}

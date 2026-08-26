using Microsoft.AspNetCore.Http;

namespace NativeEndpoints;

/// <summary>
/// Everything <see cref="EndpointGroup.MapOperation{TMessage}"/> needs to know about the operation
/// being mapped, carried as one object.
/// </summary>
/// <remarks>
/// The description travels as a record rather than a parameter list so that a setting added here
/// reaches every mapping path by construction: the typed Map methods all build their descriptor from
/// <see cref="ApiEndpointOptions"/> in a single place, so a new option cannot be silently dropped by
/// one forwarding overload the way a positional parameter can.
/// </remarks>
public sealed record EndpointOperationDescriptor
{
    /// <summary>The HTTP method the route is mapped for.</summary>
    public required string Method { get; init; }

    /// <summary>The route pattern, relative to any prefix the group was mapped with.</summary>
    public required string Pattern { get; init; }

    /// <summary>The stable operation identifier used in the endpoint name and inventory.</summary>
    public required string Operation { get; init; }

    /// <summary>How the request body is treated. Null defaults by HTTP method.</summary>
    public EndpointBodyMode? BodyMode { get; init; }

    /// <summary>Content types the request is accepted as. Also decides whether a request schema is documented.</summary>
    public string[]? Accepts { get; init; }

    /// <summary>The success response body type. Null means no body.</summary>
    public Type? ResponseType { get; init; }

    /// <summary>The status written at runtime on success.</summary>
    public int SuccessStatus { get; init; } = StatusCodes.Status200OK;

    /// <summary>
    /// The status the OpenAPI document declares, when it deliberately differs from the runtime
    /// status. Null documents <see cref="SuccessStatus"/>.
    /// </summary>
    public int? DocumentedStatus { get; init; }

    /// <summary>
    /// Forces the documented 401/403 pair on or off. Null infers it from the endpoint's completed
    /// authorization metadata, which is correct unless authorization is applied by a middleware
    /// fallback policy that endpoint metadata cannot see.
    /// </summary>
    public bool? DocumentAuthResponses { get; init; }

    /// <summary>
    /// Rejects a typed route or query value that does not parse, with a 400 naming it, rather than
    /// falling back to the parameter's default.
    /// </summary>
    /// <remarks>
    /// Opt-in. The lenient fallback is what most published contracts already do, so turning this on
    /// can change an existing API's responses. For a new endpoint it is the better setting: a value
    /// the caller sent and the binder could not read is worth reporting.
    /// </remarks>
    public bool StrictTypedParsing { get; init; }
}

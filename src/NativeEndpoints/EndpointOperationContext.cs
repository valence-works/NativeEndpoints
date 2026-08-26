using Microsoft.AspNetCore.Builder;

namespace NativeEndpoints;

/// <summary>Everything known about an operation when its endpoint metadata is applied.</summary>
/// <remarks>
/// Passed to the group's <see cref="EndpointOperationConvention"/> after the route is mapped and
/// before any endpoint-declared conventions run. A host replaces the convention to control endpoint
/// naming, tagging, and documented responses without reaching into the mapping pipeline.
/// </remarks>
public sealed record EndpointOperationContext
{
    private readonly string? _tag;

    /// <summary>The name of the group the endpoint was mapped in.</summary>
    public required string GroupName { get; init; }

    /// <summary>
    /// The tag the group publishes under. Defaults to <see cref="GroupName"/>.
    /// </summary>
    /// <remarks>
    /// Separate from the group name because the two answer different questions: the name keeps
    /// endpoint identifiers unique across a host, while the tag is how a document groups operations
    /// for a reader. Several groups can legitimately share one tag while keeping distinct names.
    /// </remarks>
    public string Tag
    {
        get => _tag ?? GroupName;
        init => _tag = value;
    }

    /// <summary>The stable operation identifier, declared or derived.</summary>
    public required string Operation { get; init; }

    /// <summary>
    /// The literal endpoint name the operation declared, when it could not be derived from
    /// <see cref="Operation"/>. Null means the convention derives the name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>The HTTP method the route was mapped for.</summary>
    public required string Method { get; init; }

    /// <summary>The route pattern, including any group prefix.</summary>
    public required string Pattern { get; init; }

    /// <summary>The request contract, when the operation documents one.</summary>
    public Type? RequestType { get; init; }

    /// <summary>
    /// The contract the operation binds, whether or not it is documented as a request body.
    /// </summary>
    /// <remarks>
    /// A GET binds a contract from the route and query without declaring a request body, so
    /// <see cref="RequestType"/> is null while this is not. Parameter descriptions come from here.
    /// </remarks>
    public Type? ContractType { get; init; }

    /// <summary>Whether the operation reads a JSON body, so body members are not repeated as parameters.</summary>
    public bool ReadsBody { get; init; }

    /// <summary>What the body is read as, which decides whether members are documented as form fields.</summary>
    public EndpointBodyKind BodyKind { get; init; } = EndpointBodyKind.Json;

    /// <summary>The success response body type. Null means no body.</summary>
    public Type? ResponseType { get; init; }

    /// <summary>
    /// The content type the success response is documented as. Null documents JSON.
    /// </summary>
    public string? SuccessContentType { get; init; }

    /// <summary>Content types the request body is accepted as.</summary>
    public string[]? Accepts { get; init; }

    /// <summary>The status written at runtime on success.</summary>
    public required int SuccessStatus { get; init; }

    /// <summary>The status the document declares, where it deliberately differs from the runtime one.</summary>
    public required int DocumentedStatus { get; init; }

    /// <summary>
    /// Forces the 401/403 pair on or off. Null infers it from the completed authorization metadata,
    /// which is correct unless authorization is applied by a middleware fallback policy that
    /// endpoint metadata cannot see.
    /// </summary>
    public bool? DocumentAuthResponses { get; init; }
}

/// <summary>Applies a host's endpoint metadata for one mapped operation.</summary>
public delegate void EndpointOperationConvention(IEndpointConventionBuilder builder, EndpointOperationContext context);

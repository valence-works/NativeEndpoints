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

    /// <summary>
    /// The literal endpoint name, for an operation whose identifier cannot be derived from
    /// <see cref="Operation"/>. Null derives it, which is what a new endpoint should do.
    /// </summary>
    /// <remarks>
    /// This exists for operation identifiers that were published before a host's naming scheme and
    /// are frozen in a document clients already generate from. Deriving is otherwise better: it keeps
    /// one operation per folder and needs no <c>Configure</c> override at all.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>How the request body is treated. Null defaults by HTTP method.</summary>
    public EndpointBodyMode? BodyMode { get; init; }

    /// <summary>What the request body is read as. Orthogonal to <see cref="BodyMode"/>.</summary>
    public EndpointBodyKind BodyKind { get; init; } = EndpointBodyKind.Json;

    /// <summary>
    /// Whether the endpoint requires an antiforgery token. No default for a form body: one must be
    /// declared, or mapping throws.
    /// </summary>
    public bool? RequireAntiforgery { get; init; }

    /// <summary>Content types the request is accepted as. Also decides whether a request schema is documented.</summary>
    public string[]? Accepts { get; init; }

    /// <summary>The success response body type. Null means no body.</summary>
    public Type? ResponseType { get; init; }

    /// <summary>
    /// The content type the success response is documented as. Null documents JSON.
    /// </summary>
    /// <remarks>
    /// The documented type, not the written one: an operation that owns its own response — a
    /// server-sent event stream, a rendered page — writes its content type itself, and this is how
    /// the document says so.
    /// </remarks>
    public string? SuccessContentType { get; init; }

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
    /// Rejects a typed route, query, header, or claim value that does not parse, with a 400 naming
    /// it, rather than falling back to the parameter's default.
    /// </summary>
    /// <remarks>
    /// Opt-in. The lenient fallback is what most published contracts already do, so turning this on
    /// can change an existing API's responses. For a new endpoint it is the better setting: a value
    /// the caller sent and the binder could not read is worth reporting.
    /// </remarks>
    public bool StrictTypedParsing { get; init; }

    /// <summary>
    /// Whether an unhandled exception is answered by the group's failure path. False lets it
    /// propagate to the host's exception pipeline instead.
    /// </summary>
    /// <remarks>
    /// Containment is the right default: the group owns the operation's failure contract, so it
    /// answers faults with the problem shape the operation documents. But an owner whose published
    /// contract makes the host's pipeline responsible for unexpected failures — one already running
    /// its own exception middleware, or serving a UI whose error page is not a problem document —
    /// needs the exception to reach it unswallowed. Only the response-owning paths honour this;
    /// a bound operation always contains, because its failure translation is what turns a domain
    /// exception into the documented status.
    /// </remarks>
    public bool ContainFailures { get; init; } = true;
}

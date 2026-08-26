using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace NativeEndpoints;

/// <summary>
/// A self-describing HTTP endpoint: one class carrying its route, verb, metadata, and handling.
/// </summary>
/// <remarks>
/// The class declares its route and verb with an attribute such as <c>[Post("definitions")]</c> and
/// refines anything dynamic — body mode, status codes, permissions — by overriding
/// <see cref="Configure"/>. Registration is explicit and module-local: the owning module maps its
/// endpoint classes from its own assembly at composition time. There is no process-global discovery
/// and no static registry, which is what made previous endpoint frameworks unloadable; scanning a
/// module's own assembly inside its own mapping call retains nothing beyond the endpoint generation.
/// <para>
/// <see cref="Configure"/> is invoked once at mapping time on an uninitialized instance, so it must
/// not touch constructor-injected state. Handling instances are created per request from the request
/// services, so constructor injection works as it does for any scoped service.
/// </para>
/// </remarks>
public abstract class ApiEndpointBase
{
    /// <summary>The current request. Set before the handler runs; not available in <see cref="Configure"/>.</summary>
    public HttpContext HttpContext { get; set; } = null!;

    /// <summary>Refines the endpoint definition beyond what its attributes declare.</summary>
    public virtual void Configure(ApiEndpointOptions options)
    {
    }
}

/// <summary>An endpoint that binds a request contract and returns a JSON response body.</summary>
public abstract class ApiEndpoint<TRequest, TResponse> : ApiEndpointBase
    where TResponse : notnull
{
    /// <summary>Handles the bound request and returns the response body.</summary>
    public abstract Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>An endpoint that binds a request contract and returns no content.</summary>
public abstract class ApiEndpoint<TRequest> : ApiEndpointBase
{
    /// <summary>Handles the bound request. The response is 204 No Content.</summary>
    public abstract Task HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>An endpoint that takes no request contract and returns a JSON response body.</summary>
public abstract class ApiEndpointWithoutRequest<TResponse> : ApiEndpointBase
    where TResponse : notnull
{
    /// <summary>Handles the request and returns the response body.</summary>
    public abstract Task<TResponse> HandleAsync(CancellationToken cancellationToken);
}

/// <summary>An endpoint that binds a request contract and picks the response status per result.</summary>
/// <remarks>
/// For operations whose status code is decided by the outcome rather than fixed by the route. The
/// handler stays a pure function: it returns the response paired with its status, and the mapper
/// writes it with the owning module's source-generated serializer metadata.
/// </remarks>
public abstract class ApiEndpointWithResult<TRequest, TResponse> : ApiEndpointBase
    where TResponse : notnull
{
    /// <summary>Handles the bound request and returns the response body with its status code.</summary>
    public abstract Task<EndpointResult<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>An endpoint that writes its own response through <see cref="ApiEndpointBase.HttpContext"/>.</summary>
/// <remarks>
/// For responses that are not JSON at all: a file stream, a redirect, an event stream. Nothing is
/// bound and nothing is written on success — the pipeline sets
/// <see cref="ApiEndpointBase.HttpContext"/> before dispatch and the handler owns the response from
/// there, including its status code and content type. The shared failure path still applies when the
/// handler throws: module-owned fault renderers first, then exception translators, then the
/// sanitized generic 500, exactly as for the contract-bound shapes.
/// </remarks>
public abstract class ApiEndpoint : ApiEndpointBase
{
    /// <summary>Handles the request, writing the response through <see cref="ApiEndpointBase.HttpContext"/>.</summary>
    public abstract Task HandleAsync(CancellationToken cancellationToken);
}

/// <summary>The endpoint definition an <see cref="ApiEndpointBase.Configure"/> override refines.</summary>
public sealed class ApiEndpointOptions
{
    private readonly List<Action<IEndpointConventionBuilder>> _conventions = [];

    /// <summary>The HTTP method. Set by the route attribute unless overridden.</summary>
    public string? Method { get; set; }

    /// <summary>The route pattern, relative to any prefix the group was mapped with.</summary>
    public string? Route { get; set; }

    /// <summary>The stable operation identifier used in the endpoint name and inventory.</summary>
    public string? Operation { get; set; }

    /// <summary>Content types the request is accepted as. Also decides whether a request schema is documented.</summary>
    public string[]? Accepts { get; set; }

    /// <summary>How the request body is treated. Defaults by HTTP method.</summary>
    public EndpointBodyMode? BodyMode { get; set; }

    /// <summary>
    /// Rejects a typed route or query value that does not parse, with a 400 naming it, rather than
    /// falling back to the parameter's default.
    /// </summary>
    /// <remarks>
    /// Opt-in. The lenient fallback is what most published contracts already do, so turning this on
    /// can change an existing API's responses. For a new endpoint it is the better setting: a value
    /// the caller sent and the binder could not read is worth reporting.
    /// </remarks>
    public bool StrictTypedParsing { get; set; }

    /// <summary>The status written on success.</summary>
    public int SuccessStatus { get; set; } = StatusCodes.Status200OK;

    /// <summary>The status the OpenAPI document declares, when it deliberately differs from the runtime status.</summary>
    public int? DocumentedStatus { get; set; }

    /// <summary>
    /// Forces the documented 401/403 pair on or off. Null infers it from the endpoint's completed
    /// authorization metadata, which is correct unless authorization is applied by a middleware
    /// fallback policy that endpoint metadata cannot see.
    /// </summary>
    public bool? DocumentAuthResponses { get; set; }

    /// <summary>Registers an ordinary ASP.NET Core convention to apply to the mapped endpoint.</summary>
    public ApiEndpointOptions Convention(Action<IEndpointConventionBuilder> convention)
    {
        ArgumentNullException.ThrowIfNull(convention);
        _conventions.Add(convention);
        return this;
    }

    /// <summary>The conventions registered by <see cref="Convention"/>, applied by the mapper after mapping.</summary>
    public IReadOnlyList<Action<IEndpointConventionBuilder>> Conventions => _conventions;
}

/// <summary>Declares the route and verb of an <see cref="ApiEndpointBase"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class EndpointRouteAttribute(string method, string route) : Attribute
{
    /// <summary>The HTTP method the endpoint is mapped for.</summary>
    public string Method { get; } = method;

    /// <summary>The declared route pattern.</summary>
    public string Route { get; } = route;
}

/// <summary>Maps the endpoint to an HTTP GET at <paramref name="route"/>.</summary>
public sealed class GetAttribute(string route) : EndpointRouteAttribute("GET", route);
/// <summary>Maps the endpoint to an HTTP POST at <paramref name="route"/>.</summary>
public sealed class PostAttribute(string route) : EndpointRouteAttribute("POST", route);
/// <summary>Maps the endpoint to an HTTP PUT at <paramref name="route"/>.</summary>
public sealed class PutAttribute(string route) : EndpointRouteAttribute("PUT", route);
/// <summary>Maps the endpoint to an HTTP PATCH at <paramref name="route"/>.</summary>
public sealed class PatchAttribute(string route) : EndpointRouteAttribute("PATCH", route);
/// <summary>Maps the endpoint to an HTTP DELETE at <paramref name="route"/>.</summary>
public sealed class DeleteAttribute(string route) : EndpointRouteAttribute("DELETE", route);

/// <summary>
/// An attribute that applies endpoint metadata when its endpoint class is mapped. Lets other layers
/// contribute class-level attributes — such as a permission requirement — without this project
/// referencing them.
/// </summary>
public interface IEndpointConventionAttribute
{
    /// <summary>Applies this attribute's metadata to the mapped endpoint.</summary>
    void Apply(IEndpointConventionBuilder builder);
}

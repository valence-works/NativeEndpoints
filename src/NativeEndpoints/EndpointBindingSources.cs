namespace NativeEndpoints;

/// <summary>Where a contract member is bound from.</summary>
/// <remarks>
/// The default precedence is route, then body, then query. These attributes override it for one
/// member, and are the only way to reach a header or a claim, neither of which participates in the
/// default order: reading them implicitly would make an unrelated header silently populate a
/// parameter that happened to share its name.
/// <para>
/// A form is not a fifth step in that order. A form <em>is</em> the body, for a request whose content
/// type says so, and it therefore occupies the body's place. <see cref="FromFormAttribute"/> is an
/// override in the same class as <see cref="FromQueryAttribute"/>, not a way to reach something
/// otherwise unreachable.
/// </para>
/// </remarks>
public enum EndpointBindingSource
{
    /// <summary>Route values only.</summary>
    Route,

    /// <summary>The query string only.</summary>
    Query,

    /// <summary>A request header.</summary>
    Header,

    /// <summary>A claim on the authenticated principal.</summary>
    Claim,

    /// <summary>A field of a URL-encoded or multipart form body.</summary>
    Form
}

/// <summary>Declares which source a contract member binds from.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public abstract class BindFromAttribute(EndpointBindingSource source, string? name) : Attribute
{
    /// <summary>The source to read.</summary>
    public EndpointBindingSource Source { get; } = source;

    /// <summary>The key to read, when it differs from the member's own name.</summary>
    public string? Name { get; } = name;
}

/// <summary>Binds from a route value.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromRouteAttribute(string? name = null) : BindFromAttribute(EndpointBindingSource.Route, name);

/// <summary>Binds from the query string. Arrays and lists collect every value for the key.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromQueryAttribute(string? name = null) : BindFromAttribute(EndpointBindingSource.Query, name);

/// <summary>Binds from a request header, for example <c>[FromHeader("X-Tenant")]</c>.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromHeaderAttribute(string? name = null) : BindFromAttribute(EndpointBindingSource.Header, name);

/// <summary>Binds from a claim on the authenticated principal, for example <c>[FromClaim("sub")]</c>.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromClaimAttribute(string? name = null) : BindFromAttribute(EndpointBindingSource.Claim, name);

/// <summary>
/// Binds from a form field. Arrays and lists collect every value for the key.
/// </summary>
/// <remarks>
/// Rarely needed: on a form endpoint an unattributed member already binds from the form. This is for
/// the two cases inference cannot express — a field whose wire name differs from the member's
/// (<c>[FromForm("legacy_name")]</c>), and a field shadowed by a route value of the same name, which
/// route precedence would otherwise win outright.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class FromFormAttribute(string? name = null) : BindFromAttribute(EndpointBindingSource.Form, name);

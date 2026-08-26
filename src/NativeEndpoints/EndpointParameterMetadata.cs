using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NativeEndpoints;

/// <summary>One request value an operation reads, and where it reads it from.</summary>
/// <remarks>
/// Plain metadata: strings, a type, and two flags. The core library only states these facts, because
/// turning them into an OpenAPI document would mean taking a dependency on the OpenAPI packages, and
/// a consumer who never generates a document should not pay for one. The optional
/// <c>NativeEndpoints.OpenApi</c> package reads this and writes the parameters.
/// </remarks>
public sealed record EndpointParameterMetadata(
    string Name,
    EndpointBindingSource Source,
    Type Type,
    bool Required);

/// <summary>Derives an operation's parameters from its request contract and route pattern.</summary>
/// <remarks>
/// Publishing a bare <see cref="Microsoft.AspNetCore.Http.RequestDelegate"/> is what keeps endpoint
/// assemblies collectible, but it also leaves API Explorer nothing to infer parameters from. This
/// recovers them by applying the binder's own rules to the contract's shape, so the document
/// describes what the binder will actually read.
/// </remarks>
public static partial class EndpointParameterDescriber
{
    /// <summary>Describes every route, query, header, and claim value the contract binds.</summary>
    /// <param name="contract">The request contract, or null when the operation binds nothing.</param>
    /// <param name="pattern">The route pattern, used to tell route values from query values.</param>
    /// <param name="bodyKind">
    /// What the body is read as. Form members are described with
    /// <see cref="EndpointBindingSource.Form"/> rather than suppressed, because OpenAPI renders them
    /// as a multipart request-body schema and something has to state which members those are.
    /// </param>
    /// <param name="readsBody">
    /// Whether the operation reads a JSON body. Members that come from the body are described by the
    /// request schema instead, so they are not repeated as parameters.
    /// </param>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "The contract is a generic argument of the endpoint being mapped, so it is referenced by the code that reaches here and cannot have been trimmed away.")]
    public static IReadOnlyList<EndpointParameterMetadata> Describe(
        Type? contract,
        string pattern,
        bool readsBody,
        EndpointBodyKind bodyKind = EndpointBodyKind.Json)
    {
        if (contract is null)
            return [];

        var routeKeys = RouteParameters(pattern);
        var described = new List<EndpointParameterMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, type, attribute, optional) in Members(contract))
        {
            if (!seen.Add(name))
                continue;

            if (attribute is not null)
            {
                var key = attribute.Name
                          ?? (attribute.Source == EndpointBindingSource.Route && routeKeys.TryGetValue(name, out var declaredRoute)
                              ? declaredRoute
                              : name);
                described.Add(new EndpointParameterMetadata(
                    key, attribute.Source, type,
                    attribute.Source == EndpointBindingSource.Route || (!optional && !IsNullable(type))));
                continue;
            }

            if (routeKeys.TryGetValue(name, out var routeKey))
            {
                // Use the template's own casing. A path parameter whose name does not match the
                // template exactly is invalid OpenAPI, and generated clients will not bind it.
                described.Add(new EndpointParameterMetadata(routeKey, EndpointBindingSource.Route, type, Required: true));
                continue;
            }

            // On a form endpoint the body *is* a set of named fields, so an unattributed member is a
            // form field rather than something the request schema will describe on its own.
            if (readsBody && bodyKind is EndpointBodyKind.Form)
            {
                described.Add(new EndpointParameterMetadata(name, EndpointBindingSource.Form, type, !optional && !IsNullable(type)));
                continue;
            }

            // Anything not in the route and not in the body is read from the query string.
            if (!readsBody)
                described.Add(new EndpointParameterMetadata(name, EndpointBindingSource.Query, type, !optional && !IsNullable(type)));
        }

        return described;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "The contract is a generic argument of the endpoint being mapped, so it is referenced by the code that reaches here and cannot have been trimmed away.")]
    private static IEnumerable<(string Name, Type Type, BindFromAttribute? Attribute, bool Optional)> Members(Type contract)
    {
        var constructors = contract.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length == 1)
        {
            foreach (var parameter in constructors[0].GetParameters())
            {
                var attribute = parameter.GetCustomAttribute<BindFromAttribute>()
                                ?? contract.GetProperty(parameter.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                    ?.GetCustomAttribute<BindFromAttribute>();
                yield return (parameter.Name!, parameter.ParameterType, attribute, parameter.HasDefaultValue);
            }

            yield break;
        }

        foreach (var property in contract.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(item => item.CanWrite))
            yield return (property.Name, property.PropertyType, property.GetCustomAttribute<BindFromAttribute>(), false);
    }

    /// <summary>Route parameter names, keyed case-insensitively but preserving the template's casing.</summary>
    private static Dictionary<string, string> RouteParameters(string pattern)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in RoutePattern().Matches(pattern))
            keys.TryAdd(match.Groups[1].Value, match.Groups[1].Value);

        return keys;
    }

    private static bool IsNullable(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    // Captures the name out of {id}, {id:guid}, {id?}, {*rest}.
    [GeneratedRegex(@"\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)[^}]*\}")]
    private static partial Regex RoutePattern();
}

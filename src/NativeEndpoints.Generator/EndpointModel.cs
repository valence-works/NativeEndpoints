using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace NativeEndpoints.Generator;

/// <summary>One endpoint class, reduced to what the generator needs to emit a registration.</summary>
internal sealed record EndpointModel(
    string QualifiedName,
    string DisplayName,
    string? RequestType,
    string? ResponseType,
    EndpointShape Shape,
    bool HasRoute,
    string? HttpMethod,
    string? RoutePattern,
    ImmutableArray<ContractParameter> Contract,
    ImmutableArray<string> Dependencies,
    ImmutableArray<string> RouteKeys,
    int PublicConstructorCount,
    string? ContractName,
    string Operation,
    bool Generatable);

/// <summary>One contract member, and everything the emitter needs to read it without reflection.</summary>
internal sealed record ContractParameter(
    string Name,
    string TypeName,
    bool Bindable,
    string Converter,
    string? ElementConverter,
    string? ElementTypeName,
    bool IsArray,
    bool IsList,
    string? DeclaredSource,
    string? DeclaredKey,
    bool SuppressNull);

/// <summary>Which base an endpoint derives from, and therefore how it is mapped.</summary>
internal enum EndpointShape
{
    /// <summary>ApiEndpoint&lt;TRequest, TResponse&gt;</summary>
    RequestResponse,

    /// <summary>ApiEndpoint&lt;TRequest&gt;, 204 No Content</summary>
    RequestOnly,

    /// <summary>ApiEndpointWithoutRequest&lt;TResponse&gt;</summary>
    ResponseOnly,

    /// <summary>ApiEndpointWithResult&lt;TRequest, TResponse&gt;</summary>
    RequestResult,

    /// <summary>ApiEndpointBase, writing its own response. The generator cannot map these.</summary>
    Unsupported
}

internal static class EndpointSymbols
{
    internal const string BaseName = "NativeEndpoints.ApiEndpointBase";

    private static readonly HashSet<string> RouteAttributes = new()
    {
        "NativeEndpoints.GetAttribute",
        "NativeEndpoints.PostAttribute",
        "NativeEndpoints.PutAttribute",
        "NativeEndpoints.PatchAttribute",
        "NativeEndpoints.DeleteAttribute",
        "NativeEndpoints.EndpointRouteAttribute"
    };

    internal static bool DerivesFromEndpointBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == BaseName)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The operation identifier, derived exactly as the runtime mapper derives it.
    /// </summary>
    /// <remarks>
    /// Segments after an <c>Endpoints</c> namespace segment, concatenated; otherwise the class name,
    /// falling back to the last namespace segment. Divergence here would rename endpoints when a
    /// project turned the generator on, so the two rules have to stay identical.
    /// </remarks>
    internal static string Operation(INamedTypeSymbol type)
    {
        var segments = (type.ContainingNamespace?.ToDisplayString() ?? string.Empty)
            .Split(new[] { '.' }, System.StringSplitOptions.RemoveEmptyEntries);

        var marker = System.Array.LastIndexOf(segments, "Endpoints");
        if (marker >= 0 && marker < segments.Length - 1)
            return string.Concat(segments.Skip(marker + 1));

        if (type.Name != "Endpoint")
            return type.Name;

        return segments.Length > 0 ? segments[segments.Length - 1] : type.Name;
    }

    /// <summary>The route the attribute declares, or null when there is none.</summary>
    internal static string? RoutePattern(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            for (var current = attribute.AttributeClass; current is not null; current = current.BaseType)
            {
                if (current.ToDisplayString() != "NativeEndpoints.EndpointRouteAttribute")
                    continue;

                // [Get("x")] passes one argument; the base [EndpointRoute("GET","x")] passes two.
                var arguments = attribute.ConstructorArguments;
                return arguments.Length switch
                {
                    1 => arguments[0].Value as string,
                    2 => arguments[1].Value as string,
                    _ => null
                };
            }
        }

        return null;
    }

    /// <summary>The endpoint's own constructor dependencies, for a generated activator.</summary>
    internal static ImmutableArray<string> Dependencies(INamedTypeSymbol type)
    {
        var constructors = type.InstanceConstructors
            .Where(item => item.DeclaredAccessibility == Accessibility.Public)
            .ToArray();

        return constructors.Length == 1
            ? constructors[0].Parameters
                .Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .ToImmutableArray()
            : ImmutableArray<string>.Empty;
    }

    /// <summary>Whether the endpoint has exactly one public constructor, so activation is unambiguous.</summary>
    internal static bool HasSingleConstructor(INamedTypeSymbol type) =>
        type.InstanceConstructors.Count(item => item.DeclaredAccessibility == Accessibility.Public) == 1;

    /// <summary>The HTTP method the route attribute declares, or null when there is none.</summary>
    internal static string? HttpMethod(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            switch (attribute.AttributeClass?.ToDisplayString())
            {
                case "NativeEndpoints.GetAttribute": return "GET";
                case "NativeEndpoints.PostAttribute": return "POST";
                case "NativeEndpoints.PutAttribute": return "PUT";
                case "NativeEndpoints.PatchAttribute": return "PATCH";
                case "NativeEndpoints.DeleteAttribute": return "DELETE";
            }
        }

        return null;
    }

    private static readonly HashSet<string> NativelyBindable = new()
    {
        "string", "bool", "int", "long", "System.Guid", "System.DateTimeOffset", "System.DateTime"
    };

    /// <summary>The EndpointValue call that converts a raw string into this type.</summary>
    internal static string? Converter(ITypeSymbol type)
    {
        var nullable = type is INamedTypeSymbol { IsGenericType: true } candidate &&
                       candidate.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;
        var underlying = nullable ? ((INamedTypeSymbol)type).TypeArguments[0] : type;
        var prefix = nullable ? "Nullable" : string.Empty;

        switch (underlying.ToDisplayString())
        {
            case "string": return "String";
            case "bool": return prefix + "Boolean";
            case "int": return prefix + "Int32";
            case "long": return prefix + "Int64";
            case "System.Guid": return prefix + "Guid";
            case "System.DateTimeOffset": return prefix + "DateTimeOffset";
        }

        var qualified = underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (underlying.TypeKind == TypeKind.Enum)
            return $"{prefix}Enum<{qualified}>";

        if (underlying.AllInterfaces.Any(item => item.ConstructedFrom.ToDisplayString() == "System.IParsable<TSelf>"))
            return nullable ? $"NullableParsable<{qualified}>" : $"Parsable<{qualified}>";

        return null;
    }

    /// <summary>The element type when this is a bindable collection shape.</summary>
    internal static (ITypeSymbol? Element, bool IsArray, bool IsList) Collection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return (array.ElementType, true, false);

        if (type is INamedTypeSymbol { IsGenericType: true } generic &&
            generic.ConstructedFrom.ToDisplayString() is "System.Collections.Generic.List<T>"
                or "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.IList<T>"
                or "System.Collections.Generic.IEnumerable<T>" or "System.Collections.Generic.ICollection<T>")
        {
            return (generic.TypeArguments[0], false, true);
        }

        return (null, false, false);
    }

    /// <summary>Whether the binder can produce this type from a single request string.</summary>
    internal static bool IsBindable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true } nullable &&
            nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            return IsBindable(nullable.TypeArguments[0]);
        }

        if (type is IArrayTypeSymbol array)
            return IsBindable(array.ElementType);

        if (type is INamedTypeSymbol { IsGenericType: true } collection &&
            collection.ConstructedFrom.ToDisplayString() is "System.Collections.Generic.List<T>"
                or "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.IList<T>"
                or "System.Collections.Generic.IEnumerable<T>" or "System.Collections.Generic.ICollection<T>")
        {
            return IsBindable(collection.TypeArguments[0]);
        }

        if (type.TypeKind == TypeKind.Enum)
            return true;

        if (NativelyBindable.Contains(type.ToDisplayString()))
            return true;

        // IParsable<TSelf> binds with no registration. A registered value binder also works, but the
        // generator cannot see runtime registrations, which is why these are warnings not errors.
        return type.AllInterfaces.Any(candidate =>
            candidate.ConstructedFrom.ToDisplayString() == "System.IParsable<TSelf>");
    }

    internal static bool HasRouteAttribute(INamedTypeSymbol type) =>
        type.GetAttributes().Any(attribute =>
        {
            for (var current = attribute.AttributeClass; current is not null; current = current.BaseType)
            {
                if (RouteAttributes.Contains(current.ToDisplayString()))
                    return true;
            }

            return false;
        });

    /// <summary>Walks to the generic base that decides the endpoint's shape.</summary>
    internal static (EndpointShape Shape, string? Request, string? Response) Shape(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.TypeArguments.Length == 0)
                continue;

            var definition = current.ConstructedFrom.ToDisplayString();
            var arguments = current.TypeArguments
                .Select(argument => argument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .ToArray();

            switch (definition)
            {
                case "NativeEndpoints.ApiEndpoint<TRequest, TResponse>":
                    return (EndpointShape.RequestResponse, arguments[0], arguments[1]);
                case "NativeEndpoints.ApiEndpoint<TRequest>":
                    return (EndpointShape.RequestOnly, arguments[0], null);
                case "NativeEndpoints.ApiEndpointWithoutRequest<TResponse>":
                    return (EndpointShape.ResponseOnly, null, arguments[0]);
                case "NativeEndpoints.ApiEndpointWithResult<TRequest, TResponse>":
                    return (EndpointShape.RequestResult, arguments[0], arguments[1]);
            }
        }

        return (EndpointShape.Unsupported, null, null);
    }
}

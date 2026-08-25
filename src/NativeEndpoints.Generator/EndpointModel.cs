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
    ImmutableArray<ContractParameter> Contract,
    int PublicConstructorCount,
    string? ContractName);

/// <summary>One contract member, and whether the binder can produce its type from a string.</summary>
internal sealed record ContractParameter(string Name, string TypeName, bool Bindable);

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

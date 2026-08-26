using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace NativeEndpoints.Generator;

/// <summary>One endpoint class, reduced to what the generator needs to emit a registration.</summary>
/// <remarks>
/// Every member compares by value — collections through <see cref="EquatableArray{T}"/>, because
/// record equality over <see cref="ImmutableArray{T}"/> is reference equality — so the incremental
/// pipeline can recognise an unchanged endpoint and skip regeneration.
/// </remarks>
internal sealed record EndpointModel(
    string QualifiedName,
    string DisplayName,
    string? RequestType,
    string? ResponseType,
    EndpointShape Shape,
    bool HasRoute,
    string? HttpMethod,
    string? RoutePattern,
    EquatableArray<ContractParameter> Contract,
    EquatableArray<string> Dependencies,
    EquatableArray<string> RouteKeys,
    int PublicConstructorCount,
    string? ContractName,
    string Operation,
    bool Generatable,
    EquatableArray<ConfigureRead> ConfigureReads);

/// <summary>A piece of instance state that Configure reads, and where it reads it.</summary>
/// <remarks>
/// Holds the location's raw data rather than a <see cref="Microsoft.CodeAnalysis.Location"/>: a
/// Location references its syntax tree and changes identity on every edit, which would poison the
/// pipeline cache. The pieces here are value-equatable, and <see cref="Location"/> rebuilds the
/// same file/line/column for the diagnostic at report time.
/// </remarks>
internal sealed record ConfigureRead(
    string Member,
    string FilePath,
    Microsoft.CodeAnalysis.Text.TextSpan Span,
    Microsoft.CodeAnalysis.Text.LinePositionSpan LineSpan)
{
    /// <summary>Rebuilds the report location from the captured data.</summary>
    internal Location Location => Location.Create(FilePath, Span, LineSpan);
}

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
    bool SuppressNull,
    bool HasDefaultValue,
    FormFileKind FormFile);

/// <summary>The file shape a contract member has, if any.</summary>
/// <remarks>
/// Carried explicitly rather than smuggled through <see cref="ContractParameter.Converter"/>, because
/// a file is not converted from a string at all and every string-shaped path would have to special
/// case it.
/// </remarks>
internal enum FormFileKind
{
    /// <summary>Not a file.</summary>
    None,

    /// <summary>IFormFile: the first file under the member's own name.</summary>
    Single,

    /// <summary>An array or list of IFormFile: every file under the member's own name.</summary>
    Many,

    /// <summary>IFormFileCollection: every file in the request, whatever name it arrived under.</summary>
    All
}

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

    /// <summary>ApiEndpoint, non-generic: the handler writes the response itself.</summary>
    Raw,

    /// <summary>ApiEndpointBase directly, which no mapper can dispatch. Reported as NE0005 and excluded.</summary>
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

    private const string FormFileName = "Microsoft.AspNetCore.Http.IFormFile";
    private const string FormFileCollectionName = "Microsoft.AspNetCore.Http.IFormFileCollection";

    /// <summary>The declared name without a nullable-reference annotation, which "IFormFile?" carries.</summary>
    private static string Unannotated(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString();

    /// <summary>The file shape of a member, detected by name so the generator takes no new reference.</summary>
    internal static FormFileKind FormFile(ITypeSymbol type)
    {
        switch (Unannotated(type))
        {
            case FormFileName: return FormFileKind.Single;
            case FormFileCollectionName: return FormFileKind.All;
        }

        var (element, _, _) = Collection(type);
        return element is not null && Unannotated(element) == FormFileName
            ? FormFileKind.Many
            : FormFileKind.None;
    }

    /// <summary>The EndpointValue call that converts a raw string into this type.</summary>
    internal static string? Converter(ITypeSymbol type)
    {
        var nullableValue = type is INamedTypeSymbol { IsGenericType: true } candidate &&
                            candidate.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

        // A nullable-ANNOTATED reference type ("Phone?") is not Nullable<T>: the annotation only
        // decorates the symbol and its display string. Strip it so "string?" still matches the
        // string case below, and remember it so a nullable reference IParsable maps to the
        // converter that treats absence as null — Parsable<T> rejects absence under strict
        // parsing, which would 400 a member the docs promise "is simply null" when omitted.
        var nullableReference = !nullableValue &&
            type is { IsReferenceType: true, NullableAnnotation: NullableAnnotation.Annotated };

        var underlying = nullableValue ? ((INamedTypeSymbol)type).TypeArguments[0]
            : nullableReference ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            : type;
        var prefix = nullableValue ? "Nullable" : string.Empty;

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
        {
            // A NON-nullable reference IParsable stays Parsable<T>, the known remaining corner:
            // under strict parsing the generated converter rejects absence while the reflective
            // binder (whose RejectsAbsence covers value types only) binds null. Kept deliberately —
            // silently changing the reflective binder's answer would be worse than the asymmetry.
            return nullableValue ? $"NullableParsable<{qualified}>"
                : nullableReference ? $"ParsableOrDefault<{qualified}>"
                : $"Parsable<{qualified}>";
        }

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

        // Checked after the collection recursion above, which has already reduced IFormFile[] to its
        // element, so this arm sees the single and collection shapes alike. A file binds; it simply
        // does not bind from a string, which is why NE0002 must not fire for it.
        if (Unannotated(type) is FormFileName or FormFileCollectionName)
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
            {
                // The raw base is the one non-generic base that is mappable; it derives
                // ApiEndpointBase directly, so no generic base can hide behind it.
                if (current.ToDisplayString() == "NativeEndpoints.ApiEndpoint")
                    return (EndpointShape.Raw, null, null);

                continue;
            }

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

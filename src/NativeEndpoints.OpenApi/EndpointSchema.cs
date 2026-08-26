using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;

namespace NativeEndpoints.OpenApi;

/// <summary>The OpenAPI schema for one bound value.</summary>
internal static class EndpointSchema
{
    /// <summary>
    /// A deliberately small schema for a bound value: enough for a generated client to produce the
    /// right call, and no more.
    /// </summary>
    /// <remarks>
    /// Shared by the parameter transformer and the form request-body transformer. The same member can
    /// be documented as a query parameter on one operation and a multipart field on another, and two
    /// implementations of "what shape is this type" would drift.
    /// </remarks>
    internal static OpenApiSchema Describe(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        // A file is opaque bytes, not a value parsed from a string, and is checked before the
        // collection unwrapping below can mistake IFormFileCollection for a generic sequence.
        if (underlying == typeof(IFormFile))
            return new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };

        if (underlying == typeof(IFormFileCollection))
            return new OpenApiSchema { Type = JsonSchemaType.Array, Items = Describe(typeof(IFormFile)) };

        var element = underlying.IsArray
            ? underlying.GetElementType()
            : underlying.IsGenericType && underlying.GetGenericArguments().Length == 1 && underlying != typeof(string)
                ? underlying.GetGenericArguments()[0]
                : null;

        if (element is not null && underlying != typeof(string))
            return new OpenApiSchema { Type = JsonSchemaType.Array, Items = Describe(element) };

        if (underlying == typeof(bool)) return new OpenApiSchema { Type = JsonSchemaType.Boolean };
        if (underlying == typeof(int)) return new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" };
        if (underlying == typeof(long)) return new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64" };
        if (underlying == typeof(Guid)) return new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
        if (underlying == typeof(DateTimeOffset) || underlying == typeof(DateTime))
            return new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" };
        if (underlying.IsEnum)
        {
            return new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [.. Enum.GetNames(underlying).Select(name => (System.Text.Json.Nodes.JsonNode)name)]
            };
        }

        return new OpenApiSchema { Type = JsonSchemaType.String };
    }
}

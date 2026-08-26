using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NativeEndpoints.OpenApi;

/// <summary>
/// Writes the multipart request-body schema for an operation that binds a form.
/// </summary>
/// <remarks>
/// OpenAPI has no <c>in: form</c>. A form field is not a parameter at all — it belongs in the
/// operation's request body, under a schema keyed by media type. So while
/// <see cref="EndpointParameterTransformer"/> filters form members out of <c>parameters</c>, this
/// puts them where they actually go, and the two together are what make a form endpoint's document
/// describe the call a client has to make.
/// </remarks>
public sealed class EndpointFormRequestBodyTransformer : IOpenApiOperationTransformer
{
    private static readonly string[] FormContentTypes =
        ["multipart/form-data", "application/x-www-form-urlencoded"];

    /// <summary>Replaces the request body with a schema describing the form's fields.</summary>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var fields = metadata
            .OfType<EndpointParameterMetadata>()
            .Where(parameter => parameter.Source is EndpointBindingSource.Form)
            .ToArray();

        if (fields.Length == 0)
            return Task.CompletedTask;

        // The endpoint's own declared content types, so the document agrees with what routing will
        // actually accept rather than with a guess made here.
        var contentTypes = metadata
            .OfType<IAcceptsMetadata>()
            .SelectMany(item => item.ContentTypes)
            .Where(type => FormContentTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (contentTypes.Length == 0)
            contentTypes = FormContentTypes;

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,

            // A schema per media type rather than one shared instance: the document object model is
            // mutable, and a host transformer refining the multipart schema must not silently
            // rewrite the URL-encoded one through a shared reference.
            Content = contentTypes.ToDictionary(
                type => type,
                _ => new OpenApiMediaType { Schema = Schema(fields) },
                StringComparer.OrdinalIgnoreCase)
        };

        return Task.CompletedTask;
    }

    private static OpenApiSchema Schema(EndpointParameterMetadata[] fields) =>
        new()
        {
            Type = JsonSchemaType.Object,
            Properties = fields.ToDictionary(
                field => field.Name,
                field => (IOpenApiSchema)EndpointSchema.Describe(field.Type),
                StringComparer.Ordinal),
            Required = new HashSet<string>(
                fields.Where(field => field.Required).Select(field => field.Name),
                StringComparer.Ordinal)
        };
}

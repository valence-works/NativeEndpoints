using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NativeEndpoints.OpenApi;

/// <summary>
/// Writes the route, query, and header parameters an endpoint binds into its OpenAPI operation.
/// </summary>
/// <remarks>
/// NativeEndpoints publishes handlers as a bare <c>RequestDelegate</c> so that API Explorer never
/// retains a handler's <c>MethodInfo</c>, which is what keeps endpoint assemblies collectible. The
/// cost is that API Explorer has nothing to infer parameters from. The core library states them as
/// <see cref="EndpointParameterMetadata"/> instead, and this turns those into document parameters.
/// <para>
/// Claims are deliberately not written. A claim is not part of the HTTP request surface a caller
/// controls; it is a consequence of the credential they present, and documenting it as an input
/// would invite clients to try to send it.
/// </para>
/// <para>
/// Form fields are not written either, for a different reason: OpenAPI has no <c>in: form</c>. A form
/// field belongs in the operation's request body, which
/// <see cref="EndpointFormRequestBodyTransformer"/> writes. Leaving it to the default arm below would
/// publish it as a query parameter — worse than omitting it, because a generated client would then
/// send it in the URL.
/// </para>
/// </remarks>
public sealed class EndpointParameterTransformer : IOpenApiOperationTransformer
{
    /// <summary>Adds any described parameter the document does not already have.</summary>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var described = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<EndpointParameterMetadata>()
            .Where(parameter => parameter.Source is not (EndpointBindingSource.Claim or EndpointBindingSource.Form))
            .ToArray();

        if (described.Length == 0)
            return Task.CompletedTask;

        operation.Parameters ??= [];
        foreach (var parameter in described)
        {
            // Never overwrite a parameter the document already describes: a host may have added a
            // richer one through its own transformer, and this runs to fill gaps, not to win.
            if (operation.Parameters.Any(existing =>
                    string.Equals(existing.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.Name,
                In = parameter.Source switch
                {
                    EndpointBindingSource.Route => ParameterLocation.Path,
                    EndpointBindingSource.Header => ParameterLocation.Header,
                    _ => ParameterLocation.Query
                },
                Required = parameter.Required || parameter.Source is EndpointBindingSource.Route,
                Schema = EndpointSchema.Describe(parameter.Type)
            });
        }

        return Task.CompletedTask;
    }
}

using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace NativeEndpoints.OpenApi;

/// <summary>Registers the NativeEndpoints OpenAPI integration.</summary>
public static class NativeEndpointsOpenApiExtensions
{
    /// <summary>
    /// Documents the parameters NativeEndpoints operations bind, and the form fields they accept.
    /// Call alongside <c>AddOpenApi</c>.
    /// </summary>
    public static IServiceCollection AddNativeEndpointsOpenApi(this IServiceCollection services, string documentName = "v1")
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Configure<OpenApiOptions>(documentName, options =>
        {
            options.AddOperationTransformer<EndpointParameterTransformer>();
            options.AddOperationTransformer<EndpointFormRequestBodyTransformer>();
        });
    }
}

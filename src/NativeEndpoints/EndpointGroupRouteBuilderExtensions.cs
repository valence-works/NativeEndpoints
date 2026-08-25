using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NativeEndpoints;

/// <summary>Entry point for mapping a group of endpoints.</summary>
public static class EndpointGroupRouteBuilderExtensions
{
    /// <summary>Opens a mapping group.</summary>
    /// <param name="endpoints">The standard route builder.</param>
    /// <param name="name">
    /// Names the group. Prefixes endpoint names so they stay unique across a host, supplies the
    /// default OpenAPI tag, and identifies endpoints in a lifetime violation report. Defaults to the
    /// calling assembly's simple name.
    /// </param>
    /// <param name="jsonContext">
    /// A source-generated serializer context governing both binding and writing. Optional: without
    /// one the host's configured <see cref="JsonOptions"/> are used, which is the simpler path but
    /// gives up native AOT and trimming.
    /// </param>
    /// <param name="jsonContentType">
    /// The exact Content-Type written on success responses. The charset suffix is part of a
    /// published wire contract, so it is configurable rather than assumed.
    /// </param>
    public static EndpointGroup MapEndpointGroup(
        this IEndpointRouteBuilder endpoints,
        string? name = null,
        JsonSerializerContext? jsonContext = null,
        string jsonContentType = "application/json; charset=utf-8")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonContentType);

        var services = endpoints.ServiceProvider;
        var groupName = name
            ?? Assembly.GetCallingAssembly().GetName().Name
            ?? throw new InvalidOperationException("The calling assembly has no simple name; pass a group name explicitly.");

        var jsonOptions = jsonContext?.Options
            ?? services.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions
            ?? JsonSerializerOptions.Web;

        var options = services.GetService<IOptions<NativeEndpointsOptions>>()?.Value;
        var convention = options?.OperationConvention ?? EndpointConventionBuilderExtensions.ApplyDefaultOperationMetadata;
        var valueBinders = options?.ValueBinders ?? new EndpointValueBinders();

        return new(endpoints, groupName, jsonContext, jsonOptions, jsonContentType, convention, valueBinders);
    }
}

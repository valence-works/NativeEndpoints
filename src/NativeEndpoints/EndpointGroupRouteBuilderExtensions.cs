using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    /// <param name="tag">
    /// The OpenAPI tag the group's operations are published under. Defaults to <paramref name="name"/>.
    /// Separate from the name because the two answer different questions: the name keeps endpoint
    /// identifiers unique across a host, while the tag is how a document groups operations for a
    /// reader, and several groups can legitimately share one tag while keeping distinct names.
    /// </param>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "JsonSerializerOptions.Web is only used when no JsonSerializerContext was supplied. A trimmed or AOT host supplies one.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "JsonSerializerOptions.Web is only used when no JsonSerializerContext was supplied. A trimmed or AOT host supplies one.")]
    // NoInlining because the default group name comes from Assembly.GetCallingAssembly(): if the
    // JIT inlined this method into its caller, the "calling assembly" would be whatever assembly
    // the caller was itself inlined into, and the group would silently take the wrong name.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static EndpointGroup MapEndpointGroup(
        this IEndpointRouteBuilder endpoints,
        string? name = null,
        JsonSerializerContext? jsonContext = null,
        string jsonContentType = "application/json; charset=utf-8",
        string? tag = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonContentType);

        var services = endpoints.ServiceProvider;

        var groupName = name
            ?? Assembly.GetCallingAssembly().GetName().Name
            ?? throw new InvalidOperationException("The calling assembly has no simple name; pass a group name explicitly.");

        // Fail at mapping time when the pipeline's services were never registered. Without this
        // check the omission only surfaces on the first binding failure or handler exception, where
        // WriteProblemAsync cannot resolve a problem writer and the caller's real 400 becomes an
        // opaque 500. Either registration satisfies the check — the unkeyed writer that
        // AddNativeEndpoints installs, or one keyed by this group's name — because those are
        // exactly the two the failure path consults per request; a host composing only keyed
        // per-group writers is a working configuration, not a misconfiguration.
        // Probed without instantiating: a host may legitimately register a scoped writer, and
        // resolving that from the root provider would itself throw under scope validation. A
        // container that cannot answer the question skips the check rather than guessing.
        var registrationProbe = services.GetService<IServiceProviderIsService>();
        if (registrationProbe is not null &&
            !registrationProbe.IsService(typeof(IEndpointProblemWriter)) &&
            services.GetService<IServiceProviderIsKeyedService>()?.IsKeyedService(typeof(IEndpointProblemWriter), groupName) is not true)
        {
            throw new InvalidOperationException(
                "No IEndpointProblemWriter is registered. " +
                "Call services.AddNativeEndpoints() before mapping an endpoint group.");
        }

        var jsonOptions = jsonContext?.Options
            ?? services.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions
            ?? JsonSerializerOptions.Web;

        var options = services.GetService<IOptions<NativeEndpointsOptions>>()?.Value;
        var convention = options?.OperationConvention ?? EndpointConventionBuilderExtensions.ApplyDefaultOperationMetadata;
        var valueBinders = options?.ValueBinders ?? new EndpointValueBinders();

        return new(endpoints, groupName, jsonContext, jsonOptions, jsonContentType, convention, valueBinders, tag);
    }
}

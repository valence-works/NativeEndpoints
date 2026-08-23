using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace NativeEndpoints;

/// <summary>Standard ASP.NET Core conventions the mapping pipeline applies.</summary>
/// <remarks>
/// Every one of these is ordinary endpoint metadata applied through the standard convention builder.
/// Routing, binding, serialization, results, and policy execution are untouched.
/// </remarks>
public static class EndpointConventionBuilderExtensions
{
    private const string JsonContentType = "application/json";

    private static readonly MethodInfo ApiExplorerDescriptionMethod =
        typeof(RequestDelegate).GetMethod(nameof(RequestDelegate.Invoke))
        ?? throw new InvalidOperationException("RequestDelegate.Invoke metadata is unavailable.");

    /// <summary>Adds one metadata item to the endpoint.</summary>
    public static TBuilder AddEndpointMetadata<TBuilder, TMetadata>(this TBuilder builder, TMetadata metadata)
        where TBuilder : IEndpointConventionBuilder
        where TMetadata : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(metadata);
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(metadata));
        return builder;
    }

    /// <summary>Records the group an endpoint belongs to.</summary>
    public static TBuilder WithEndpointGroup<TBuilder>(this TBuilder builder, string name)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return builder.AddEndpointMetadata(new EndpointGroupMetadata(name));
    }

    /// <summary>
    /// The metadata every operation carries unless the group replaces this convention: a unique
    /// endpoint name, a tag, the documented success response, an optional request body, the
    /// authorization responses the endpoint can actually produce, an API Explorer description
    /// method, and lifetime validation.
    /// </summary>
    public static void ApplyDefaultOperationMetadata(IEndpointConventionBuilder builder, EndpointOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(context);

        var hasBody = context.ResponseType is not null && context.ResponseType != typeof(void);
        builder
            .WithName($"{context.GroupName}_{context.Operation}")
            .WithTags(context.GroupName)
            .WithEndpointGroup(context.GroupName)
            .AddEndpointMetadata(new ProducesResponseTypeMetadata(
                context.DocumentedStatus,
                context.ResponseType ?? typeof(void),
                hasBody ? [JsonContentType] : []));

        if (context.RequestType is not null)
            builder.AddEndpointMetadata(new AcceptsMetadata(context.Accepts ?? [JsonContentType], context.RequestType, false));

        DocumentAuthResponses(builder, context.DocumentAuthResponses);
        builder.WithApiExplorerDescription().RequireStableEndpointMetadata();
    }

    /// <summary>
    /// Documents 401 and 403 only where the completed metadata actually carries authorization.
    /// </summary>
    /// <remarks>
    /// Runs in <see cref="IEndpointConventionBuilder.Finally"/> so it observes conventions applied
    /// after mapping, including authorization contributed by a class-level attribute. Stamping the
    /// pair unconditionally would document responses a public endpoint can never return.
    /// </remarks>
    private static void DocumentAuthResponses(IEndpointConventionBuilder builder, bool? forced)
    {
        builder.Finally(endpointBuilder =>
        {
            var document = forced ?? (
                endpointBuilder.Metadata.OfType<IAuthorizeData>().Any() &&
                !endpointBuilder.Metadata.OfType<IAllowAnonymous>().Any());

            if (!document)
                return;

            endpointBuilder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(void), []));
            endpointBuilder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(void), []));
        });
    }

    /// <summary>Publishes <see cref="RequestDelegate.Invoke"/> as the endpoint's description method.</summary>
    /// <remarks>
    /// API Explorer needs a <see cref="MethodInfo"/> in endpoint metadata to derive an
    /// <c>ApiDescription</c>; without one the endpoint never reaches the OpenAPI document and a test
    /// that inspects the document passes vacuously. It must also be the last <see cref="MethodInfo"/>
    /// in the collection, because <c>EndpointMetadataCollection.GetMetadata&lt;T&gt;()</c> selects the
    /// last match. Publishing the handler's own method instead would root its declaring assembly
    /// through API Explorer, so the stable framework method is used deliberately.
    /// </remarks>
    public static TBuilder WithApiExplorerDescription<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointMetadata(ApiExplorerDescriptionMethod);

    /// <summary>
    /// Validates completed endpoint metadata as the final convention. The returned builder is the
    /// original builder, and no request, routing, authorization, binding, serialization, or result
    /// behavior changes.
    /// </summary>
    public static TBuilder RequireStableEndpointMetadata<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Finally(StripCompilerMetadataAndValidate);
        return builder;
    }

    private static void StripCompilerMetadataAndValidate(EndpointBuilder builder)
    {
        // RequestDelegateFactory copies handler attributes into endpoint metadata. Compiler-only
        // attributes are not part of the HTTP or OpenAPI contract, but AsyncStateMachineAttribute
        // references the handler's generated implementation type and would pin a collectible owner.
        // This runs whether or not the boundary is enforced: a state machine pins its owner even in
        // a host that never builds a document.
        for (var index = builder.Metadata.Count - 1; index >= 0; index--)
        {
            if (builder.Metadata[index] is System.Runtime.CompilerServices.AsyncStateMachineAttribute
                or System.Diagnostics.DebuggerStepThroughAttribute)
            {
                builder.Metadata.RemoveAt(index);
            }
        }

        if (!EnforcementEnabled(builder.ApplicationServices))
            return;

        EndpointLifetimeValidator.ValidateAndMark(builder);
    }

    /// <summary>
    /// Fail-closed: anything other than an explicit, resolvable suppression enforces the boundary,
    /// so an unconfigured host or one with no service provider keeps the guard.
    /// </summary>
    private static bool EnforcementEnabled(IServiceProvider? services) =>
        services?.GetService<IOptions<EndpointLifetimeEnforcementOptions>>()?.Value.Enabled ?? true;
}

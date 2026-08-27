using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NativeEndpoints;

/// <summary>
/// A named group of endpoints mapped onto one route builder.
/// </summary>
/// <remarks>
/// There is no process-global discovery and no static registry: every route is registered by an
/// explicit call, and each Map method returns the standard <see cref="IEndpointConventionBuilder"/>,
/// so authorization, filters, metadata, and any other ASP.NET Core convention compose on top as
/// usual. Nothing is prescribed about how a request is handled; the group owns the route, the
/// binding, and the metadata, and stops there.
/// <para>
/// Handlers are published as bare <see cref="RequestDelegate"/> instances on purpose. Passing a typed
/// lambda to <c>MapGet</c> would make RequestDelegateFactory publish the handler's own
/// <see cref="System.Reflection.MethodInfo"/> and async state machine into endpoint metadata, which
/// API Explorer retains for the host service-provider lifetime.
/// </para>
/// </remarks>
public sealed class EndpointGroup
{
    private readonly IEndpointRouteBuilder _endpoints;
    private readonly string _jsonContentType;
    private readonly JsonSerializerContext? _jsonContext;
    private readonly JsonSerializerOptions _jsonOptions;

    private readonly EndpointOperationConvention _convention;
    private readonly EndpointValueBinders _valueBinders;

    internal EndpointGroup(
        IEndpointRouteBuilder endpoints,
        string name,
        JsonSerializerContext? jsonContext,
        JsonSerializerOptions jsonOptions,
        string jsonContentType,
        EndpointOperationConvention convention,
        EndpointValueBinders valueBinders,
        string? tag = null)
    {
        _endpoints = endpoints;
        Name = name;
        _jsonContext = jsonContext;
        _jsonOptions = jsonOptions;
        _jsonContentType = jsonContentType;
        _convention = convention;
        _valueBinders = valueBinders;
        Tag = tag ?? name;
    }

    /// <summary>The name applied to every endpoint in the group.</summary>
    public string Name { get; }

    /// <summary>The tag the group's operations are published under. Defaults to <see cref="Name"/>.</summary>
    public string Tag { get; }

    /// <summary>
    /// Maps a route onto an inline handler, for an operation with no endpoint class of its own.
    /// </summary>
    /// <remarks>
    /// Binding, failure translation, and endpoint metadata are identical to an endpoint class; only
    /// where the handling lives differs. Useful for one-line operations that would be more code as a
    /// class than as a lambda.
    /// </remarks>
    public IEndpointConventionBuilder MapHandler<TRequest, TResponse>(
        string method,
        string pattern,
        string operation,
        Func<HttpContext, TRequest, CancellationToken, Task<TResponse>> handler,
        EndpointBodyMode? bodyMode = null,
        string[]? accepts = null,
        int successStatus = StatusCodes.Status200OK)
        where TResponse : notnull
    {
        var writer = new EndpointJsonWriter<TResponse>(this);
        return MapOperation<TRequest>(
            new EndpointOperationDescriptor
            {
                Method = method,
                Pattern = pattern,
                Operation = operation,
                BodyMode = bodyMode,
                Accepts = accepts,
                ResponseType = typeof(TResponse),
                SuccessStatus = successStatus
            },
            async (context, request, cancellationToken) =>
            {
                var response = await handler(context, request, cancellationToken);
                await writer.WriteAsync(context, response, successStatus);
            });
    }

    /// <summary>Maps a route that takes no request contract onto an inline handler.</summary>
    public IEndpointConventionBuilder MapHandler<TResponse>(
        string method,
        string pattern,
        string operation,
        Func<HttpContext, CancellationToken, Task<TResponse>> handler,
        int successStatus = StatusCodes.Status200OK)
        where TResponse : notnull
    {
        var writer = new EndpointJsonWriter<TResponse>(this);
        return MapUnbound(
            new EndpointOperationDescriptor
            {
                Method = method,
                Pattern = pattern,
                Operation = operation,
                ResponseType = typeof(TResponse),
                SuccessStatus = successStatus
            },
            async context => await writer.WriteAsync(context, await handler(context, context.RequestAborted), successStatus));
    }

    /// <summary>Writes a value using the owner's source-generated serializer metadata.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The options path is only reached when no JsonSerializerContext was supplied. " +
                        "A trimmed or AOT host supplies one, which takes the JsonTypeInfo path above.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The options path is only reached when no JsonSerializerContext was supplied. " +
                        "A trimmed or AOT host supplies one, which takes the JsonTypeInfo path above.")]
    public Task WriteJsonAsync<TValue>(HttpContext context, TValue value, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = statusCode;
        if (_jsonContext is null)
            return Results.Json(value, _jsonOptions, _jsonContentType).ExecuteAsync(context);

        var typeInfo = _jsonContext.GetTypeInfo(typeof(TValue))
                       ?? throw new InvalidOperationException($"No source-generated JSON metadata exists for '{typeof(TValue).FullName}'.");
        return Results.Json(value, typeInfo, _jsonContentType).ExecuteAsync(context);
    }

    /// <summary>
    /// A per-endpoint success writer that resolves the response type's serializer metadata once and
    /// reuses it for every request, instead of looking it up per response.
    /// </summary>
    /// <remarks>
    /// Metadata is resolved on first use rather than at map time: resolution can throw — no
    /// source-generated metadata for the type, or options with no resolver — and that failure is
    /// contractually a per-request 500 through the shared failure path, not a startup crash.
    /// The fast path writes exactly what <see cref="WriteJsonAsync"/> writes for a non-null value of
    /// the declared type; a null value (which writes no body) and a runtime type diverging from the
    /// declared one (which serializes polymorphically) keep the original
    /// <see cref="WriteJsonAsync"/> path so the observable behaviour cannot drift.
    /// </remarks>
    private sealed class EndpointJsonWriter<TValue>(EndpointGroup group)
    {
        private JsonTypeInfo<TValue>? _typeInfo;

        public Task WriteAsync(HttpContext context, TValue value, int statusCode)
        {
            if (value is null || !(typeof(TValue).IsValueType || value.GetType() == typeof(TValue)))
                return group.WriteJsonAsync(context, value, statusCode);

            // Status first, matching WriteJsonAsync: a metadata failure below must still leave the
            // status it left before.
            context.Response.StatusCode = statusCode;
            var typeInfo = _typeInfo ??= Resolve();
            return context.Response.WriteAsJsonAsync(value, typeInfo, group._jsonContentType);
        }

        private JsonTypeInfo<TValue> Resolve() => (JsonTypeInfo<TValue>)(group._jsonContext is null
            ? group._jsonOptions.GetTypeInfo(typeof(TValue))
            : group._jsonContext.GetTypeInfo(typeof(TValue))
              ?? throw new InvalidOperationException($"No source-generated JSON metadata exists for '{typeof(TValue).FullName}'."));
    }

    /// <summary>
    /// The low-level operation pipeline: bind, dispatch, translate failures, and attach the module
    /// operation metadata. The typed Map methods and external bridges compose on top of this,
    /// describing the operation with an <see cref="EndpointOperationDescriptor"/>.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The reflective binder is only called when no generated binder was supplied. A trimmed or AOT build runs the generator, which supplies one.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflective binder is only called when no generated binder was supplied. A trimmed or AOT build runs the generator, which supplies one.")]
    public IEndpointConventionBuilder MapOperation<TMessage>(
        EndpointOperationDescriptor descriptor,
        Func<HttpContext, TMessage, CancellationToken, Task> dispatch,
        EndpointBinder<TMessage>? binder = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(dispatch);

        var effectiveBodyMode = descriptor.BodyMode ?? DefaultBodyMode(descriptor.Method);
        var jsonOptions = _jsonOptions;
        // Eager, so a missing stance is a startup failure naming the operation rather than a CSRF
        // hole discovered later. No analyzer can cover this: the stance is set inside Configure,
        // which the generator reads only shallowly.
        if (descriptor.BodyKind is EndpointBodyKind.Form && descriptor.RequireAntiforgery is null)
        {
            throw new InvalidOperationException(
                $"Operation '{descriptor.Operation}' reads a form body but declares no antiforgery stance. " +
                "Set options.RequireAntiforgery to true to validate a token, or false to opt out " +
                "(the usual choice for a token-authenticated API). A form is the one request shape a " +
                "browser can be made to send cross-origin with the user's cookies, so this library " +
                "will not guess.");
        }

        var bindingOptions = new EndpointBindingOptions(
            effectiveBodyMode, descriptor.StrictTypedParsing, _valueBinders, descriptor.BodyKind);

        RequestDelegate handler = async context =>
        {
            EndpointBindingResult<TMessage> binding;
            try
            {
                binding = binder is null
                    ? await EndpointRequestBinder.BindAsync<TMessage>(context, jsonOptions, bindingOptions)
                    : await binder(context, jsonOptions, bindingOptions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A body that cannot be read (an I/O fault, an unreadable stream) is an infrastructure
                // failure, and its details must not leak into the response.
                LogUnexpected(context, exception, typeof(TMessage));
                if (context.Response.HasStarted)
                {
                    // Binding does not write, so this is only reachable through middleware or a
                    // custom binder that did; the same rule as HandleFailureAsync applies.
                    context.Abort();
                    return;
                }

                await WriteProblemAsync(context,
                    EndpointProblem.General(StatusCodes.Status500InternalServerError, "Unexpected error occurred"));
                return;
            }

            if (!binding.Succeeded)
            {
                // The content-type-gated modes reject an unsupported media type with a bare status, so
                // there is no problem document to write. RequiredWithContentTypeAndPayload also
                // arrives here for a literal-null payload, which it answers at the same gate.
                if (binding.Failure is EndpointBindingFailure.UnsupportedMediaType &&
                    effectiveBodyMode is EndpointBodyMode.RequiredWithContentType or EndpointBodyMode.OptionalWithContentType
                        or EndpointBodyMode.RequiredWithContentTypeAndPayload)
                {
                    context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    return;
                }

                await WriteProblemAsync(context, ToProblem(binding));
                return;
            }

            try
            {
                await dispatch(context, binding.Value!, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await HandleFailureAsync(context, exception, typeof(TMessage));
            }
        };

        var builder = _endpoints.MapMethods(descriptor.Pattern, [descriptor.Method], handler);

        // The framework's own public IAntiforgeryMetadata implementation, rather than a parallel one.
        // This is exactly what DisableAntiforgery() and [RequireAntiforgeryToken] put on an endpoint,
        // so the antiforgery middleware needs no special case for endpoints mapped from here.
        if (descriptor.RequireAntiforgery is { } stance)
            builder.AddEndpointMetadata(new Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryTokenAttribute(stance));

        // An endpoint may describe a request schema it does not bind from the body: several existing
        // GET operations advertise their request shape in the document while binding from the query.
        // Declaring accepts is therefore what decides the OpenAPI request type, not the body mode.
        var declaresRequest = descriptor.Accepts is not null || effectiveBodyMode is not EndpointBodyMode.None;

        // Accepts must follow the kind. AcceptsMatcherPolicy uses this metadata during routing, so
        // leaving the JSON default in place would reject every form request with a bare 415 before
        // the binder's own rules ever ran.
        var effectiveAccepts = descriptor.Accepts ?? DefaultAccepts(descriptor.BodyKind, effectiveBodyMode);
        _convention(builder, Contextualize(
            descriptor,
            requestType: declaresRequest ? typeof(TMessage) : null,
            contractType: typeof(TMessage),
            readsBody: effectiveBodyMode is not EndpointBodyMode.None,
            accepts: effectiveAccepts));
        return builder;
    }

    /// <summary>
    /// Maps an endpoint whose binding and activation were generated at build time.
    /// </summary>
    /// <remarks>
    /// The reflection-free path. <paramref name="bind"/> and <paramref name="activate"/> come from
    /// the source generator, so nothing here resolves a constructor, makes a generic method, or asks
    /// a container to build a type it cannot see. The reflective mapper produces identical endpoints
    /// by a slower route; the conformance suite asserts they agree.
    /// </remarks>
    public IEndpointConventionBuilder MapGenerated<TEndpoint, TRequest, TResponse>(
        ApiEndpointOptions options,
        EndpointBinder<TRequest> bind,
        Func<IServiceProvider, TEndpoint> activate,
        Func<TEndpoint, TRequest, CancellationToken, Task<TResponse>> handle)
        where TEndpoint : ApiEndpointBase
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(activate);

        var writer = new EndpointJsonWriter<TResponse>(this);
        return MapOperation(
            Describe(options, typeof(TResponse)),
            async (context, request, cancellationToken) =>
            {
                var endpoint = activate(context.RequestServices);
                endpoint.HttpContext = context;
                await writer.WriteAsync(context, await handle(endpoint, request, cancellationToken), options.SuccessStatus);
            },
            bind);
    }

    /// <summary>Maps a generated endpoint that returns no content.</summary>
    public IEndpointConventionBuilder MapGeneratedNoContent<TEndpoint, TRequest>(
        ApiEndpointOptions options,
        EndpointBinder<TRequest> bind,
        Func<IServiceProvider, TEndpoint> activate,
        Func<TEndpoint, TRequest, CancellationToken, Task> handle)
        where TEndpoint : ApiEndpointBase
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(activate);

        return MapOperation(
            DescribeNoContent(options),
            async (context, request, cancellationToken) =>
            {
                var endpoint = activate(context.RequestServices);
                endpoint.HttpContext = context;
                await handle(endpoint, request, cancellationToken);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            },
            bind);
    }

    /// <summary>Maps a generated endpoint whose status travels with its result.</summary>
    public IEndpointConventionBuilder MapGeneratedResult<TEndpoint, TRequest, TResponse>(
        ApiEndpointOptions options,
        EndpointBinder<TRequest> bind,
        Func<IServiceProvider, TEndpoint> activate,
        Func<TEndpoint, TRequest, CancellationToken, Task<EndpointResult<TResponse>>> handle)
        where TEndpoint : ApiEndpointBase
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(activate);

        var writer = new EndpointJsonWriter<TResponse>(this);
        return MapOperation(
            Describe(options, typeof(TResponse)),
            async (context, request, cancellationToken) =>
            {
                var endpoint = activate(context.RequestServices);
                endpoint.HttpContext = context;
                var result = await handle(endpoint, request, cancellationToken);
                await writer.WriteAsync(context, result.Response, result.StatusCode);
            },
            bind);
    }

    /// <summary>Maps a generated endpoint that binds no request contract.</summary>
    /// <remarks>
    /// The reflection-free path for <see cref="ApiEndpointWithoutRequest{TResponse}"/> classes:
    /// nothing binds, so the generated slot supplies only the activator. The reflective mapper
    /// produces identical endpoints by a slower route; the conformance suite asserts they agree.
    /// </remarks>
    public IEndpointConventionBuilder MapGeneratedUnbound<TEndpoint, TResponse>(
        ApiEndpointOptions options,
        Func<IServiceProvider, TEndpoint> activate,
        Func<TEndpoint, CancellationToken, Task<TResponse>> handle)
        where TEndpoint : ApiEndpointBase
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(activate);

        return MapUnboundBody<TResponse>(options, async (context, cancellationToken) =>
        {
            var endpoint = activate(context.RequestServices);
            endpoint.HttpContext = context;
            return await handle(endpoint, cancellationToken);
        });
    }

    /// <summary>Maps an options-described operation whose dispatch writes the response itself.</summary>
    /// <remarks>
    /// The raw path, shared by the reflective mapper and the generated registration for non-generic
    /// <see cref="ApiEndpoint"/> classes. Nothing is bound and nothing is written on success — the
    /// dispatch owns the response entirely.
    /// <para>
    /// Owning the response is not the same as having nothing to say about it. The operation still
    /// describes itself: <see cref="ApiEndpointOptions.ResponseType"/> and
    /// <see cref="ApiEndpointOptions.SuccessContentType"/> document the body it writes, and
    /// <see cref="ApiEndpointOptions.DocumentedStatus"/> documents a status that deliberately differs
    /// from the runtime one. Each defaults to saying nothing, which is what a raw endpoint that
    /// really is undescribed wants.
    /// </para>
    /// <para>
    /// The shared failure path — fault renderers, then exception translators, then the sanitized 500
    /// — applies when the dispatch throws, unless
    /// <see cref="ApiEndpointOptions.ContainFailures"/> is false.
    /// </para>
    /// </remarks>
    public IEndpointConventionBuilder MapRaw(ApiEndpointOptions options, Func<HttpContext, Task> dispatch)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatch);
        return MapUnbound(Describe(options, options.ResponseType), dispatch);
    }

    /// <summary>Maps an options-described operation returning a body. Used by the endpoint-class mapper.</summary>
    internal IEndpointConventionBuilder MapBody<TRequest, TResponse>(
        ApiEndpointOptions options,
        Func<HttpContext, TRequest, CancellationToken, Task<TResponse>> dispatch)
        where TResponse : notnull
    {
        var writer = new EndpointJsonWriter<TResponse>(this);
        return MapOperation<TRequest>(
            Describe(options, typeof(TResponse)),
            async (context, request, cancellationToken) =>
                await writer.WriteAsync(context, await dispatch(context, request, cancellationToken), options.SuccessStatus));
    }

    /// <summary>Maps an options-described operation with no request contract. Used by the endpoint-class mapper.</summary>
    internal IEndpointConventionBuilder MapUnboundBody<TResponse>(
        ApiEndpointOptions options,
        Func<HttpContext, CancellationToken, Task<TResponse>> dispatch)
        where TResponse : notnull
    {
        var writer = new EndpointJsonWriter<TResponse>(this);
        return MapUnbound(
            Describe(options, typeof(TResponse)),
            async context => await writer.WriteAsync(context, await dispatch(context, context.RequestAborted), options.SuccessStatus));
    }

    /// <summary>Maps an options-described operation whose status travels with the result. Used by the endpoint-class mapper.</summary>
    internal IEndpointConventionBuilder MapResultBody<TRequest, TResponse>(
        ApiEndpointOptions options,
        Func<HttpContext, TRequest, CancellationToken, Task<EndpointResult<TResponse>>> dispatch)
        where TResponse : notnull
    {
        var writer = new EndpointJsonWriter<TResponse>(this);
        return MapOperation<TRequest>(
            Describe(options, typeof(TResponse)),
            async (context, request, cancellationToken) =>
            {
                var result = await dispatch(context, request, cancellationToken);
                await writer.WriteAsync(context, result.Response, result.StatusCode);
            });
    }

    /// <summary>Maps an options-described operation returning no content. Used by the endpoint-class mapper.</summary>
    internal IEndpointConventionBuilder MapNoContent<TRequest>(
        ApiEndpointOptions options,
        Func<HttpContext, TRequest, CancellationToken, Task> dispatch) =>
        MapOperation<TRequest>(
            DescribeNoContent(options),
            async (context, request, cancellationToken) =>
            {
                await dispatch(context, request, cancellationToken);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });

    /// <summary>
    /// The single translation from an endpoint's configured options to an operation descriptor.
    /// Every options-described mapping path builds its descriptor here, so a setting added to
    /// <see cref="ApiEndpointOptions"/> is forwarded — or deliberately not — in exactly one place.
    /// </summary>
    private static EndpointOperationDescriptor Describe(ApiEndpointOptions options, Type? responseType) =>
        new()
        {
            Method = options.Method!,
            Pattern = options.Route!,
            Operation = options.Operation!,
            Name = options.Name,
            BodyMode = options.BodyMode,
            Accepts = options.Accepts,
            ResponseType = responseType,
            SuccessContentType = options.SuccessContentType,
            SuccessStatus = options.SuccessStatus,
            DocumentedStatus = options.DocumentedStatus,
            DocumentAuthResponses = options.DocumentAuthResponses,
            StrictTypedParsing = options.StrictTypedParsing,
            BodyKind = options.BodyKind,
            RequireAntiforgery = options.RequireAntiforgery,
            ContainFailures = options.ContainFailures
        };

    /// <summary>
    /// The no-content variant of <see cref="Describe"/>: a handler that returns nothing always
    /// writes 204 and documents no response body, whatever the options say.
    /// </summary>
    private static EndpointOperationDescriptor DescribeNoContent(ApiEndpointOptions options) =>
        Describe(options, responseType: null) with { SuccessStatus = StatusCodes.Status204NoContent };

    /// <summary>The content types an operation accepts when it did not name them itself.</summary>
    /// <remarks>
    /// Null for a JSON or bodyless operation, so the existing behaviour of leaving Accepts unset — and
    /// letting the convention default it to application/json — is preserved exactly.
    /// </remarks>
    private static string[]? DefaultAccepts(EndpointBodyKind kind, EndpointBodyMode mode) =>
        mode is not EndpointBodyMode.None && kind is EndpointBodyKind.Form
            ? ["multipart/form-data", "application/x-www-form-urlencoded"]
            : null;

    private static EndpointBodyMode DefaultBodyMode(string method) => method switch
    {
        _ when HttpMethods.IsGet(method) || HttpMethods.IsHead(method) => EndpointBodyMode.None,
        _ when HttpMethods.IsDelete(method) => EndpointBodyMode.Optional,
        _ => EndpointBodyMode.Required
    };

    private static EndpointProblem ToProblem<T>(EndpointBindingResult<T> binding) => binding.Failure switch
    {
        EndpointBindingFailure.UnsupportedMediaType =>
            EndpointProblem.General(StatusCodes.Status415UnsupportedMediaType, binding.Message!),
        EndpointBindingFailure.RequestTooLarge =>
            EndpointProblem.General(StatusCodes.Status413PayloadTooLarge, binding.Message!),
        EndpointBindingFailure.MalformedBody =>
            EndpointProblem.General(StatusCodes.Status400BadRequest, binding.Message!, "serializerErrors"),
        // A failure that names its offending value — a strict-parsing rejection — is keyed by that
        // value's wire name, so a caller can see which parameter to fix.
        _ => EndpointProblem.General(StatusCodes.Status400BadRequest, binding.Message!, binding.Key ?? "generalErrors")
    };

    /// <summary>
    /// The shared failure path: module-owned fault renderers first, then translation into the
    /// owner's problem shape, then a sanitized generic failure.
    /// </summary>
    private async Task HandleFailureAsync(HttpContext context, Exception exception, Type messageType)
    {
        // A dispatch (or the serializer mid-write) that throws after the response started streaming
        // cannot be answered with a problem document: setting the status or writing a body would
        // throw an InvalidOperationException that replaces the real failure. Fault renderers and
        // exception translators write responses, so they are not consulted either. Log the original
        // exception and abort the connection so the truncated response is not mistaken for a
        // complete one — the same choice ASP.NET Core's own exception middleware makes when it
        // cannot re-execute the request.
        if (context.Response.HasStarted)
        {
            LogUnexpected(context, exception, messageType);
            context.Abort();
            return;
        }

        foreach (var renderer in FaultRenderers(context))
        {
            if (await renderer.TryWriteAsync(context, exception))
                return;
        }

        var problem = Translate(context, exception);
        if (problem is null)
        {
            LogUnexpected(context, exception, messageType);
            problem = EndpointProblem.General(StatusCodes.Status500InternalServerError, "Unexpected error occurred");
        }

        await WriteProblemAsync(context, problem);
    }

    // Failure services resolve keyed by the owner first so hosts composing several modules keep each
    // module's own shapes; the unkeyed registration remains the single-module fallback.
    private IEnumerable<IEndpointFaultRenderer> FaultRenderers(HttpContext context) =>
        context.RequestServices.GetKeyedServices<IEndpointFaultRenderer>(Name)
            .Concat(context.RequestServices.GetServices<IEndpointFaultRenderer>());

    private EndpointProblem? Translate(HttpContext context, Exception exception)
    {
        var translators = context.RequestServices.GetKeyedServices<IEndpointExceptionTranslator>(Name)
            .Concat(context.RequestServices.GetServices<IEndpointExceptionTranslator>());
        foreach (var translator in translators)
        {
            var problem = translator.Translate(exception);
            if (problem is not null)
                return problem;
        }

        return null;
    }

    /// <summary>
    /// The no-contract pipeline: nothing binds, so the descriptor's request-side settings do not
    /// apply. Everything describing the operation still does — an unbound endpoint documents its
    /// status, its content type, and its authorization responses like any other.
    /// </summary>
    /// <remarks>
    /// This previously documented the runtime status unconditionally, reasoning that an operation
    /// without a request contract has no result-carried status to diverge from. That conflated two
    /// separate things: <see cref="ApiEndpointWithResult{TRequest,TResponse}"/> carries a status in
    /// its result, whereas <see cref="EndpointOperationDescriptor.DocumentedStatus"/> is an explicit
    /// declaration by the author, which an unbound operation is as entitled to make as a bound one.
    /// <see cref="EndpointOperationDescriptor.DocumentAuthResponses"/> was dropped here too, with no
    /// stated reason, which made its documented "forces the pair on or off" contract unreachable on
    /// this path.
    /// </remarks>
    private IEndpointConventionBuilder MapUnbound(
        EndpointOperationDescriptor descriptor,
        Func<HttpContext, Task> dispatch)
    {
        // An owner whose published contract makes the host's exception pipeline responsible for
        // unexpected failures opts out of containment and runs bare.
        RequestDelegate handler = descriptor.ContainFailures
            ? async context =>
            {
                try
                {
                    await dispatch(context);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await HandleFailureAsync(context, exception, typeof(EndpointGroup));
                }
            }
            : dispatch.Invoke;

        var builder = _endpoints.MapMethods(descriptor.Pattern, [descriptor.Method], handler);
        _convention(builder, Contextualize(descriptor));
        return builder;
    }

    /// <summary>
    /// The single translation from a descriptor to the context the convention sees. Every mapping
    /// path builds its context here, for the same reason every path builds its descriptor in
    /// <see cref="Describe"/>: a field added to either can no longer be dropped by one path and
    /// forwarded by another. That is exactly how <see cref="EndpointOperationDescriptor.DocumentedStatus"/>
    /// and <see cref="EndpointOperationDescriptor.DocumentAuthResponses"/> went missing on the
    /// unbound path while the bound one forwarded them.
    /// </summary>
    private EndpointOperationContext Contextualize(
        EndpointOperationDescriptor descriptor,
        Type? requestType = null,
        Type? contractType = null,
        bool readsBody = false,
        string[]? accepts = null) =>
        new()
        {
            GroupName = Name,
            Tag = Tag,
            Operation = descriptor.Operation,
            Name = descriptor.Name,
            Method = descriptor.Method,
            Pattern = descriptor.Pattern,
            RequestType = requestType,
            ContractType = contractType,
            ReadsBody = readsBody,
            BodyKind = descriptor.BodyKind,
            ResponseType = descriptor.ResponseType,
            SuccessContentType = descriptor.SuccessContentType,
            Accepts = accepts ?? descriptor.Accepts,
            SuccessStatus = descriptor.SuccessStatus,
            DocumentedStatus = descriptor.DocumentedStatus ?? descriptor.SuccessStatus,
            DocumentAuthResponses = descriptor.DocumentAuthResponses
        };

    private Task WriteProblemAsync(HttpContext context, EndpointProblem problem)
    {
        var writer = context.RequestServices.GetKeyedService<IEndpointProblemWriter>(Name)
                     ?? context.RequestServices.GetRequiredService<IEndpointProblemWriter>();
        return writer.WriteAsync(context, problem);
    }

    private static void LogUnexpected(HttpContext context, Exception exception, Type messageType) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EndpointGroup))
            .LogError(exception, "Unexpected error occurred when handling request '{type}'", messageType);
}

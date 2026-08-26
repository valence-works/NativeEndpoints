using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        EndpointValueBinders valueBinders)
    {
        _endpoints = endpoints;
        Name = name;
        _jsonContext = jsonContext;
        _jsonOptions = jsonOptions;
        _jsonContentType = jsonContentType;
        _convention = convention;
        _valueBinders = valueBinders;
    }

    /// <summary>The name applied to every endpoint in the group.</summary>
    public string Name { get; }

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
        where TResponse : notnull =>
        MapOperation<TRequest>(            method, pattern, operation, bodyMode, accepts, typeof(TResponse), successStatus, null,
            async (context, request, cancellationToken) =>
            {
                var response = await handler(context, request, cancellationToken);
                await WriteJsonAsync(context, response, successStatus);
            });

    /// <summary>Maps a route that takes no request contract onto an inline handler.</summary>
    public IEndpointConventionBuilder MapHandler<TResponse>(
        string method,
        string pattern,
        string operation,
        Func<HttpContext, CancellationToken, Task<TResponse>> handler,
        int successStatus = StatusCodes.Status200OK)
        where TResponse : notnull =>
        MapUnbound(method, pattern, operation, typeof(TResponse), successStatus,
            async context => await WriteJsonAsync(context, await handler(context, context.RequestAborted), successStatus));

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
    /// The low-level operation pipeline: bind, dispatch, translate failures, and attach the module
    /// operation metadata. The typed Map methods and external bridges compose on top of this.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The reflective binder is only called when no generated binder was supplied. A trimmed or AOT build runs the generator, which supplies one.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflective binder is only called when no generated binder was supplied. A trimmed or AOT build runs the generator, which supplies one.")]
    public IEndpointConventionBuilder MapOperation<TMessage>(
        string method,
        string pattern,
        string operation,
        EndpointBodyMode? bodyMode,
        string[]? accepts,
        Type? responseType,
        int successStatus,
        int? documentedStatus,
        Func<HttpContext, TMessage, CancellationToken, Task> dispatch,
        bool? documentAuthResponses = null,
        EndpointBinder<TMessage>? binder = null,
        bool strictTypedParsing = false)
    {
        var effectiveBodyMode = bodyMode ?? DefaultBodyMode(method);
        var jsonOptions = _jsonOptions;
        var bindingOptions = new EndpointBindingOptions(effectiveBodyMode, strictTypedParsing, _valueBinders);

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
                await WriteProblemAsync(context,
                    EndpointProblem.General(StatusCodes.Status500InternalServerError, "Unexpected error occurred"));
                return;
            }

            if (!binding.Succeeded)
            {
                // The content-type-gated modes reject media types with a bare status, before any body
                // is read, so there is no problem document to write.
                if (binding.Failure is EndpointBindingFailure.UnsupportedMediaType &&
                    effectiveBodyMode is EndpointBodyMode.RequiredWithContentType or EndpointBodyMode.OptionalWithContentType)
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

        var builder = _endpoints.MapMethods(pattern, [method], handler);

        // An endpoint may describe a request schema it does not bind from the body: several existing
        // GET operations advertise their request shape in the document while binding from the query.
        // Declaring accepts is therefore what decides the OpenAPI request type, not the body mode.
        var declaresRequest = accepts is not null || effectiveBodyMode is not EndpointBodyMode.None;
        _convention(builder, new EndpointOperationContext
        {
            GroupName = Name,
            Operation = operation,
            Method = method,
            Pattern = pattern,
            RequestType = declaresRequest ? typeof(TMessage) : null,
            ContractType = typeof(TMessage),
            ReadsBody = effectiveBodyMode is not EndpointBodyMode.None,
            ResponseType = responseType,
            Accepts = accepts,
            SuccessStatus = successStatus,
            DocumentedStatus = documentedStatus ?? successStatus,
            DocumentAuthResponses = documentAuthResponses
        });
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

        return MapOperation<TRequest>(
            options.Method!, options.Route!, options.Operation!, options.BodyMode, options.Accepts,
            typeof(TResponse), options.SuccessStatus, options.DocumentedStatus,
            async (context, request, cancellationToken) =>
            {
                var endpoint = activate(context.RequestServices);
                endpoint.HttpContext = context;
                await WriteJsonAsync(context, await handle(endpoint, request, cancellationToken), options.SuccessStatus);
            },
            options.DocumentAuthResponses,
            bind,
            options.StrictTypedParsing);
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

        return MapOperation<TRequest>(
            options.Method!, options.Route!, options.Operation!, options.BodyMode, options.Accepts,
            null, StatusCodes.Status204NoContent, options.DocumentedStatus,
            async (context, request, cancellationToken) =>
            {
                var endpoint = activate(context.RequestServices);
                endpoint.HttpContext = context;
                await handle(endpoint, request, cancellationToken);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            },
            options.DocumentAuthResponses,
            bind,
            options.StrictTypedParsing);
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

        return MapOperation<TRequest>(
            options.Method!, options.Route!, options.Operation!, options.BodyMode, options.Accepts,
            typeof(TResponse), options.SuccessStatus, options.DocumentedStatus,
            async (context, request, cancellationToken) =>
            {
                var endpoint = activate(context.RequestServices);
                endpoint.HttpContext = context;
                var result = await handle(endpoint, request, cancellationToken);
                await WriteJsonAsync(context, result.Response, result.StatusCode);
            },
            options.DocumentAuthResponses,
            bind,
            options.StrictTypedParsing);
    }

    /// <summary>Maps an options-described operation returning a body. Used by the endpoint-class mapper.</summary>
    internal IEndpointConventionBuilder MapBody<TRequest, TResponse>(
        ApiEndpointOptions options,
        Func<HttpContext, TRequest, CancellationToken, Task<TResponse>> dispatch)
        where TResponse : notnull =>
        MapOperation<TRequest>(            options.Method!, options.Route!, options.Operation!, options.BodyMode, options.Accepts,
            typeof(TResponse), options.SuccessStatus, options.DocumentedStatus,
            async (context, request, cancellationToken) =>
                await WriteJsonAsync(context, await dispatch(context, request, cancellationToken), options.SuccessStatus), options.DocumentAuthResponses,
            strictTypedParsing: options.StrictTypedParsing);

    /// <summary>Maps an options-described operation with no request contract. Used by the endpoint-class mapper.</summary>
    internal IEndpointConventionBuilder MapUnboundBody<TResponse>(
        ApiEndpointOptions options,
        Func<HttpContext, CancellationToken, Task<TResponse>> dispatch)
        where TResponse : notnull =>
        MapUnbound(options.Method!, options.Route!, options.Operation!, typeof(TResponse), options.SuccessStatus,
            async context => await WriteJsonAsync(context, await dispatch(context, context.RequestAborted), options.SuccessStatus));

    /// <summary>Maps an options-described operation whose status travels with the result. Used by the endpoint-class mapper.</summary>
    internal IEndpointConventionBuilder MapResultBody<TRequest, TResponse>(
        ApiEndpointOptions options,
        Func<HttpContext, TRequest, CancellationToken, Task<EndpointResult<TResponse>>> dispatch)
        where TResponse : notnull =>
        MapOperation<TRequest>(            options.Method!, options.Route!, options.Operation!, options.BodyMode, options.Accepts,
            typeof(TResponse), options.SuccessStatus, options.DocumentedStatus,
            async (context, request, cancellationToken) =>
            {
                var result = await dispatch(context, request, cancellationToken);
                await WriteJsonAsync(context, result.Response, result.StatusCode);
            },
            options.DocumentAuthResponses,
            strictTypedParsing: options.StrictTypedParsing);

    /// <summary>Maps an options-described operation returning no content. Used by the endpoint-class mapper.</summary>
    internal IEndpointConventionBuilder MapNoContent<TRequest>(
        ApiEndpointOptions options,
        Func<HttpContext, TRequest, CancellationToken, Task> dispatch) =>
        MapOperation<TRequest>(            options.Method!, options.Route!, options.Operation!, options.BodyMode, options.Accepts,
            null, StatusCodes.Status204NoContent, options.DocumentedStatus,
            async (context, request, cancellationToken) =>
            {
                await dispatch(context, request, cancellationToken);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            },
            options.DocumentAuthResponses,
            strictTypedParsing: options.StrictTypedParsing);

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

    private IEndpointConventionBuilder MapUnbound(
        string method,
        string pattern,
        string operation,
        Type? responseType,
        int successStatus,
        Func<HttpContext, Task> dispatch)
    {
        RequestDelegate handler = async context =>
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
        };

        var builder = _endpoints.MapMethods(pattern, [method], handler);
        _convention(builder, new EndpointOperationContext
        {
            GroupName = Name,
            Operation = operation,
            Method = method,
            Pattern = pattern,
            ResponseType = responseType,
            SuccessStatus = successStatus,
            DocumentedStatus = successStatus
        });
        return builder;
    }

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

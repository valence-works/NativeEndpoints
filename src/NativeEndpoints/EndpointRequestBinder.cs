using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NativeEndpoints;

/// <summary>How an endpoint treats a JSON request body.</summary>
public enum EndpointBodyMode
{
    /// <summary>No body is read. Values come from route and query only.</summary>
    None,

    /// <summary>
    /// A JSON body is required. A content type that is present but not JSON is unsupported media;
    /// an absent content type still attempts the body, so an empty or malformed payload is reported
    /// as a bad request rather than as unsupported media.
    /// </summary>
    Required,

    /// <summary>
    /// As <see cref="Required"/>, but an absent content type is also unsupported media, and the
    /// rejection is a bare 415 status with no response body.
    /// </summary>
    /// <remarks>
    /// This reproduces a published contract in which the media-type check runs before any body is
    /// read and writes only a status code. It is not the default because every other operation
    /// reports an unsupported media type through the owner's problem shape.
    /// </remarks>
    RequiredWithContentType,

    /// <summary>A JSON body is read when present, and its absence binds from route and query instead.</summary>
    Optional,

    /// <summary>
    /// As <see cref="RequiredWithContentType"/> for the media-type gate — a missing or non-JSON
    /// content type is a bare 415 — but a body that deserializes to <c>null</c> binds from route and
    /// query instead of being rejected.
    /// </summary>
    OptionalWithContentType
}

/// <summary>The reason a request could not be bound, mapped by the caller to a status code.</summary>
public enum EndpointBindingFailure
{
    /// <summary>The request declared a content type the endpoint does not accept.</summary>
    UnsupportedMediaType,
    /// <summary>A body was required and none was supplied.</summary>
    MissingBody,
    /// <summary>The body was present but could not be deserialized.</summary>
    MalformedBody
}

/// <summary>The outcome of binding a request, either a value or a failure with a message.</summary>
public readonly record struct EndpointBindingResult<T>(T? Value, EndpointBindingFailure? Failure, string? Message)
{
    /// <summary>Whether a value was bound.</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Binds a request record from route values, the query string, and a JSON body.
/// </summary>
/// <remarks>
/// This is deliberately small. It exists because module endpoints publish bare
/// <see cref="RequestDelegate"/> handlers rather than typed lambdas: a typed lambda makes
/// RequestDelegateFactory publish the handler's own <see cref="MethodInfo"/> into endpoint metadata,
/// which API Explorer then retains for the host service-provider lifetime and which the endpoint
/// ownership guards reject. Binding therefore cannot be delegated to the framework here, and this
/// type covers exactly the shapes first-party endpoints use.
/// <para>
/// Precedence is route, then body, then query, then the parameter's own default. Route wins over the
/// body so a route-addressed resource identifier cannot be contradicted by the payload.
/// </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2067",
    Justification = SuppressionReason)]
[UnconditionalSuppressMessage("Trimming", "IL2070",
    Justification = SuppressionReason)]
[UnconditionalSuppressMessage("Trimming", "IL2075",
    Justification = SuppressionReason)]
[UnconditionalSuppressMessage("Trimming", "IL2087",
    Justification = SuppressionReason)]
[UnconditionalSuppressMessage("Trimming", "IL2090",
    Justification = SuppressionReason)]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = SuppressionReason)]
public static class EndpointRequestBinder
{
    private const string SuppressionReason =
        "This is the reflective binder. Its public entry point is annotated RequiresUnreferencedCode " +
        "and RequiresDynamicCode, so a trimmed or AOT build is told at the boundary rather than here. " +
        "Projects running the source generator bind through emitted code and never reach these paths.";

    // Weak-keyed on purpose. Contract types ship in each domain's collectible .Api assembly, so a
    // strong static reference here would root that assembly's load context for host lifetime.
    // ConditionalWeakTable uses ephemerons, so ConstructorInfo referencing its own declaring Type
    // does not keep the key alive.
    private static readonly ConditionalWeakTable<Type, ConstructorInfo> Constructors = new();

    /// <summary>Binds a request contract from the route values, the query string, and the body.</summary>
    /// <summary>
    /// Reads and deserializes the request body, applying the body mode's media-type rules.
    /// </summary>
    /// <remarks>
    /// Public so a generated binder can reuse it rather than reimplementing the rules. The
    /// media-type behaviour is subtle enough that two implementations of it would drift.
    /// </remarks>
    /// <returns>The deserialized body, or a failure describing why it could not be read.</returns>
    public static async ValueTask<(bool Succeeded, T? Body, EndpointBindingResult<T> Failure)> ReadBodyAsync<T>(
        HttpContext context,
        JsonSerializerOptions jsonOptions,
        EndpointBodyMode bodyMode)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        if (bodyMode is EndpointBodyMode.None)
            return (true, default, default);

        var declared = !string.IsNullOrWhiteSpace(context.Request.ContentType);
        var isJson = declared && IsJsonContentType(context.Request.ContentType);
        var unsupported = bodyMode switch
        {
            EndpointBodyMode.Optional => false,
            EndpointBodyMode.RequiredWithContentType or EndpointBodyMode.OptionalWithContentType => !isJson,
            _ => declared && !isJson
        };

        if (unsupported)
        {
            return (false, default, new(default, EndpointBindingFailure.UnsupportedMediaType,
                "The request content type must be application/json."));
        }

        if (bodyMode is EndpointBodyMode.Optional or EndpointBodyMode.OptionalWithContentType && !isJson)
            return (true, default, default);

        T? body;
        try
        {
            // The JsonTypeInfo overload is the AOT-safe one: with a source-generated context the
            // resolver already knows this type and nothing is discovered at runtime.
            var typeInfo = jsonOptions.GetTypeInfo(typeof(T));
            body = (T?)await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted);
        }
        catch (JsonException exception)
        {
            var message = exception.Message.Replace(" Path: $ |", "", StringComparison.Ordinal);
            return (false, default, new(default, EndpointBindingFailure.MalformedBody, message));
        }

        if (body is null && bodyMode is EndpointBodyMode.Required or EndpointBodyMode.RequiredWithContentType)
            return (false, default, new(default, EndpointBindingFailure.MissingBody, "A request body is required."));

        return (true, body, default);
    }

    /// <summary>Binds a request contract from the route values, the query string, and the body.</summary>
    /// <remarks>
    /// Reflective, and annotated so a trimmed or AOT build says so rather than failing at runtime.
    /// The source generator emits a binder per contract that needs none of this; a project that runs
    /// the generator never reaches here.
    /// </remarks>
    [RequiresUnreferencedCode("Binds contracts by reflecting over their constructors and properties. Use the source generator, which emits a binder per contract.")]
    [RequiresDynamicCode("Constructs collection types at runtime. Use the source generator, which emits a binder per contract.")]
    public static async ValueTask<EndpointBindingResult<T>> BindAsync<T>(
        HttpContext context,
        JsonSerializerOptions jsonOptions,
        EndpointBodyMode bodyMode,
        EndpointValueBinders? valueBinders = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        // Body reading is shared with generated binders, so the media-type rules have exactly one
        // implementation and the two paths cannot drift apart.
        var read = await ReadBodyAsync<T>(context, jsonOptions, bodyMode);
        if (!read.Succeeded)
            return read.Failure;

        object? body = read.Body;

        var constructor = Constructors.GetValue(typeof(T), SelectConstructor);
        var parameters = constructor.GetParameters();

        // A contract declared with init-only properties rather than positional parameters is bound by
        // assignment: the deserialized body is kept and route values are applied over it.
        if (parameters.Length == 0)
            return new(BindProperties<T>(body, context, valueBinders), null, null);
        var arguments = new object?[parameters.Length];
        var routeValues = context.Request.RouteValues;
        var query = context.Request.Query;

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var name = parameter.Name!;

            // A positional record can carry the attribute on the parameter ([FromHeader] string x)
            // or on the generated property ([property: FromHeader] string x). Both are idiomatic, so
            // both are honoured.
            var declared = parameter.GetCustomAttribute<BindFromAttribute>()
                           ?? typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                               ?.GetCustomAttribute<BindFromAttribute>();
            if (declared is not null)
            {
                arguments[index] = BindDeclared(context, declared, name, parameter.ParameterType, valueBinders);
                continue;
            }

            if (TryGetRouteValue(routeValues, name, out var routeValue))
            {
                arguments[index] = Convert(routeValue, parameter.ParameterType, name, valueBinders);
                continue;
            }

            if (body is not null)
            {
                arguments[index] = ReadProperty(body, name, parameter.ParameterType);
                continue;
            }

            if (TryGetCollection(query, name, parameter.ParameterType, valueBinders, out var collection))
            {
                arguments[index] = collection;
                continue;
            }

            if (TryGetQueryValue(query, name, out var queryValue))
            {
                arguments[index] = Convert(queryValue, parameter.ParameterType, name, valueBinders);
                continue;
            }

            arguments[index] = parameter.HasDefaultValue
                ? parameter.DefaultValue
                : DefaultOf(parameter.ParameterType);
        }

        return new((T)constructor.Invoke(arguments), null, null);
    }

    private static T BindProperties<T>(object? body, HttpContext context, EndpointValueBinders? valueBinders)
    {
        var instance = body ?? Activator.CreateInstance(typeof(T))!;
        var routeValues = context.Request.RouteValues;
        var query = context.Request.Query;

        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite)
                continue;

            var declared = property.GetCustomAttribute<BindFromAttribute>();
            if (declared is not null)
                property.SetValue(instance, BindDeclared(context, declared, property.Name, property.PropertyType, valueBinders));
            else if (TryGetRouteValue(routeValues, property.Name, out var routeValue))
                property.SetValue(instance, Convert(routeValue, property.PropertyType, property.Name, valueBinders));
            else if (body is null && TryGetCollection(query, property.Name, property.PropertyType, valueBinders, out var collection))
                property.SetValue(instance, collection);
            else if (body is null && TryGetQueryValue(query, property.Name, out var queryValue))
                property.SetValue(instance, Convert(queryValue, property.PropertyType, property.Name, valueBinders));
        }

        return (T)instance;
    }

    private static ConstructorInfo SelectConstructor(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length != 1)
        {
            throw new InvalidOperationException(
                $"'{type.FullName}' must declare exactly one public constructor to be bound from a request; found {constructors.Length}.");
        }

        return constructors[0];
    }

    private static object? ReadProperty(object source, string name, Type targetType)
    {
        var property = source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return property is null ? DefaultOf(targetType) : property.GetValue(source);
    }

    private static bool TryGetRouteValue(RouteValueDictionary routeValues, string name, out string? value)
    {
        foreach (var entry in routeValues)
        {
            if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = entry.Value?.ToString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static bool TryGetQueryValue(IQueryCollection query, string name, out string? value)
    {
        foreach (var entry in query)
        {
            if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = entry.Value.ToString();
            return true;
        }

        value = null;
        return false;
    }

    private static object? Convert(string? value, Type targetType, string parameterName, EndpointValueBinders? valueBinders)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value is null)
            return DefaultOf(targetType);

        if (underlying == typeof(string))
            return value;

        // A blank query value for a nullable parameter means "absent", matching the previous
        // per-module helpers, which returned null rather than failing.
        if (value.Length == 0)
            return DefaultOf(targetType);

        if (underlying == typeof(bool))
            return bool.TryParse(value, out var boolean) ? boolean : DefaultOf(targetType);
        if (underlying == typeof(int))
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer : DefaultOf(targetType);
        if (underlying == typeof(long))
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue) ? longValue : DefaultOf(targetType);
        if (underlying == typeof(Guid))
            return Guid.TryParse(value, out var guid) ? guid : DefaultOf(targetType);
        if (underlying.IsEnum)
            return Enum.TryParse(underlying, value, ignoreCase: true, out var parsed) ? parsed : DefaultOf(targetType);
        if (underlying == typeof(DateTimeOffset))
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out var instant) ? instant : DefaultOf(targetType);

        // A registered parser wins over the built-in fallbacks, so a host can override how one of
        // its own types is read without forking the binder.
        if (valueBinders is not null && valueBinders.Handles(underlying))
            return valueBinders.TryParse(underlying, value, CultureInfo.InvariantCulture, out var custom) ? custom : DefaultOf(targetType);

        if (typeof(IParsable<>).MakeGenericType(underlying).IsAssignableFrom(underlying))
            return TryParsable(underlying, value, out var parsable) ? parsable : DefaultOf(targetType);

        throw new InvalidOperationException(
            $"Request parameter '{parameterName}' has unsupported type '{targetType.FullName}'. " +
            $"Implement IParsable<{underlying.Name}>, or register a parser with " +
            "AddNativeEndpoints(o => o.ValueBinders.Add<T>(...)), rather than widening the binder implicitly.");
    }

    /// <summary>Reads a member whose source was declared with a <see cref="BindFromAttribute"/>.</summary>
    private static object? BindDeclared(
        HttpContext context,
        BindFromAttribute declared,
        string memberName,
        Type targetType,
        EndpointValueBinders? valueBinders)
    {
        var key = declared.Name ?? memberName;
        switch (declared.Source)
        {
            case EndpointBindingSource.Route:
                return TryGetRouteValue(context.Request.RouteValues, key, out var route)
                    ? Convert(route, targetType, memberName, valueBinders)
                    : DefaultOf(targetType);

            case EndpointBindingSource.Query:
                if (TryGetCollection(context.Request.Query, key, targetType, valueBinders, out var many))
                    return many;
                return TryGetQueryValue(context.Request.Query, key, out var single)
                    ? Convert(single, targetType, memberName, valueBinders)
                    : DefaultOf(targetType);

            case EndpointBindingSource.Header:
                if (!context.Request.Headers.TryGetValue(key, out var header))
                    return DefaultOf(targetType);
                return ElementType(targetType) is not null
                    ? BuildCollection(targetType, header!, memberName, valueBinders)
                    : Convert(header.ToString(), targetType, memberName, valueBinders);

            case EndpointBindingSource.Claim:
                // An unauthenticated request simply has no claims; that is an absent value, not a
                // binding failure. Authorization decides whether absence is allowed.
                var claims = context.User?.FindAll(key).Select(claim => claim.Value).ToArray() ?? [];
                if (ElementType(targetType) is not null)
                    return BuildCollection(targetType, claims, memberName, valueBinders);
                return claims.Length == 0
                    ? DefaultOf(targetType)
                    : Convert(claims[0], targetType, memberName, valueBinders);

            default:
                return DefaultOf(targetType);
        }
    }

    /// <summary>The element type when <paramref name="type"/> is a bindable collection shape.</summary>
    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (!type.IsGenericType)
            return null;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(List<>) || definition == typeof(IReadOnlyList<>)
            || definition == typeof(IList<>) || definition == typeof(IEnumerable<>) || definition == typeof(ICollection<>)
            ? type.GetGenericArguments()[0]
            : null;
    }

    private static bool TryGetCollection(
        IQueryCollection query,
        string name,
        Type targetType,
        EndpointValueBinders? valueBinders,
        out object? value)
    {
        value = null;
        if (ElementType(targetType) is null)
            return false;

        foreach (var entry in query)
        {
            if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = BuildCollection(targetType, entry.Value!, name, valueBinders);
            return true;
        }

        // A collection parameter with no values present binds empty rather than null, so a handler
        // can enumerate it without a null check.
        value = BuildCollection(targetType, [], name, valueBinders);
        return true;
    }

    /// <summary>
    /// Builds an array or list from repeated request values.
    /// </summary>
    /// <remarks>
    /// Repeated keys only: <c>?tag=a&amp;tag=b</c>. A comma inside one value is part of that value,
    /// because guessing that commas separate is exactly the kind of implicit behavior that makes a
    /// binder unpredictable.
    /// </remarks>
    private static object BuildCollection(Type targetType, IEnumerable<string?> raw, string memberName, EndpointValueBinders? valueBinders)
    {
        var element = ElementType(targetType)!;
        var items = raw.Where(item => item is not null).ToArray();
        var array = Array.CreateInstance(element, items.Length);
        for (var index = 0; index < items.Length; index++)
            array.SetValue(Convert(items[index], element, memberName, valueBinders), index);

        if (targetType.IsArray)
            return array;

        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        foreach (var item in array)
            list.Add(item);
        return list;
    }

    /// <summary>Resolves and caches an <see cref="IParsable{TSelf}"/> TryParse for a type.</summary>
    private static readonly ConditionalWeakTable<Type, MethodInfo> Parsables = new();

    private static bool TryParsable(Type type, string raw, out object? value)
    {
        var method = Parsables.GetValue(type, static target =>
            target.GetMethod(
                "TryParse",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [typeof(string), typeof(IFormatProvider), target.MakeByRefType()],
                modifiers: null)!);

        if (method is null)
        {
            value = null;
            return false;
        }

        var arguments = new object?[] { raw, CultureInfo.InvariantCulture, null };
        var parsed = (bool)method.Invoke(null, arguments)!;
        value = parsed ? arguments[2] : null;
        return parsed;
    }

    private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static bool IsJsonContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) &&
        string.Equals(contentType.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase);
}

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
    MalformedBody,

    /// <summary>
    /// A typed route or query value did not parse. Raised only under strict parsing.
    /// </summary>
    InvalidTypedValue
}

/// <summary>The outcome of binding a request, either a value or a failure with a message.</summary>
/// <param name="Value">The bound contract, when binding succeeded.</param>
/// <param name="Failure">Why binding failed, or null when it succeeded.</param>
/// <param name="Message">The failure message to report.</param>
/// <param name="Key">Names the offending value for failures that have one, in its wire form.</param>
public readonly record struct EndpointBindingResult<T>(T? Value, EndpointBindingFailure? Failure, string? Message, string? Key = null)
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
    public static async ValueTask<EndpointBodyResult<T>> ReadBodyAsync<T>(
        HttpContext context,
        JsonSerializerOptions jsonOptions,
        EndpointBodyMode bodyMode)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        if (bodyMode is EndpointBodyMode.None)
            return new(true, default, default, null);

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
            return new(false, default, new(default, EndpointBindingFailure.UnsupportedMediaType,
                "The request content type must be application/json."), null);
        }

        if (bodyMode is EndpointBodyMode.Optional or EndpointBodyMode.OptionalWithContentType && !isJson)
            return new(true, default, default, null);

        T? body;
        HashSet<string>? supplied = null;
        try
        {
            // Buffered so the payload can be read twice: once to deserialize, once to record which
            // properties the caller actually sent. Without that second pass, a property sent as null
            // and a property omitted entirely are indistinguishable, and an omitted one would stop
            // falling through to the query string.
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind is JsonValueKind.Object)
            {
                supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject())
                    supplied.Add(property.Name);
            }

            var typeInfo = jsonOptions.GetTypeInfo(typeof(T));
            body = (T?)document.Deserialize(typeInfo);
        }
        catch (JsonException exception)
        {
            var message = exception.Message.Replace(" Path: $ |", "", StringComparison.Ordinal);
            return new(false, default, new(default, EndpointBindingFailure.MalformedBody, message), null);
        }

        if (body is null && bodyMode is EndpointBodyMode.Required or EndpointBodyMode.RequiredWithContentType)
            return new(false, default, new(default, EndpointBindingFailure.MissingBody, "A request body is required."), null);

        return new(true, body, default, supplied);
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
        EndpointBindingOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        // Body reading is shared with generated binders, so the media-type rules have exactly one
        // implementation and the two paths cannot drift apart.
        var read = await ReadBodyAsync<T>(context, jsonOptions, options.BodyMode);
        if (!read.Succeeded)
            return read.Failure;

        object? body = read.Body;
        var supplied = read.SuppliedProperties;
        var valueBinders = options.ValueBinders;

        var strict = options.StrictTypedParsing;
        try
        {
            return new(BindContract<T>(body, supplied, context, valueBinders, strict), null, null);
        }
        catch (EndpointStrictValueException failure)
        {
            // The reported name is the wire form the query string documents, not the Pascal-cased
            // constructor parameter it binds into.
            return new(default, EndpointBindingFailure.InvalidTypedValue,
                $"Value [{failure.RawValue}] is not valid for a [{failure.TypeName}] property!",
                JsonNamingPolicy.CamelCase.ConvertName(failure.Name));
        }
    }

    private static T BindContract<T>(
        object? body,
        IReadOnlySet<string>? supplied,
        HttpContext context,
        EndpointValueBinders? valueBinders,
        bool strict)
    {
        var constructor = Constructors.GetValue(typeof(T), SelectConstructor);
        var parameters = constructor.GetParameters();

        // A contract declared with init-only properties rather than positional parameters is bound by
        // assignment: the deserialized body is kept and route values are applied over it.
        if (parameters.Length == 0)
            return BindProperties<T>(body, supplied, context, valueBinders, strict);
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
                arguments[index] = BindDeclared(context, declared, name, parameter.ParameterType, valueBinders, strict);
                continue;
            }

            if (TryGetRouteValue(routeValues, name, out var routeValue))
            {
                arguments[index] = Convert(routeValue, parameter.ParameterType, name, valueBinders, strict);
                continue;
            }

            if (body is not null && SuppliedByBody(supplied, name))
            {
                arguments[index] = ReadProperty(body, name, parameter.ParameterType);
                continue;
            }

            if (TryGetCollection(query, name, parameter.ParameterType, valueBinders, strict, out var collection))
            {
                arguments[index] = collection;
                continue;
            }

            if (TryGetQueryValue(query, name, out var queryValue))
            {
                arguments[index] = Convert(queryValue, parameter.ParameterType, name, valueBinders, strict);
                continue;
            }

            arguments[index] = parameter.HasDefaultValue
                ? parameter.DefaultValue
                : AbsentValue(parameter.ParameterType, name, strict);
        }

        return (T)constructor.Invoke(arguments);
    }

    private static T BindProperties<T>(object? body, IReadOnlySet<string>? supplied, HttpContext context, EndpointValueBinders? valueBinders, bool strict)
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
                property.SetValue(instance, BindDeclared(context, declared, property.Name, property.PropertyType, valueBinders, strict));
            else if (TryGetRouteValue(routeValues, property.Name, out var routeValue))
                property.SetValue(instance, Convert(routeValue, property.PropertyType, property.Name, valueBinders, strict));
            else if (!SuppliedByBody(supplied, property.Name) && TryGetCollection(query, property.Name, property.PropertyType, valueBinders, strict, out var collection))
                property.SetValue(instance, collection);
            else if (!SuppliedByBody(supplied, property.Name) && TryGetQueryValue(query, property.Name, out var queryValue))
                property.SetValue(instance, Convert(queryValue, property.PropertyType, property.Name, valueBinders, strict));
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

    /// <summary>Whether the caller actually sent this property. Null means the body was not an object.</summary>
    private static bool SuppliedByBody(IReadOnlySet<string>? supplied, string name) =>
        supplied is null || supplied.Contains(name);

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

    private static object? Convert(string? value, Type targetType, string parameterName, EndpointValueBinders? valueBinders, bool strict = false)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value is null)
            return DefaultOf(targetType);

        if (underlying == typeof(string))
            return value;

        // A blank value means "absent" under the lenient default. Under strict parsing a blank
        // value for a typed parameter is a value the caller sent that cannot be read, so it is
        // reported rather than quietly becoming zero.
        if (value.Length == 0)
            return strict ? throw new EndpointStrictValueException(parameterName, value, underlying.Name) : DefaultOf(targetType);

        if (underlying == typeof(bool))
            return bool.TryParse(value, out var boolean) ? boolean : Fallback(value, targetType, underlying, parameterName, strict);
        if (underlying == typeof(int))
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer : Fallback(value, targetType, underlying, parameterName, strict);
        if (underlying == typeof(long))
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue) ? longValue : Fallback(value, targetType, underlying, parameterName, strict);
        if (underlying == typeof(Guid))
            return Guid.TryParse(value, out var guid) ? guid : Fallback(value, targetType, underlying, parameterName, strict);
        if (underlying.IsEnum)
            return Enum.TryParse(underlying, value, ignoreCase: true, out var parsed) ? parsed : Fallback(value, targetType, underlying, parameterName, strict);
        if (underlying == typeof(DateTimeOffset))
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out var instant) ? instant : Fallback(value, targetType, underlying, parameterName, strict);

        // A registered parser wins over the built-in fallbacks, so a host can override how one of
        // its own types is read without forking the binder.
        if (valueBinders is not null && valueBinders.Handles(underlying))
            return valueBinders.TryParse(underlying, value, CultureInfo.InvariantCulture, out var custom) ? custom : Fallback(value, targetType, underlying, parameterName, strict);

        if (typeof(IParsable<>).MakeGenericType(underlying).IsAssignableFrom(underlying))
            return TryParsable(underlying, value, out var parsable) ? parsable : Fallback(value, targetType, underlying, parameterName, strict);

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
        EndpointValueBinders? valueBinders,
        bool strict)
    {
        var key = declared.Name ?? memberName;
        switch (declared.Source)
        {
            case EndpointBindingSource.Route:
                return TryGetRouteValue(context.Request.RouteValues, key, out var route)
                    ? Convert(route, targetType, memberName, valueBinders, strict)
                    : AbsentValue(targetType, memberName, strict);

            case EndpointBindingSource.Query:
                if (TryGetCollection(context.Request.Query, key, targetType, valueBinders, strict, out var many))
                    return many;
                return TryGetQueryValue(context.Request.Query, key, out var single)
                    ? Convert(single, targetType, memberName, valueBinders, strict)
                    : AbsentValue(targetType, memberName, strict);

            case EndpointBindingSource.Header:
                if (!context.Request.Headers.TryGetValue(key, out var header))
                    return AbsentValue(targetType, memberName, strict);
                return ElementType(targetType) is not null
                    ? BuildCollection(targetType, header!, memberName, valueBinders, strict)
                    : Convert(header.ToString(), targetType, memberName, valueBinders, strict);

            case EndpointBindingSource.Claim:
                // An unauthenticated request simply has no claims; that is an absent value, not a
                // binding failure. Authorization decides whether absence is allowed.
                var claims = context.User?.FindAll(key).Select(claim => claim.Value).ToArray() ?? [];
                if (ElementType(targetType) is not null)
                    return BuildCollection(targetType, claims, memberName, valueBinders, strict);
                return claims.Length == 0
                    ? AbsentValue(targetType, memberName, strict)
                    : Convert(claims[0], targetType, memberName, valueBinders, strict);

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
        bool strict,
        out object? value)
    {
        value = null;
        if (ElementType(targetType) is null)
            return false;

        foreach (var entry in query)
        {
            if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = BuildCollection(targetType, entry.Value!, name, valueBinders, strict);
            return true;
        }

        // A collection parameter with no values present binds empty rather than null, so a handler
        // can enumerate it without a null check.
        value = BuildCollection(targetType, [], name, valueBinders, strict);
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
    private static object BuildCollection(Type targetType, IEnumerable<string?> raw, string memberName, EndpointValueBinders? valueBinders, bool strict)
    {
        var element = ElementType(targetType)!;
        var items = raw.Where(item => item is not null).ToArray();
        var array = Array.CreateInstance(element, items.Length);
        for (var index = 0; index < items.Length; index++)
            array.SetValue(Convert(items[index], element, memberName, valueBinders, strict), index);

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

    /// <summary>Either the parameter's default, or a strict-mode failure naming the value.</summary>
    private static object? Fallback(string value, Type targetType, Type underlying, string parameterName, bool strict) =>
        strict ? throw new EndpointStrictValueException(parameterName, value, underlying.Name) : DefaultOf(targetType);

    /// <summary>The value of a member no request source supplied at all.</summary>
    /// <remarks>
    /// Under strict parsing an absent non-nullable typed value is a failure: the caller was required
    /// to send something readable and sent nothing. This mirrors how the generated converters treat
    /// an absent raw value, so the two binders agree. Everything else — nullables, strings,
    /// collections, and types read only through a registered parser — binds its default, exactly as
    /// the generated path does for them.
    /// </remarks>
    private static object? AbsentValue(Type targetType, string memberName, bool strict) =>
        strict && RejectsAbsence(targetType)
            ? throw new EndpointStrictValueException(memberName, string.Empty, targetType.Name)
            : DefaultOf(targetType);

    /// <summary>Whether the type's converter rejects absence under strict parsing.</summary>
    /// <remarks>
    /// Probed through the implemented interfaces rather than <c>MakeGenericType</c>:
    /// <c>IParsable&lt;TSelf&gt;</c> constrains its own argument, so closing it over a type that
    /// does not implement it throws instead of answering no.
    /// </remarks>
    private static bool RejectsAbsence(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null &&
        (type == typeof(bool) || type == typeof(int) || type == typeof(long)
         || type == typeof(Guid) || type == typeof(DateTimeOffset) || type.IsEnum
         || System.Array.Exists(type.GetInterfaces(), static candidate =>
             candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IParsable<>)));

    private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static bool IsJsonContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) &&
        string.Equals(contentType.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase);
}

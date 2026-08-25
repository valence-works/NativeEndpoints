using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace NativeEndpoints;

/// <summary>
/// Reads and converts single request values without reflection.
/// </summary>
/// <remarks>
/// What generated binders call. Every method here is statically typed, so a trimmer can see exactly
/// what is used and native AOT has nothing to resolve at runtime. The reflective binder produces the
/// same results by a slower route; the conformance suite asserts they agree.
/// </remarks>
#pragma warning disable CS1591 // the converter overloads are named after the types they produce
public static class EndpointValue
{
    /// <summary>Whether the caller actually sent this body property.</summary>
    /// <remarks>
    /// Null means the body was not a JSON object, in which case anything deserialized from it counts
    /// as supplied. This is what separates "sent as null" from "not sent".
    /// </remarks>
    public static bool Supplied(IReadOnlySet<string>? supplied, string name) =>
        supplied is null || supplied.Contains(name);

    /// <summary>Reads a route value, or null when it is absent.</summary>
    public static string? Route(HttpContext context, string name)
    {
        foreach (var entry in context.Request.RouteValues)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                return entry.Value?.ToString();
        }

        return null;
    }

    /// <summary>Reads the first value for a query key, or null when the key is absent.</summary>
    public static string? Query(HttpContext context, string name)
    {
        foreach (var entry in context.Request.Query)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                return entry.Value.ToString();
        }

        return null;
    }

    /// <summary>Every value for a query key, in order.</summary>
    public static string?[] QueryValues(HttpContext context, string name)
    {
        foreach (var entry in context.Request.Query)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                return [.. entry.Value];
        }

        return [];
    }

    /// <summary>Reads a request header, or null when it is absent.</summary>
    public static string? Header(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    /// <summary>Every value for a request header, in order.</summary>
    public static string?[] HeaderValues(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out var value) ? [.. value] : [];

    /// <summary>Reads the first matching claim, or null. An unauthenticated request has none.</summary>
    public static string? Claim(HttpContext context, string type) =>
        context.User?.FindFirst(type)?.Value;

    /// <summary>Every matching claim, in order.</summary>
    public static string?[] ClaimValues(HttpContext context, string type) =>
        context.User is null ? [] : [.. context.User.FindAll(type).Select(claim => claim.Value)];

    /// <summary>A blank value means absent, matching the reflective binder.</summary>
    private static bool Absent(string? raw) => string.IsNullOrEmpty(raw);

    /// <summary>
    /// Either the type's default, or a strict-parsing failure naming the value the caller sent.
    /// </summary>
    /// <remarks>
    /// The single place both binders decide what an unreadable value means, so lenient and strict
    /// behaviour cannot differ between the reflective path and the generated one.
    /// </remarks>
    private static T Reject<T>(string? raw, bool strict, string? name, string typeName) =>
        strict
            ? throw new EndpointStrictValueException(name ?? "value", raw ?? string.Empty, typeName)
            : default!;

    // Every converter takes the same (raw, strict, name) shape so generated code can emit one form
    // for all of them. Absent means null for a nullable target and, under strict parsing, a failure
    // for a non-nullable one: a caller who sent nothing for a required typed value sent something
    // unreadable.

    public static string? String(string? raw, bool strict = false, string? name = null) => raw;

    public static bool Boolean(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? Reject<bool>(raw, strict, name, "Boolean")
        : bool.TryParse(raw, out var value) ? value : Reject<bool>(raw, strict, name, "Boolean");

    public static bool? NullableBoolean(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? null
        : bool.TryParse(raw, out var value) ? value : Reject<bool?>(raw, strict, name, "Boolean");

    public static int Int32(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? Reject<int>(raw, strict, name, "Int32")
        : int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : Reject<int>(raw, strict, name, "Int32");

    public static int? NullableInt32(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? null
        : int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : Reject<int?>(raw, strict, name, "Int32");

    public static long Int64(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? Reject<long>(raw, strict, name, "Int64")
        : long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : Reject<long>(raw, strict, name, "Int64");

    public static long? NullableInt64(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? null
        : long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : Reject<long?>(raw, strict, name, "Int64");

    public static Guid Guid(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? Reject<Guid>(raw, strict, name, "Guid")
        : System.Guid.TryParse(raw, out var value) ? value : Reject<Guid>(raw, strict, name, "Guid");

    public static Guid? NullableGuid(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? null
        : System.Guid.TryParse(raw, out var value) ? value : Reject<Guid?>(raw, strict, name, "Guid");

    public static DateTimeOffset DateTimeOffset(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? Reject<DateTimeOffset>(raw, strict, name, "DateTimeOffset")
        : System.DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : Reject<DateTimeOffset>(raw, strict, name, "DateTimeOffset");

    public static DateTimeOffset? NullableDateTimeOffset(string? raw, bool strict = false, string? name = null) =>
        Absent(raw) ? null
        : System.DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : Reject<DateTimeOffset?>(raw, strict, name, "DateTimeOffset");

    public static TEnum Enum<TEnum>(string? raw, bool strict = false, string? name = null) where TEnum : struct, Enum =>
        Absent(raw) ? Reject<TEnum>(raw, strict, name, typeof(TEnum).Name)
        : System.Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value) ? value : Reject<TEnum>(raw, strict, name, typeof(TEnum).Name);

    public static TEnum? NullableEnum<TEnum>(string? raw, bool strict = false, string? name = null) where TEnum : struct, Enum =>
        Absent(raw) ? null
        : System.Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value) ? value : Reject<TEnum?>(raw, strict, name, typeof(TEnum).Name);

    /// <summary>
    /// Parses through <see cref="IParsable{TSelf}"/>'s static abstract member.
    /// </summary>
    /// <remarks>
    /// A direct constrained call, so there is no <c>GetMethod</c> and nothing for a trimmer to miss.
    /// This is why <c>IParsable&lt;T&gt;</c> is the supported way to add a type.
    /// </remarks>
    public static T Parsable<T>(string? raw, bool strict = false, string? name = null) where T : IParsable<T> =>
        Absent(raw) ? Reject<T>(raw, strict, name, typeof(T).Name)
        : T.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : Reject<T>(raw, strict, name, typeof(T).Name);

    public static T? NullableParsable<T>(string? raw, bool strict = false, string? name = null) where T : struct, IParsable<T> =>
        Absent(raw) ? null
        : T.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : Reject<T?>(raw, strict, name, typeof(T).Name);

    /// <summary>Falls back to a parser registered at runtime, for a type with no other route in.</summary>
    public static T Registered<T>(string? raw, EndpointValueBinders? binders)
    {
        if (Absent(raw) || binders is null)
            return default!;

        return binders.TryParse(typeof(T), raw!, CultureInfo.InvariantCulture, out var value) && value is T typed
            ? typed
            : default!;
    }

    /// <summary>Projects raw values into an array using a generated element converter.</summary>
    public static T[] Array<T>(string?[] raw, Func<string?, T> convert)
    {
        var result = new T[raw.Length];
        for (var index = 0; index < raw.Length; index++)
            result[index] = convert(raw[index]);

        return result;
    }

    /// <summary>Projects raw values into a list using a generated element converter.</summary>
    public static List<T> List<T>(string?[] raw, Func<string?, T> convert)
    {
        var result = new List<T>(raw.Length);
        foreach (var item in raw)
            result.Add(convert(item));

        return result;
    }
}
#pragma warning restore CS1591

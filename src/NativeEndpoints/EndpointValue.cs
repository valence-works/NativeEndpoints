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

    public static string? String(string? raw) => raw;

    public static bool Boolean(string? raw) => !Absent(raw) && bool.TryParse(raw, out var value) && value;

    public static bool? NullableBoolean(string? raw) => Absent(raw) ? null : bool.TryParse(raw, out var value) ? value : null;

    public static int Int32(string? raw) =>
        !Absent(raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : default;

    public static int? NullableInt32(string? raw) =>
        !Absent(raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static long Int64(string? raw) =>
        !Absent(raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : default;

    public static long? NullableInt64(string? raw) =>
        !Absent(raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static Guid Guid(string? raw) => !Absent(raw) && System.Guid.TryParse(raw, out var value) ? value : default;

    public static Guid? NullableGuid(string? raw) => !Absent(raw) && System.Guid.TryParse(raw, out var value) ? value : null;

    public static DateTimeOffset DateTimeOffset(string? raw) =>
        !Absent(raw) && System.DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : default;

    public static DateTimeOffset? NullableDateTimeOffset(string? raw) =>
        !Absent(raw) && System.DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Enum parsing through the generic overload, which needs no runtime type lookup.</summary>
    public static TEnum Enum<TEnum>(string? raw) where TEnum : struct, Enum =>
        !Absent(raw) && System.Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value) ? value : default;

    public static TEnum? NullableEnum<TEnum>(string? raw) where TEnum : struct, Enum =>
        !Absent(raw) && System.Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value) ? value : null;

    /// <summary>
    /// Parses through <see cref="IParsable{TSelf}"/>'s static abstract member.
    /// </summary>
    /// <remarks>
    /// A direct constrained call, so there is no <c>GetMethod</c> and nothing for a trimmer to miss.
    /// This is why <c>IParsable&lt;T&gt;</c> is the supported way to add a type.
    /// </remarks>
    public static T Parsable<T>(string? raw) where T : IParsable<T> =>
        !Absent(raw) && T.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : default!;

    public static T? NullableParsable<T>(string? raw) where T : struct, IParsable<T> =>
        !Absent(raw) && T.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : null;

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

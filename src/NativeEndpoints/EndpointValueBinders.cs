using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace NativeEndpoints;

/// <summary>Parses a single request value into <typeparamref name="T"/>.</summary>
public delegate bool EndpointValueParser<T>(string value, IFormatProvider? provider, [MaybeNullWhen(false)] out T result);

/// <summary>
/// The types the binder can produce beyond the ones it knows natively.
/// </summary>
/// <remarks>
/// The seam that makes "no" a helpful answer. The binder covers a deliberately small set of shapes
/// and throws on anything else; registering a parser here is how a contract uses a domain type
/// without the binder growing to guess at it.
/// </remarks>
public sealed class EndpointValueBinders
{
    private readonly ConcurrentDictionary<Type, Func<string, IFormatProvider?, (bool Parsed, object? Value)>> _parsers = new();

    /// <summary>Registers a parser for <typeparamref name="T"/>, replacing any previous one.</summary>
    public EndpointValueBinders Add<T>(EndpointValueParser<T> parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parsers[typeof(T)] = (raw, provider) => parser(raw, provider, out var value) ? (true, value) : (false, null);
        return this;
    }

    /// <summary>Whether a parser is registered for <paramref name="type"/>.</summary>
    public bool Handles(Type type) => _parsers.ContainsKey(type);

    internal bool TryParse(Type type, string raw, IFormatProvider? provider, out object? value)
    {
        if (_parsers.TryGetValue(type, out var parser))
        {
            var (parsed, result) = parser(raw, provider);
            value = result;
            return parsed;
        }

        value = null;
        return false;
    }
}

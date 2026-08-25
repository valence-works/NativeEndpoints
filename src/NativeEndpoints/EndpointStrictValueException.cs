namespace NativeEndpoints;

/// <summary>
/// A typed request value did not parse under strict parsing.
/// </summary>
/// <remarks>
/// Internal control flow, not an error for a handler to catch: both the reflective binder and
/// generated ones throw it and convert it into an
/// <see cref="EndpointBindingFailure.InvalidTypedValue"/> result. Public only because generated code
/// lives in the consumer's assembly and has to name the type it catches.
/// </remarks>
public sealed class EndpointStrictValueException(string name, string rawValue, string typeName) : Exception
{
    /// <summary>The member that could not be bound.</summary>
    public string Name { get; } = name;

    /// <summary>The value the caller sent.</summary>
    public string RawValue { get; } = rawValue;

    /// <summary>The type it could not be read as.</summary>
    public string TypeName { get; } = typeName;
}

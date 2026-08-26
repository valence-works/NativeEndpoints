namespace NativeEndpoints;

/// <summary>How one operation binds its request.</summary>
/// <remarks>
/// Passed to both the reflective binder and generated ones, so the two cannot be handed different
/// settings. A record rather than loose parameters because binding has gained settings before and
/// will again, and every addition would otherwise be a breaking signature change.
/// </remarks>
/// <param name="BodyMode">How the request body is treated.</param>
/// <param name="StrictTypedParsing">
/// Rejects a typed route, query, header, or claim value that does not parse, rather than falling back to the
/// parameter's default. Off by default because the lenient behaviour is what most published
/// contracts already do, and turning it on can change an existing API's responses.
/// </param>
/// <param name="ValueBinders">Parsers for types the binder does not know natively.</param>
public sealed record EndpointBindingOptions(
    EndpointBodyMode BodyMode,
    bool StrictTypedParsing = false,
    EndpointValueBinders? ValueBinders = null);

/// <summary>The outcome of reading a request body.</summary>
/// <param name="Succeeded">False when <paramref name="Failure"/> describes why the body was rejected.</param>
/// <param name="Body">The deserialized body, or null when there was none.</param>
/// <param name="Failure">The failure to report, when reading did not succeed.</param>
/// <param name="SuppliedProperties">
/// The property names the caller actually sent, case-insensitively. Null when no JSON object was
/// read. This is what separates "sent as null" from "not sent", so an omitted property can fall
/// through to the query string while an explicit null stays null.
/// </param>
public readonly record struct EndpointBodyResult<T>(
    bool Succeeded,
    T? Body,
    EndpointBindingResult<T> Failure,
    IReadOnlySet<string>? SuppliedProperties);

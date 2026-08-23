namespace NativeEndpoints;

/// <summary>Names the group an endpoint was mapped in.</summary>
/// <remarks>
/// The whole of what the framework knows about who an endpoint belongs to. It is a label, not an
/// ownership model: it prefixes endpoint names so they stay unique across a host, supplies the
/// default OpenAPI tag, and identifies the endpoint in a lifetime violation report. A host that
/// needs a richer notion of ownership attaches its own metadata through an ordinary convention.
/// </remarks>
public sealed record EndpointGroupMetadata(string Name);

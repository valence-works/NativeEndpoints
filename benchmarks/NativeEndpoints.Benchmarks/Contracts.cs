namespace NativeEndpoints.Benchmarks;

/// <summary>
/// Response for the GET scenario, echoing every bound value. Echoing matters: a stack that skipped
/// or misbound a value would produce a different body, and the setup-time conformance check in each
/// benchmark class would throw. Nothing bound can be dead-code-eliminated into a fake win.
/// </summary>
public sealed record ItemView(int Id, string[] Tags, int Page);

/// <summary>The ~5-property JSON body for the POST scenario, shared by every stack that binds a record.</summary>
public sealed record CreateItem(string Name, string Sku, int Quantity, decimal Price, bool Active);

/// <summary>Response for the POST scenario, echoing the whole body for the same reason as <see cref="ItemView"/>.</summary>
public sealed record ItemCreated(string Name, string Sku, int Quantity, decimal Price, bool Active);

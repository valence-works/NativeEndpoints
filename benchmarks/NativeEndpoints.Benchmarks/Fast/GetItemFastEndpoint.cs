using FastEndpoints;

namespace NativeEndpoints.Benchmarks.Fast;

/// <summary>
/// FastEndpoints binds to settable properties rather than a constructor, so the GET contract is a
/// mutable class here while the other stacks share a positional record. The bound values and the
/// response are identical.
/// </summary>
public sealed class FastGetItem
{
    public int Id { get; set; }

    public string[] Tag { get; set; } = [];

    public int Page { get; set; }
}

/// <summary>The GET scenario on FastEndpoints: int from the route, string[] and int from the query.</summary>
public sealed class GetItemFastEndpoint : Endpoint<FastGetItem, ItemView>
{
    public override void Configure()
    {
        Get("/items/{id:int}");
        AllowAnonymous();
    }

    public override Task HandleAsync(FastGetItem req, CancellationToken ct) =>
        Send.OkAsync(new ItemView(req.Id, req.Tag, req.Page), ct);
}

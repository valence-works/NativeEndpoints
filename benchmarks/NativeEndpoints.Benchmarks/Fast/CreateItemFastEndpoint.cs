using FastEndpoints;

namespace NativeEndpoints.Benchmarks.Fast;

/// <summary>Mutable twin of <see cref="CreateItem"/>, for the same reason as <see cref="FastGetItem"/>.</summary>
public sealed class FastCreateItem
{
    public string Name { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    public bool Active { get; set; }
}

/// <summary>The POST scenario on FastEndpoints: JSON body in, small JSON echo out.</summary>
public sealed class CreateItemFastEndpoint : Endpoint<FastCreateItem, ItemCreated>
{
    public override void Configure()
    {
        Post("/items");
        AllowAnonymous();
    }

    public override Task HandleAsync(FastCreateItem req, CancellationToken ct) =>
        Send.OkAsync(new ItemCreated(req.Name, req.Sku, req.Quantity, req.Price, req.Active), ct);
}

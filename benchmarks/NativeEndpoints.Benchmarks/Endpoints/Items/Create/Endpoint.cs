namespace NativeEndpoints.Benchmarks.Endpoints.Items.Create;

/// <summary>
/// The POST scenario for both NativeEndpoints stacks: a JSON body into <see cref="CreateItem"/>,
/// a small JSON echo out. Same class for reflective and generated mapping, as with the GET.
/// </summary>
[Post("items")]
public sealed class Endpoint : ApiEndpoint<CreateItem, ItemCreated>
{
    public override Task<ItemCreated> HandleAsync(CreateItem request, CancellationToken cancellationToken) =>
        Task.FromResult(new ItemCreated(request.Name, request.Sku, request.Quantity, request.Price, request.Active));
}

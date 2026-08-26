namespace NativeEndpoints.Benchmarks.Endpoints.Items.Get;

/// <summary>Route + query binding: an int from the route, a string[] and an int from the query.</summary>
public sealed record GetItem(int Id, string[] Tag, int Page);

/// <summary>
/// The GET scenario for both NativeEndpoints stacks. The reflective host maps this class through
/// <c>MapEndpointsFrom</c> and the generated host through the emitted <c>Map()</c>, so the two
/// benchmarks measure the two binding paths over the exact same endpoint.
/// </summary>
[Get("items/{id:int}")]
public sealed class Endpoint : ApiEndpoint<GetItem, ItemView>
{
    public override Task<ItemView> HandleAsync(GetItem request, CancellationToken cancellationToken) =>
        Task.FromResult(new ItemView(request.Id, request.Tag, request.Page));
}

using NativeEndpoints;

namespace Aot.Endpoints.Widgets.Get;

public sealed record GetWidget(Guid WidgetId, string? Search, int[] Tag);
public sealed record WidgetView(Guid Id, string Name, int TagCount);

[Get("widgets/{widgetId}")]
public sealed class Endpoint(WidgetStore store) : ApiEndpoint<GetWidget, WidgetView>
{
    public override Task<WidgetView> HandleAsync(GetWidget request, CancellationToken cancellationToken) =>
        Task.FromResult(store.Find(request.WidgetId, request.Search, request.Tag.Length));
}

public sealed class WidgetStore
{
    public WidgetView Find(Guid id, string? search, int tagCount) => new(id, search ?? "widget", tagCount);
}

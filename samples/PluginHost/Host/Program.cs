using NativeEndpoints;
using PluginHost.Host;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNativeEndpoints();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<PluginEndpointDataSource>();
builder.Services.AddSingleton<PluginRegistry>();

// API Explorer caches its description collection for the host's lifetime. This tells it to rebuild
// when the route table publishes a new generation, so the OpenAPI document follows plugins in and out.
builder.Services.AddDynamicEndpointApiExplorerRefresh();

var app = builder.Build();

// The plugin route table joins the application's own. It starts empty.
((IEndpointRouteBuilder)app).DataSources.Add(app.Services.GetRequiredService<PluginEndpointDataSource>());

var pluginPath = Path.Combine(AppContext.BaseDirectory, "plugins", "PluginHost.Plugin.dll");
var registry = app.Services.GetRequiredService<PluginRegistry>();

app.MapGet("/admin/status", () => Results.Ok(new
{
    loaded = registry.LoadedName,
    endpoints = app.Services.GetRequiredService<PluginEndpointDataSource>().Endpoints.Count,
    previousContextCollected = registry.PreviousContextCollected
}));

app.MapPost("/admin/load", () =>
{
    registry.Load(pluginPath, routePrefix: "/api");
    return Results.Ok(new { loaded = registry.LoadedName });
});

// Best-effort only. See PluginRegistry.Unload: the request that triggers the unload is still on the
// stack, so this normally reports false even when the plugin is perfectly collectible.
app.MapPost("/admin/unload", () => Results.Ok(new
{
    unloaded = true,
    collectedImmediately = registry.Unload(),
    note = "Call POST /admin/collect from a separate request for the definitive answer."
}));

app.MapPost("/admin/collect", () => Results.Ok(new { collected = registry.Collect() }));

app.MapOpenApi();
app.MapGet("/", () => Results.Redirect("/admin/status")).ExcludeFromDescription();

registry.Load(pluginPath, routePrefix: "/api");

app.Run();

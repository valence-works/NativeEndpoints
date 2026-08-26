using System.Text.Json.Serialization;
using Aot.Endpoints.Widgets.Get;
using Aot.Endpoints.Widgets.Import;
using NativeEndpoints;
using NativeEndpoints.Generated;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddNativeEndpoints();
builder.Services.AddSingleton<WidgetStore>();

var app = builder.Build();

// A source-generated serializer context: the JSON half of the AOT story.
app.MapEndpointGroup("Aot", AotJson.Default).Map(routePrefix: "/api");

app.Run();

// Without a naming policy a context serializes PascalCase, while the non-context fallback uses
// JsonSerializerOptions.Web, which is camelCase. Adopting a context would otherwise change the wire.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GetWidget))]
[JsonSerializable(typeof(WidgetView))]
[JsonSerializable(typeof(ImportWidgets))]
[JsonSerializable(typeof(ImportView))]
internal partial class AotJson : JsonSerializerContext;

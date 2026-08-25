using Minimal;
using Minimal.Notes;
using NativeEndpoints;
using NativeEndpoints.OpenApi;
using NativeEndpoints.Generated;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNativeEndpoints();
builder.Services.AddOpenApi();
builder.Services.AddNativeEndpointsOpenApi();
builder.Services.AddSingleton<NoteStore>();
builder.Services.AddSingleton<IEndpointExceptionTranslator, NoteFaultTranslator>();

var app = builder.Build();

// Generated: MinimalEndpoints.Map names every endpoint class in this assembly explicitly, so there
// is no scan and nothing for the trimmer to be unable to see. The reflective equivalent still works:
//     app.MapEndpointGroup().MapEndpointsFrom(typeof(Program).Assembly, routePrefix: "/api");
app.MapEndpointGroup().Map(routePrefix: "/api");

app.MapOpenApi();
app.MapGet("/", () => Results.Redirect("/openapi/v1.json")).ExcludeFromDescription();

app.Run();

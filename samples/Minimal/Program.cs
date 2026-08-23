using Minimal;
using Minimal.Notes;
using NativeEndpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNativeEndpoints();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<NoteStore>();
builder.Services.AddSingleton<IEndpointExceptionTranslator, NoteFaultTranslator>();

var app = builder.Build();

// One line. The group is named after this assembly, and every ApiEndpointBase in it is mapped from
// its own attribute and namespace. Nothing is registered process-globally, and nothing survives the
// endpoint generation.
app.MapEndpointGroup().MapEndpointsFrom(typeof(Program).Assembly, routePrefix: "/api");

app.MapOpenApi();
app.MapGet("/", () => Results.Redirect("/openapi/v1.json")).ExcludeFromDescription();

app.Run();

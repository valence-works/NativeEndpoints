using Minimal.Notes;
using NativeEndpoints;

namespace Minimal.Endpoints.Notes.Create;

/// <summary>
/// Returns 201 with a Location header. The status travels with the result rather than being fixed by
/// the route, which is what ApiEndpointWithResult is for.
/// </summary>
[Post("notes")]
public sealed class Endpoint(NoteStore store) : ApiEndpointWithResult<CreateNote, NoteView>
{
    // The runtime status travels with the result, so the framework cannot infer what to document.
    // Say it explicitly, or the document claims 200 while callers receive 201.
    public override void Configure(ApiEndpointOptions options) =>
        options.SuccessStatus = StatusCodes.Status201Created;

    public override Task<EndpointResult<NoteView>> HandleAsync(CreateNote request, CancellationToken cancellationToken)
    {
        var note = store.Add(request.Title, request.Body);
        HttpContext.Response.Headers.Location = $"/api/notes/{note.Id}";

        return Task.FromResult(EndpointResult.Status(StatusCodes.Status201Created, NoteView.From(note)));
    }
}

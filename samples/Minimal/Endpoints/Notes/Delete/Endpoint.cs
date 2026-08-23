using Minimal.Notes;
using NativeEndpoints;

namespace Minimal.Endpoints.Notes.Delete;

/// <summary>Deriving from ApiEndpoint&lt;TRequest&gt; makes the response 204 No Content.</summary>
[Delete("notes/{noteId}")]
public sealed class Endpoint(NoteStore store) : ApiEndpoint<DeleteNote>
{
    // DELETE defaults to an optional body, which makes the document advertise a JSON request body
    // this endpoint never reads. Everything it needs is in the route.
    public override void Configure(ApiEndpointOptions options) =>
        options.BodyMode = EndpointBodyMode.None;

    public override Task HandleAsync(DeleteNote request, CancellationToken cancellationToken)
    {
        store.Remove(request.NoteId);
        return Task.CompletedTask;
    }
}

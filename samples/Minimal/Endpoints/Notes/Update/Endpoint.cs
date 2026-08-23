using Minimal.Notes;
using NativeEndpoints;

namespace Minimal.Endpoints.Notes.Update;

[Put("notes/{noteId}")]
public sealed class Endpoint(NoteStore store) : ApiEndpoint<UpdateNote, NoteView>
{
    public override Task<NoteView> HandleAsync(UpdateNote request, CancellationToken cancellationToken) =>
        Task.FromResult(NoteView.From(
            store.Replace(request.NoteId, request.Title, request.Body, request.Archived)));
}

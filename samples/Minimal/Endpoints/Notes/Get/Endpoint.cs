using Minimal.Notes;
using NativeEndpoints;

namespace Minimal.Endpoints.Notes.Get;

/// <summary>A missing note throws, and NoteFaultTranslator turns that into a 404.</summary>
[Get("notes/{noteId}")]
public sealed class Endpoint(NoteStore store) : ApiEndpoint<GetNote, NoteView>
{
    public override Task<NoteView> HandleAsync(GetNote request, CancellationToken cancellationToken) =>
        Task.FromResult(NoteView.From(store.Get(request.NoteId)));
}

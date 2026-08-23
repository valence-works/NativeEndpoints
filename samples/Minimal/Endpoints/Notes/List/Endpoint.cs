using Minimal.Notes;
using NativeEndpoints;

namespace Minimal.Endpoints.Notes.List;

/// <summary>The operation id is derived from this namespace: NotesList.</summary>
[Get("notes")]
public sealed class Endpoint(NoteStore store) : ApiEndpoint<ListNotes, NoteListView>
{
    public override Task<NoteListView> HandleAsync(ListNotes request, CancellationToken cancellationToken)
    {
        var notes = store.List(request.Search, request.IncludeArchived, request.Take);
        var view = new NoteListView(
            notes.Select(NoteView.From).ToArray(),
            store.Count(request.IncludeArchived));

        return Task.FromResult(view);
    }
}

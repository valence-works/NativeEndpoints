namespace Minimal.Endpoints.Notes.Get;

/// <summary>Bound from the route: /api/notes/{noteId}</summary>
public sealed record GetNote(Guid NoteId);

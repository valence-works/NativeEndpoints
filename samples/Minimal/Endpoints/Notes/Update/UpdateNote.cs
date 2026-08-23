namespace Minimal.Endpoints.Notes.Update;

/// <summary>
/// NoteId comes from the route and the rest from the body. Route wins over the body, so a caller
/// cannot contradict the resource they addressed.
/// </summary>
public sealed record UpdateNote(Guid NoteId, string Title, string Body, bool Archived);

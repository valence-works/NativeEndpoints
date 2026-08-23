namespace Minimal.Endpoints.Notes.Create;

/// <summary>Bound from the JSON body.</summary>
public sealed record CreateNote(string Title, string Body);

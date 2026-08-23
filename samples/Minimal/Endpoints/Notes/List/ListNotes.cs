namespace Minimal.Endpoints.Notes.List;

/// <summary>Bound from the query string: /api/notes?search=x&amp;includeArchived=true&amp;take=10</summary>
public sealed record ListNotes(string? Search, bool IncludeArchived, int Take);

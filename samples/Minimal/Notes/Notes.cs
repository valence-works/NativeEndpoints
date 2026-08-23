using System.Collections.Concurrent;

namespace Minimal.Notes;

/// <summary>A note. The whole domain of this sample.</summary>
public sealed record Note(Guid Id, string Title, string Body, bool Archived, DateTimeOffset CreatedAt);

/// <summary>The view returned to callers.</summary>
public sealed record NoteView(Guid Id, string Title, string Body, bool Archived, DateTimeOffset CreatedAt)
{
    public static NoteView From(Note note) => new(note.Id, note.Title, note.Body, note.Archived, note.CreatedAt);
}

/// <summary>A page of notes.</summary>
public sealed record NoteListView(IReadOnlyList<NoteView> Items, int Total);

/// <summary>Thrown when a note id does not resolve. Translated to a 404 by NoteFaultTranslator.</summary>
public sealed class NoteNotFoundException(Guid id) : Exception($"No note with id '{id}'.")
{
    public Guid Id { get; } = id;
}

/// <summary>An in-memory store, so the sample runs with no configuration at all.</summary>
public sealed class NoteStore
{
    private readonly ConcurrentDictionary<Guid, Note> _notes = new();

    public NoteStore()
    {
        Add("Read the binding page", "Route, then body, then query.");
        Add("Try unloading something", "The test kit measures it for you.");
    }

    public IReadOnlyList<Note> List(string? search, bool includeArchived, int take) =>
        _notes.Values
            .Where(note => includeArchived || !note.Archived)
            .Where(note => search is null || note.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(note => note.CreatedAt)
            .Take(take <= 0 ? 20 : take)
            .ToArray();

    public int Count(bool includeArchived) => _notes.Values.Count(note => includeArchived || !note.Archived);

    public Note Get(Guid id) =>
        _notes.TryGetValue(id, out var note) ? note : throw new NoteNotFoundException(id);

    public Note Add(string title, string body)
    {
        var note = new Note(Guid.CreateVersion7(), title, body, Archived: false, DateTimeOffset.UtcNow);
        _notes[note.Id] = note;
        return note;
    }

    public Note Replace(Guid id, string title, string body, bool archived)
    {
        var existing = Get(id);
        var updated = existing with { Title = title, Body = body, Archived = archived };
        _notes[id] = updated;
        return updated;
    }

    public void Remove(Guid id)
    {
        if (!_notes.TryRemove(id, out _))
            throw new NoteNotFoundException(id);
    }
}

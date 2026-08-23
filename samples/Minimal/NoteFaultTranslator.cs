using NativeEndpoints;
using Minimal.Notes;

namespace Minimal;

/// <summary>
/// Turns domain exceptions into responses.
/// </summary>
/// <remarks>
/// The mapping from exception to status is domain knowledge, so it lives beside the code that owns
/// the exception rather than in a filter every part of the application has to agree on. Returning
/// null means "not mine", so several translators can coexist without knowing about each other.
/// </remarks>
public sealed class NoteFaultTranslator : IEndpointExceptionTranslator
{
    public EndpointProblem? Translate(Exception exception) => exception switch
    {
        NoteNotFoundException found => EndpointProblem.General(StatusCodes.Status404NotFound, found.Message),
        _ => null
    };
}

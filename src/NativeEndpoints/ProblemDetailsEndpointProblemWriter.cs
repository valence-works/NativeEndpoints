using Microsoft.AspNetCore.Http;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.WebUtilities;

namespace NativeEndpoints;

/// <summary>Writes an <see cref="EndpointProblem"/> as an RFC 9457 problem document.</summary>
/// <remarks>
/// Registered by <c>AddNativeEndpoints</c> so a host that configures nothing still returns a sane
/// body on a binding failure. Replace <see cref="IEndpointProblemWriter"/> to own the wire shape.
/// </remarks>
public sealed class ProblemDetailsEndpointProblemWriter(IProblemDetailsService problemDetails) : IEndpointProblemWriter
{
    /// <summary>Writes the problem as an RFC 9457 document, keyed errors becoming extensions.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The fallback write is only reached when the host's IProblemDetailsService declines, and ProblemDetails is a framework type the host already serializes.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The fallback write is only reached when the host's IProblemDetailsService declines, and ProblemDetails is a framework type the host already serializes.")]
    public async Task WriteAsync(HttpContext context, EndpointProblem problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);

        context.Response.StatusCode = problem.StatusCode;
        var details = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = problem.StatusCode,
            Title = ReasonPhrases.GetReasonPhrase(problem.StatusCode)
        };

        foreach (var (key, messages) in problem.Errors)
            details.Extensions[key] = messages;

        var written = await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = details
        });

        if (!written)
            await context.Response.WriteAsJsonAsync(details, context.RequestAborted);
    }
}

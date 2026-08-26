using NativeEndpoints;

namespace Aot.Endpoints.Widgets.Import;

/// <summary>
/// A form contract carrying files, so CI's native-AOT publish covers the form and file branches too.
/// </summary>
/// <remarks>
/// The file members are the point: if the generator ever stops emitting for them, the endpoint falls
/// back to the reflective mapper and this project fails to publish rather than shipping a binder that
/// reflects at runtime.
/// </remarks>
public sealed record ImportWidgets(
    Guid BatchId,
    string Name,
    int Count,
    string[] Tag,
    IFormFile? Manifest,
    IFormFile[] Attachment);

public sealed record ImportView(Guid BatchId, string Name, int Count, int TagCount, string? Manifest, int Attachments);

[Post("widgets/{batchId}/import")]
public sealed class Endpoint : ApiEndpoint<ImportWidgets, ImportView>
{
    public override void Configure(ApiEndpointOptions options)
    {
        options.BodyKind = EndpointBodyKind.Form;

        // A token-authenticated API with no cookies to forge. Stated rather than assumed, because a
        // form endpoint will not map without an answer either way.
        options.RequireAntiforgery = false;
    }

    public override Task<ImportView> HandleAsync(ImportWidgets request, CancellationToken cancellationToken) =>
        Task.FromResult(new ImportView(
            request.BatchId, request.Name, request.Count, request.Tag.Length,
            request.Manifest?.FileName, request.Attachment.Length));
}

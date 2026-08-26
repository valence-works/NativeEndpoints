using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Billing.Flat;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The form binder at the unit level, for the paths a TestServer host cannot reach.
/// </summary>
/// <remarks>
/// TestServer does not enforce the server's own request-size limit, so the 413 path is unreachable
/// through a host even though Kestrel raises it in production. Driving the binder directly over a
/// stream that throws is what keeps that catch clause honest rather than aspirational.
/// </remarks>
public class FormBinderUnitTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly EndpointBindingOptions FormOptions =
        new(EndpointBodyMode.Required, BodyKind: EndpointBodyKind.Form);

    [Fact]
    public async Task A_body_the_server_rejects_as_too_large_is_reported_as_413_not_500()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=x";
        context.Request.Body = new ThrowingStream(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        var result = await EndpointRequestBinder.BindAsync<StrictForm>(context, Json, FormOptions);

        Assert.False(result.Succeeded);
        Assert.Equal(EndpointBindingFailure.RequestTooLarge, result.Failure);
    }

    [Fact]
    public async Task A_malformed_multipart_body_is_a_bad_request()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=x";
        context.Request.Body = new ThrowingStream(new InvalidDataException("Missing content-type boundary."));

        var result = await EndpointRequestBinder.BindAsync<StrictForm>(context, Json, FormOptions);

        Assert.False(result.Succeeded);
        Assert.Equal(EndpointBindingFailure.MalformedBody, result.Failure);
    }

    [Fact]
    public async Task A_non_form_content_type_is_unsupported_media()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";

        var result = await EndpointRequestBinder.BindAsync<StrictForm>(context, Json, FormOptions);

        Assert.False(result.Succeeded);
        Assert.Equal(EndpointBindingFailure.UnsupportedMediaType, result.Failure);
        Assert.Contains("multipart/form-data", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_json_endpoint_does_not_bind_from_a_form_it_was_handed()
    {
        // The kind gates the form step, not the content type alone. Without that gate a JSON endpoint
        // under an optional body mode would quietly start reading fields out of a posted form.
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new() { ["Page"] = "42" });

        var result = await EndpointRequestBinder.BindAsync<StrictForm>(
            context, Json, new EndpointBindingOptions(EndpointBodyMode.Optional));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value!.Page);
    }

    /// <summary>A request body that fails the moment it is read.</summary>
    private sealed class ThrowingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw failure;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw failure;

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

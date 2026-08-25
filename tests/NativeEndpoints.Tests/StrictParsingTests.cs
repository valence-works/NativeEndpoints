using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace NativeEndpoints.Tests;

public sealed record Typed(int Page, Guid? Filter);

/// <summary>
/// Strict parsing turns a value the caller sent but the binder cannot read into a reported failure
/// rather than a silent default. Ported because a real consumer needed it, and because the lenient
/// default quietly contradicted the promise that nothing misbinds silently.
/// </summary>
public class StrictParsingTests
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    [Fact]
    public async Task Lenient_parsing_falls_back_to_the_default()
    {
        var result = await Bind("?page=notanumber", strict: false);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value!.Page);
    }

    [Fact]
    public async Task Strict_parsing_reports_the_value_and_names_it()
    {
        var result = await Bind("?page=notanumber", strict: true);

        Assert.False(result.Succeeded);
        Assert.Equal(EndpointBindingFailure.InvalidTypedValue, result.Failure);
        Assert.Equal("Value [notanumber] is not valid for a [Int32] property!", result.Message);
        Assert.Equal("page", result.Key);   // the wire name, not the constructor parameter
    }

    [Fact]
    public async Task Strict_parsing_leaves_an_absent_nullable_alone()
    {
        // Absent is not the same as unreadable: a nullable the caller omitted is simply null.
        var result = await Bind("?page=1", strict: true);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value!.Filter);
    }

    [Fact]
    public async Task Strict_parsing_rejects_an_unreadable_nullable()
    {
        var result = await Bind("?page=1&filter=nope", strict: true);

        Assert.False(result.Succeeded);
        Assert.Equal("filter", result.Key);
    }

    private static async ValueTask<EndpointBindingResult<Typed>> Bind(string query, bool strict)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);

        return await EndpointRequestBinder.BindAsync<Typed>(
            context, Json, new EndpointBindingOptions(EndpointBodyMode.None, strict));
    }
}

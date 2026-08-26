using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// A repeated query key bound to a scalar takes the first value: <c>?page=1&amp;page=2</c> binds 1.
/// </summary>
/// <remarks>
/// <c>StringValues.ToString()</c> comma-joins a repeated key, and no scalar sensibly means "1,2":
/// it fails to parse, and under the lenient default that silently bound the type's zero —
/// contradicting the promise that nothing misbinds silently. Minimal APIs comma-join here too
/// (verified against a TestHost app: <c>int page</c> with <c>?page=1&amp;page=2</c> answers a bare
/// 400 because "1,2" fails to parse, and a <c>string</c> parameter binds "1,2"), so the join is a
/// framework accident rather than behavior worth matching; binding the first value is the
/// deliberate choice. Headers are different: HTTP defines a repeated field as one comma-separated
/// field, so a multi-valued header keeps the join.
/// </remarks>
public class RepeatedScalarQueryTests
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    [Fact]
    public async Task Lenient_repeated_scalar_key_binds_the_first_value_not_zero()
    {
        var result = await Bind("?page=1&page=2", strict: false);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value!.Page);
    }

    [Fact]
    public async Task Strict_repeated_scalar_key_binds_the_first_value_when_it_parses()
    {
        var result = await Bind("?page=1&page=2", strict: true);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value!.Page);
    }

    [Fact]
    public async Task Strict_repeated_scalar_key_rejects_an_unreadable_first_value()
    {
        var result = await Bind("?page=notanumber&page=2", strict: true);

        Assert.False(result.Succeeded);
        Assert.Equal(EndpointBindingFailure.InvalidTypedValue, result.Failure);
        Assert.Equal("Value [notanumber] is not valid for a [Int32] property!", result.Message);
        Assert.Equal("page", result.Key);
    }

    [Fact]
    public async Task A_single_value_still_binds_exactly_as_before()
    {
        var result = await Bind("?page=7", strict: false);

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.Value!.Page);
    }

    [Fact]
    public void The_generated_helper_reads_the_first_query_value()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?page=1&page=2");

        Assert.Equal("1", EndpointValue.Query(context, "page"));
    }

    [Fact]
    public void The_generated_helper_still_joins_a_multi_valued_header()
    {
        // Per HTTP semantics a repeated header field is one comma-separated field, so the join IS
        // the header's value; only query keys changed.
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Multi"] = new Microsoft.Extensions.Primitives.StringValues(["a", "b"]);

        Assert.Equal("a,b", EndpointValue.Header(context, "X-Multi"));
    }

    private static async ValueTask<EndpointBindingResult<Typed>> Bind(string query, bool strict)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);

        return await EndpointRequestBinder.BindAsync<Typed>(
            context, Json, new EndpointBindingOptions(EndpointBodyMode.None, strict));
    }
}

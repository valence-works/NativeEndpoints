using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using NativeEndpoints.OpenApi;
using Xunit;

namespace NativeEndpoints.Tests;

public enum TransformerShade
{
    Light,
    Dark
}

/// <summary>
/// The OpenAPI transformer turns <see cref="EndpointParameterMetadata"/> into document parameters:
/// the right location, requiredness, and schema per type, never a claim, and never overwriting a
/// parameter the document already describes.
/// </summary>
public class EndpointParameterTransformerTests
{
    [Fact]
    public async Task Route_query_and_header_parameters_are_written_with_their_locations()
    {
        var operation = new OpenApiOperation();

        await Transform(operation,
            new EndpointParameterMetadata("id", EndpointBindingSource.Route, typeof(int), Required: false),
            new EndpointParameterMetadata("filter", EndpointBindingSource.Query, typeof(Guid), Required: true),
            new EndpointParameterMetadata("X-Tenant", EndpointBindingSource.Header, typeof(string), Required: false));

        Assert.NotNull(operation.Parameters);
        Assert.Equal(3, operation.Parameters.Count);

        var route = operation.Parameters.Single(parameter => parameter.Name == "id");
        Assert.Equal(ParameterLocation.Path, route.In);
        Assert.True(route.Required); // a route parameter is required even when the metadata says optional

        var query = operation.Parameters.Single(parameter => parameter.Name == "filter");
        Assert.Equal(ParameterLocation.Query, query.In);
        Assert.True(query.Required);

        var header = operation.Parameters.Single(parameter => parameter.Name == "X-Tenant");
        Assert.Equal(ParameterLocation.Header, header.In);
        Assert.False(header.Required);
    }

    [Fact]
    public async Task An_int_is_described_as_integer_int32()
    {
        var schema = await DescribeOne(typeof(int));

        Assert.Equal(JsonSchemaType.Integer, schema.Type);
        Assert.Equal("int32", schema.Format);
    }

    [Fact]
    public async Task A_guid_is_described_as_string_uuid()
    {
        var schema = await DescribeOne(typeof(Guid));

        Assert.Equal(JsonSchemaType.String, schema.Type);
        Assert.Equal("uuid", schema.Format);
    }

    [Fact]
    public async Task An_enum_is_described_as_a_string_with_its_names()
    {
        var schema = await DescribeOne(typeof(TransformerShade));

        Assert.Equal(JsonSchemaType.String, schema.Type);
        Assert.NotNull(schema.Enum);
        Assert.Equal(["Light", "Dark"], schema.Enum.Select(name => name!.GetValue<string>()));
    }

    [Fact]
    public async Task An_array_is_described_as_an_array_of_its_element_schema()
    {
        var schema = await DescribeOne(typeof(int[]));

        Assert.Equal(JsonSchemaType.Array, schema.Type);
        var items = Assert.IsType<OpenApiSchema>(schema.Items);
        Assert.Equal(JsonSchemaType.Integer, items.Type);
        Assert.Equal("int32", items.Format);
    }

    [Fact]
    public async Task A_claim_sourced_parameter_is_not_written()
    {
        var operation = new OpenApiOperation();

        await Transform(operation,
            new EndpointParameterMetadata("sub", EndpointBindingSource.Claim, typeof(string), Required: true));

        // Nothing was described, so the transformer never even materializes the collection.
        Assert.Null(operation.Parameters);
    }

    [Fact]
    public async Task An_already_present_parameter_is_not_overwritten_case_insensitively()
    {
        var existing = new OpenApiParameter
        {
            Name = "ID",
            In = ParameterLocation.Query,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "host-owned" }
        };
        var operation = new OpenApiOperation { Parameters = [existing] };

        await Transform(operation,
            new EndpointParameterMetadata("id", EndpointBindingSource.Route, typeof(int), Required: true));

        var parameter = Assert.Single(operation.Parameters);
        Assert.Same(existing, parameter);
        Assert.Equal("host-owned", Assert.IsType<OpenApiSchema>(parameter.Schema).Format);
    }

    private static async Task<OpenApiSchema> DescribeOne(Type type)
    {
        var operation = new OpenApiOperation();
        await Transform(operation, new EndpointParameterMetadata("value", EndpointBindingSource.Query, type, Required: true));
        var parameter = Assert.Single(operation.Parameters!);
        return Assert.IsType<OpenApiSchema>(parameter.Schema);
    }

    private static Task Transform(OpenApiOperation operation, params EndpointParameterMetadata[] metadata)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new OpenApiOperationTransformerContext
        {
            DocumentName = "v1",
            Description = new ApiDescription
            {
                ActionDescriptor = new ActionDescriptor { EndpointMetadata = [.. metadata] }
            },
            ApplicationServices = services
        };

        return new EndpointParameterTransformer().TransformAsync(operation, context, CancellationToken.None);
    }
}

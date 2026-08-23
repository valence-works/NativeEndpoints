using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The operation identifier becomes the OpenAPI operationId and the endpoint name, so where it comes
/// from is a wire-visible contract rather than an implementation detail.
/// </summary>
public class OperationDerivationTests
{
    [Fact]
    public void Namespace_after_an_Endpoints_segment_supplies_the_operation() =>
        Assert.Equal("Billing_InvoicesGet", MapAndReadName<Billing.Endpoints.Invoices.Get.Endpoint>());

    [Fact]
    public void Class_name_is_used_when_there_is_no_Endpoints_segment() =>
        Assert.Equal("Billing_PostLedgerEntry", MapAndReadName<Billing.Flat.PostLedgerEntry>());

    [Fact]
    public void Declared_operation_wins_over_derivation() =>
        Assert.Equal("Billing_Explicit", MapAndReadName<Billing.Flat.DeclaredOperation>());

    private static string? MapAndReadName<TEndpoint>()
        where TEndpoint : ApiEndpointBase
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNativeEndpoints();

        var app = builder.Build();
        var routes = (IEndpointRouteBuilder)app;
        routes.MapEndpointGroup("Billing").MapEndpoint<TEndpoint>();

        return routes.DataSources
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .SingleOrDefault(name => name is not null);
    }
}

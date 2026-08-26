using System.Reflection;
using Billing.Flat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NativeEndpoints.Generated;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The raw shape end to end: the handler writes the response itself, and the pipeline adds nothing
/// on success but keeps the shared failure path. Both mapping paths are exercised, because the
/// reflective mapper and the generated registration dispatch raw endpoints through different code.
/// </summary>
public class RawEndpointTests : IAsyncDisposable
{
    private readonly IHost _reflective;
    private readonly IHost _generated;

    public RawEndpointTests()
    {
        _reflective = Host(group => group.MapEndpointsFrom(typeof(RawEndpointTests).Assembly));
        _generated = Host(group => group.Map());
    }

    [Fact]
    public async Task Both_mapping_paths_serve_the_handler_written_response_untouched()
    {
        foreach (var (label, host) in Hosts())
        {
            using var client = host.GetTestClient();
            var response = await client.GetAsync("/raw-export");

            Assert.True(202 == (int)response.StatusCode,
                $"{label} mapper returned {(int)response.StatusCode} for '/raw-export', expected 202.");
            Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.Equal("raw export", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_thrown_domain_exception_still_reaches_the_translator_pipeline()
    {
        foreach (var (label, host) in Hosts())
        {
            var (status, body) = await Send(host, "/raw-throw/conflict");

            Assert.True(409 == status, $"{label} mapper returned {status} for '/raw-throw/conflict', expected 409.");
            Assert.Contains("raw conflict", body);
        }
    }

    [Fact]
    public async Task An_untranslated_exception_is_the_sanitized_500()
    {
        foreach (var (label, host) in Hosts())
        {
            var (status, body) = await Send(host, "/raw-throw/other");

            Assert.True(500 == status, $"{label} mapper returned {status} for '/raw-throw/other', expected 500.");
            Assert.Contains("Unexpected error occurred", body);
            Assert.DoesNotContain("sensitive connection string detail", body);
        }
    }

    private (string Label, IHost Host)[] Hosts() => [("reflective", _reflective), ("generated", _generated)];

    private static async Task<(int Status, string Body)> Send(IHost host, string url)
    {
        using var client = host.GetTestClient();
        var response = await client.GetAsync(url);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed class ConflictTranslator : IEndpointExceptionTranslator
    {
        public EndpointProblem? Translate(Exception exception) =>
            exception is RawConflictException
                ? EndpointProblem.General(StatusCodes.Status409Conflict, "raw conflict")
                : null;
    }

    private static IHost Host(Action<EndpointGroup> map) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints();
                    services.AddSingleton<IEndpointExceptionTranslator>(new ConflictTranslator());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => map(endpoints.MapEndpointGroup("Raw")));
                }))
            .Start();

    public async ValueTask DisposeAsync()
    {
        await _reflective.StopAsync();
        _reflective.Dispose();
        await _generated.StopAsync();
        _generated.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// One class the scan picked up but cannot map is still an error — silent skipping would hide bugs —
/// but the error must name the offending type and the supported bases, not blame its route.
/// </summary>
/// <remarks>
/// The unmappable fixture is compiled at run time rather than declared in this assembly: the
/// conformance suites map this whole assembly reflectively, so a resident unmappable class would
/// blow up every one of them — which is exactly the failure mode this error message exists for.
/// </remarks>
public class UnmappableEndpointScanTests
{
    [Fact]
    public void The_scan_error_names_the_offending_type_and_the_five_supported_bases()
    {
        var assembly = CompileDirectBaseSubclass();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNativeEndpoints();
        var app = builder.Build();
        var group = ((IEndpointRouteBuilder)app).MapEndpointGroup("Rogue");

        var failure = Assert.Throws<InvalidOperationException>(() => group.MapEndpointsFrom(assembly));

        Assert.Contains("Rogue.DirectBaseEndpoint", failure.Message);
        Assert.Contains("ApiEndpoint<TRequest, TResponse>", failure.Message);
        Assert.Contains("ApiEndpoint<TRequest>", failure.Message);
        Assert.Contains("ApiEndpointWithoutRequest<TResponse>", failure.Message);
        Assert.Contains("ApiEndpointWithResult<TRequest, TResponse>", failure.Message);
        Assert.Contains("non-generic ApiEndpoint", failure.Message);
    }

    private static Assembly CompileDirectBaseSubclass()
    {
        // Carries a route on purpose: the error under test must fire because of the shape, and must
        // win over the missing-route complaint a route-less class would also earn.
        const string source = """
            namespace Rogue;

            [NativeEndpoints.Get("rogue")]
            public sealed class DirectBaseEndpoint : NativeEndpoints.ApiEndpointBase
            {
            }
            """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(ApiEndpointBase).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "NativeEndpoints.Tests.Rogue",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }
}

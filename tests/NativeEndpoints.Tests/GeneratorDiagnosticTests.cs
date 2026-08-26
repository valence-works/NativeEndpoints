using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NativeEndpoints.Generator;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The generator driven directly over source, for what cannot be proven by compiling this project:
/// a truly unmappable endpoint class cannot live in this assembly (the reflective-scan suites map
/// the whole assembly and would throw at startup), so NE0005 is asserted through a GeneratorDriver.
/// </summary>
public class GeneratorDiagnosticTests
{
    [Fact]
    public void A_direct_base_subclass_reports_NE0005_and_is_excluded_from_the_registration()
    {
        var (diagnostics, generated, compileErrors) = Run("""
            namespace Rogue;

            [NativeEndpoints.Get("rogue")]
            public sealed class DirectBaseEndpoint : NativeEndpoints.ApiEndpointBase
            {
            }

            [NativeEndpoints.Get("stream")]
            public sealed class StreamEndpoint : NativeEndpoints.ApiEndpoint
            {
                public override System.Threading.Tasks.Task HandleAsync(System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(diagnostics, item => item.Id == "NE0005");
        Assert.Contains("Rogue.DirectBaseEndpoint", diagnostic.GetMessage());
        Assert.Contains("ApiEndpoint", diagnostic.GetMessage());

        // The raw endpoint is mapped first-class; the unmappable one appears nowhere in the output.
        Assert.Contains("MapRaw", generated);
        Assert.Contains("StreamEndpoint", generated);
        Assert.DoesNotContain("DirectBaseEndpoint", generated);

        // And the emitted registration actually compiles.
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void A_raw_endpoint_reports_no_diagnostics()
    {
        var (diagnostics, _, compileErrors) = Run("""
            namespace Clean;

            [NativeEndpoints.Get("stream")]
            public sealed class StreamEndpoint : NativeEndpoints.ApiEndpoint
            {
                public override System.Threading.Tasks.Task HandleAsync(System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void A_response_only_endpoint_is_mapped_through_the_generated_unbound_path()
    {
        var (diagnostics, generated, compileErrors) = Run("""
            namespace Clean;

            public sealed record Status(string State);

            [NativeEndpoints.Get("status")]
            public sealed class StatusEndpoint : NativeEndpoints.ApiEndpointWithoutRequest<Status>
            {
                public override System.Threading.Tasks.Task<Status> HandleAsync(System.Threading.CancellationToken cancellationToken) =>
                    System.Threading.Tasks.Task.FromResult(new Status("ok"));
            }
            """);

        Assert.Empty(diagnostics);

        // Mapped first-class through the no-contract generated path, and the output compiles.
        Assert.Contains("MapGeneratedUnbound<global::Clean.StatusEndpoint, global::Clean.Status>", generated);
        Assert.Empty(compileErrors);
    }

    [Fact]
    public void A_file_member_on_a_bodyless_method_reports_NE0006()
    {
        var (diagnostics, _, _) = Run("""
            namespace Forms;

            public sealed record Request(Microsoft.AspNetCore.Http.IFormFile Upload);

            [NativeEndpoints.Get("things")]
            public sealed class Endpoint : NativeEndpoints.ApiEndpoint<Request>
            {
                public override System.Threading.Tasks.Task HandleAsync(Request request, System.Threading.CancellationToken token) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(diagnostics, item => item.Id == "NE0006");
        Assert.Contains("A form is a request body", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("GET", diagnostic.GetMessage(), StringComparison.Ordinal);

        // A file binds; it simply has nowhere to bind from here. Reporting NE0002 as well would send
        // the reader looking for an IParsable<IFormFile>.
        Assert.DoesNotContain(diagnostics, item => item.Id == "NE0002");
    }

    [Fact]
    public void A_FromForm_member_on_a_bodyless_method_reports_NE0006()
    {
        var (diagnostics, _, _) = Run("""
            namespace Forms;

            public sealed record Request([property: NativeEndpoints.FromForm] string Name);

            [NativeEndpoints.Get("things")]
            public sealed class Endpoint : NativeEndpoints.ApiEndpoint<Request>
            {
                public override System.Threading.Tasks.Task HandleAsync(Request request, System.Threading.CancellationToken token) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(diagnostics, item => item.Id == "NE0006");
        Assert.Contains("binds member 'Name' from a form", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_file_shape_reports_NE0006_on_a_bodyless_method()
    {
        var (diagnostics, _, _) = Run("""
            namespace Forms;

            public sealed record Request(
                Microsoft.AspNetCore.Http.IFormFile One,
                Microsoft.AspNetCore.Http.IFormFile[] Many,
                System.Collections.Generic.List<Microsoft.AspNetCore.Http.IFormFile> Listed,
                Microsoft.AspNetCore.Http.IFormFileCollection All);

            [NativeEndpoints.Get("things")]
            public sealed class Endpoint : NativeEndpoints.ApiEndpoint<Request>
            {
                public override System.Threading.Tasks.Task HandleAsync(Request request, System.Threading.CancellationToken token) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """);

        Assert.Equal(4, diagnostics.Count(item => item.Id == "NE0006"));
    }

    [Fact]
    public void A_form_endpoint_reports_nothing_and_its_registration_compiles()
    {
        // The rule is about a member that can never receive a value, not about form binding as such.
        // A POST carries a body, and whether that body is a form is decided in Configure, which the
        // generator reads only shallowly — so guessing there would be noise.
        var (diagnostics, generated, compileErrors) = Run("""
            namespace Forms;

            public sealed record Request(
                System.Guid Id,
                string Name,
                Microsoft.AspNetCore.Http.IFormFile? Upload,
                Microsoft.AspNetCore.Http.IFormFile[] Attachments);

            [NativeEndpoints.Post("things/{id}")]
            public sealed class Endpoint : NativeEndpoints.ApiEndpoint<Request>
            {
                public override void Configure(NativeEndpoints.ApiEndpointOptions options)
                {
                    options.BodyKind = NativeEndpoints.EndpointBodyKind.Form;
                    options.RequireAntiforgery = false;
                }

                public override System.Threading.Tasks.Task HandleAsync(Request request, System.Threading.CancellationToken token) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Empty(compileErrors);

        // Emitted, not fallen back to the reflective mapper — which is what keeps the AOT claim true.
        Assert.Contains("EndpointValue.File(context, \"Upload\")", generated, StringComparison.Ordinal);
        Assert.Contains("EndpointValue.Files(context, \"Attachments\")", generated, StringComparison.Ordinal);
    }

    private static (ImmutableArray<Diagnostic> Diagnostics, string Generated, Diagnostic[] CompileErrors) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(ApiEndpointBase).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "GeneratorDiagnosticTests.Fixture",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new EndpointRegistrationGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        var result = driver.GetRunResult();

        var generated = string.Join(
            Environment.NewLine,
            result.GeneratedTrees.Select(tree => tree.GetText().ToString()));
        var compileErrors = updated.GetDiagnostics()
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .ToArray();

        return (diagnostics, generated, compileErrors);
    }
}

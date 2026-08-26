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

using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeEndpoints.Generator;

/// <summary>
/// Emits an explicit registration for every endpoint class in the compilation.
/// </summary>
/// <remarks>
/// Replaces the reflective scan in the default path. The generated method names every endpoint, so
/// there is no <c>assembly.GetTypes()</c>, no <c>MakeGenericMethod</c>, and nothing for the trimmer
/// to be unable to see. The reflective path remains for anyone who cannot run the generator.
/// </remarks>
[Generator]
public sealed class EndpointRegistrationGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var endpoints = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, _) => Describe(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        // Types the assembly declares a runtime value binder for. The generator cannot see the
        // registration call itself, so the attribute is how intent reaches the build.
        var declared = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.Assembly.GetAttributes()
                .Where(attribute => attribute.AttributeClass?.ToDisplayString() == "NativeEndpoints.EndpointValueBinderAttribute")
                .Select(attribute => attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as INamedTypeSymbol : null)
                .Where(symbol => symbol is not null)
                .Select(symbol => symbol!.ToDisplayString())
                .ToImmutableHashSet());

        var collected = endpoints.Collect().Combine(context.CompilationProvider).Combine(declared);

        context.RegisterSourceOutput(collected, static (production, source) =>
        {
            var ((models, compilation), boundTypes) = source;
            foreach (var model in models)
                Report(production, model, boundTypes);

            var mappable = models
                .Where(model => model.HasRoute && model.Shape is not EndpointShape.Unsupported)
                .OrderBy(model => model.QualifiedName, System.StringComparer.Ordinal)
                .ToImmutableArray();

            if (mappable.Length == 0)
                return;

            production.AddSource(
                "NativeEndpointsRegistration.g.cs",
                SourceText(compilation.AssemblyName ?? "Generated", mappable));
        });
    }

    private static EndpointModel? Describe(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol type)
            return null;
        if (type.IsAbstract || !EndpointSymbols.DerivesFromEndpointBase(type))
            return null;

        var (shape, request, response) = EndpointSymbols.Shape(type);
        var contract = ContractOf(type);

        return new EndpointModel(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.ToDisplayString(),
            request,
            response,
            shape,
            EndpointSymbols.HasRouteAttribute(type),
            EndpointSymbols.HttpMethod(type),
            contract.Parameters,
            contract.ConstructorCount,
            contract.Name);
    }

    /// <summary>The request contract's single-constructor shape, as far as the compiler can see it.</summary>
    private static (ImmutableArray<ContractParameter> Parameters, int ConstructorCount, string? Name) ContractOf(INamedTypeSymbol endpoint)
    {
        for (var current = endpoint.BaseType; current is not null; current = current.BaseType)
        {
            if (current.TypeArguments.Length == 0)
                continue;

            var definition = current.ConstructedFrom.ToDisplayString();
            if (definition is not ("NativeEndpoints.ApiEndpoint<TRequest, TResponse>"
                or "NativeEndpoints.ApiEndpoint<TRequest>"
                or "NativeEndpoints.ApiEndpointWithResult<TRequest, TResponse>"))
            {
                continue;
            }

            if (current.TypeArguments[0] is not INamedTypeSymbol contract)
                break;

            var constructors = contract.InstanceConstructors
                .Where(item => item.DeclaredAccessibility == Accessibility.Public)
                .ToArray();

            var parameters = constructors.Length == 1
                ? constructors[0].Parameters
                    .Select(parameter => new ContractParameter(
                        parameter.Name,
                        parameter.Type.ToDisplayString(),
                        EndpointSymbols.IsBindable(parameter.Type)))
                    .ToImmutableArray()
                : ImmutableArray<ContractParameter>.Empty;

            return (parameters, constructors.Length, contract.ToDisplayString());
        }

        return (ImmutableArray<ContractParameter>.Empty, 1, null);
    }

    private static void Report(
        SourceProductionContext production,
        EndpointModel model,
        ImmutableHashSet<string> boundTypes)
    {
        if (!model.HasRoute && model.Shape is not EndpointShape.Unsupported)
            production.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingRoute, Location.None, model.DisplayName));

        if (model.ContractName is not null && model.PublicConstructorCount != 1)
        {
            production.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.AmbiguousConstructor, Location.None, model.ContractName, model.PublicConstructorCount));
        }

        // Only for methods that read nothing from a body. Everywhere else a contract member may come
        // from JSON, where any serialisable type is fine, and Configure can change the body mode in
        // ways the generator cannot see. Reporting there would be noise.
        if (model.HttpMethod is not ("GET" or "HEAD") || model.ContractName is null)
            return;

        foreach (var parameter in model.Contract.Where(item => !item.Bindable && !boundTypes.Contains(item.TypeName)))
        {
            production.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.UnsupportedParameterType, Location.None,
                model.ContractName, parameter.Name, parameter.TypeName));
        }
    }

    private static Microsoft.CodeAnalysis.Text.SourceText SourceText(string assemblyName, ImmutableArray<EndpointModel> endpoints)
    {
        var identifier = new string(assemblyName.Where(char.IsLetterOrDigit).ToArray());
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace NativeEndpoints.Generated;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>Explicit registrations for every endpoint class in this assembly.</summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine("/// Generated. Calling this instead of MapEndpointsFrom removes the assembly scan, so nothing");
        builder.AppendLine("/// here depends on reflection over types the trimmer cannot see.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public static class {identifier}Endpoints");
        builder.AppendLine("{");
        builder.AppendLine($"    /// <summary>Maps all {endpoints.Length} endpoint class(es) declared in this assembly.</summary>");
        builder.AppendLine("    public static global::NativeEndpoints.EndpointGroup Map(");
        builder.AppendLine("        this global::NativeEndpoints.EndpointGroup group,");
        builder.AppendLine("        string? routePrefix = null)");
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(group);");
        builder.AppendLine();

        foreach (var endpoint in endpoints)
            builder.AppendLine($"        group.MapEndpoint<{endpoint.QualifiedName}>(routePrefix);");

        builder.AppendLine();
        builder.AppendLine("        return group;");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return Microsoft.CodeAnalysis.Text.SourceText.From(builder.ToString(), Encoding.UTF8);
    }
}

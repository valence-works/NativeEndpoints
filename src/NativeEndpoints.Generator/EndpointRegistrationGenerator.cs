using System.Collections.Generic;
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
        // registration call itself, so the attribute is how intent reaches the build. Sorted and
        // deduplicated into an EquatableArray so an unchanged declaration set compares equal and
        // keeps the cached outputs alive.
        var declared = context.CompilationProvider.Select(static (compilation, _) =>
            (EquatableArray<string>)compilation.Assembly.GetAttributes()
                .Where(attribute => attribute.AttributeClass?.ToDisplayString() == "NativeEndpoints.EndpointValueBinderAttribute")
                .Select(attribute => attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as INamedTypeSymbol : null)
                .Where(symbol => symbol is not null)
                .Select(symbol => symbol!.ToDisplayString())
                .Distinct()
                .OrderBy(name => name, System.StringComparer.Ordinal)
                .ToImmutableArray());

        // Diagnostics run per endpoint, so an edit inside one class re-checks that class alone.
        context.RegisterSourceOutput(endpoints.Combine(declared), static (production, source) =>
            Report(production, source.Left, source.Right));

        // Emission needs only the assembly name from the compilation. Combining the whole
        // CompilationProvider would re-run this output on every keystroke in the consuming project;
        // the name is a value-equatable string that almost never changes.
        var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName);

        // Emission never reads Configure's diagnostic positions, but they participate in the
        // model's equality, so a line shift inside a Configure body would re-run emission for a
        // byte-identical file. Strip them from the emission input; the diagnostics node above
        // keeps the full model, so the reported locations stay exact.
        var collected = endpoints
            .Select(static (model, _) => model with { ConfigureReads = default })
            .Collect()
            .Combine(assemblyName);

        context.RegisterSourceOutput(collected, static (production, source) =>
        {
            var (models, assemblyName) = source;

            var mappable = models
                .Where(model => model.HasRoute && model.Shape is not EndpointShape.Unsupported)
                .OrderBy(model => model.QualifiedName, System.StringComparer.Ordinal)
                .ToImmutableArray();

            if (mappable.Length == 0)
                return;

            production.AddSource(
                "NativeEndpointsRegistration.g.cs",
                SourceText(assemblyName ?? "Generated", mappable));
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
        var pattern = EndpointSymbols.RoutePattern(type);
        var routeKeys = RouteKeys(pattern);

        // A request contract whose single public constructor takes no parameters is bound by
        // property assignment: the reflective BindProperties keeps the deserialized body and lays
        // route, query, and declared sources over it. The emitter's `new TRequest()` cannot do
        // that — it would silently discard every deserialized body value — so these fall back to
        // the reflective mapper. Only a real request contract triggers this: the no-request shapes
        // (ResponseOnly, Raw) have no contract at all (ContractName is null) and stay generatable.
        var propertyBound = contract.Name is not null && contract.ConstructorCount == 1 &&
                            contract.Parameters.Length == 0;

        // A constructor-parameter default (`int Page = 3`) is a compile-time constant the emitter
        // would have to re-literalize correctly for every supported type (enums, decimals, nested
        // nullables, ...); getting one wrong would misbind silently. The reflective binder reads
        // ParameterInfo.DefaultValue at bind time and is correct today, so any defaulted parameter
        // sends the whole contract down the reflective path instead.
        var hasDefaultedParameter = contract.Parameters.Any(parameter => parameter.HasDefaultValue);

        // Generatable only when everything is statically knowable: one constructor on both the
        // endpoint and its contract, and a conversion for every member the binder must produce from
        // a string. Anything else falls back to the reflective path, which handles it correctly.
        var generatable =
            shape is not EndpointShape.Unsupported &&
            pattern is not null &&
            contract.ConstructorCount == 1 &&
            !propertyBound &&
            !hasDefaultedParameter &&
            EndpointSymbols.HasSingleConstructor(type) &&
            contract.Parameters.All(parameter => IsEmittable(parameter, routeKeys));

        return new EndpointModel(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.ToDisplayString(),
            request,
            response,
            shape,
            EndpointSymbols.HasRouteAttribute(type),
            EndpointSymbols.HttpMethod(type),
            pattern,
            contract.Parameters,
            EndpointSymbols.Dependencies(type),
            routeKeys,
            contract.ConstructorCount,
            contract.Name,
            EndpointSymbols.Operation(type),
            generatable,
            ConfigureReads(context, type));
    }

    /// <summary>
    /// Instance state read inside a Configure override.
    /// </summary>
    /// <remarks>
    /// Configure runs at map time on an uninitialized instance, before any constructor. Reading a
    /// constructor-injected dependency there observes null, and the resulting NullReferenceException
    /// surfaces during startup with no obvious cause. Only a doc comment guarded this before.
    /// </remarks>
    private static ImmutableArray<ConfigureRead> ConfigureReads(GeneratorSyntaxContext context, INamedTypeSymbol type)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        var configure = declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText == "Configure");

        if (configure is null)
            return ImmutableArray<ConfigureRead>.Empty;

        var reads = ImmutableArray.CreateBuilder<ConfigureRead>();
        var seen = new HashSet<string>();

        foreach (var identifier in configure.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = context.SemanticModel.GetSymbolInfo(identifier).Symbol;
            var reads_state = symbol switch
            {
                // A primary constructor parameter captured into the class.
                IParameterSymbol parameter =>
                    parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor &&
                    SymbolEqualityComparer.Default.Equals(constructor.ContainingType, type),

                IFieldSymbol { IsStatic: false, IsConst: false } field =>
                    SymbolEqualityComparer.Default.Equals(field.ContainingType, type),

                // HttpContext is set per request, so reading it here is the same mistake.
                IPropertySymbol { IsStatic: false } property =>
                    SymbolEqualityComparer.Default.Equals(property.ContainingType, type) ||
                    property.Name == "HttpContext",

                _ => false
            };

            if (reads_state && seen.Add(symbol!.Name))
            {
                // Captured as raw data rather than the Location itself, which holds its syntax tree
                // and would keep the model from ever comparing equal across edits.
                var location = identifier.GetLocation();
                reads.Add(new ConfigureRead(
                    symbol.Name,
                    location.SourceTree?.FilePath ?? string.Empty,
                    location.SourceSpan,
                    location.GetLineSpan().Span));
            }
        }

        return reads.ToImmutable();
    }

    /// <summary>Whether the emitter can write a conversion for this parameter.</summary>
    private static bool IsEmittable(ContractParameter parameter, ImmutableArray<string> routeKeys)
    {
        // A file needs no conversion at all. This arm is load-bearing rather than an optimisation:
        // without it a file-bearing endpoint falls back to the reflective mapper, which is annotated
        // RequiresUnreferencedCode, and samples/Aot fails to publish rather than degrading.
        if (parameter.FormFile is not FormFileKind.None)
            return true;

        // Collections need an element conversion; scalars need their own.
        if (parameter.IsArray || parameter.IsList)
            return !string.IsNullOrEmpty(parameter.ElementConverter);

        if (!string.IsNullOrEmpty(parameter.Converter))
            return true;

        // A member with no conversion is fine when it comes from the body, where JSON handles it.
        return parameter.DeclaredSource is null && !routeKeys.Contains(parameter.Name);
    }

    private static ImmutableArray<string> RouteKeys(string? pattern)
    {
        if (pattern is null)
            return ImmutableArray<string>.Empty;

        var keys = ImmutableArray.CreateBuilder<string>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(pattern, @"\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)[^}]*\}"))
        {
            keys.Add(match.Groups[1].Value);
        }

        return keys.ToImmutable();
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
                ? constructors[0].Parameters.Select(parameter => Describe(parameter, contract)).ToImmutableArray()
                : ImmutableArray<ContractParameter>.Empty;

            return (parameters, constructors.Length, contract.ToDisplayString());
        }

        return (ImmutableArray<ContractParameter>.Empty, 1, null);
    }

    /// <summary>Reduces one contract parameter to the calls the emitter will write for it.</summary>
    private static ContractParameter Describe(IParameterSymbol parameter, INamedTypeSymbol contract)
    {
        // The attribute may sit on the parameter or, for a positional record, on the generated
        // property. Both spellings are idiomatic, so both are read.
        var attribute = parameter.GetAttributes()
                            .FirstOrDefault(item => IsBindFrom(item.AttributeClass))
                        ?? contract.GetMembers(parameter.Name)
                            .OfType<IPropertySymbol>()
                            .SelectMany(property => property.GetAttributes())
                            .FirstOrDefault(item => IsBindFrom(item.AttributeClass));

        var source = attribute?.AttributeClass?.ToDisplayString() switch
        {
            "NativeEndpoints.FromRouteAttribute" => "Route",
            "NativeEndpoints.FromQueryAttribute" => "Query",
            "NativeEndpoints.FromHeaderAttribute" => "Header",
            "NativeEndpoints.FromClaimAttribute" => "Claim",
            "NativeEndpoints.FromFormAttribute" => "Form",
            _ => null
        };

        var key = attribute?.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as string
            : null;

        var (element, isArray, isList) = EndpointSymbols.Collection(parameter.Type);
        var elementConverter = element is null ? null : EndpointSymbols.Converter(element);

        return new ContractParameter(
            parameter.Name,
            parameter.Type.ToDisplayString(),
            EndpointSymbols.IsBindable(parameter.Type),
            EndpointSymbols.Converter(parameter.Type) ?? string.Empty,
            elementConverter,
            element?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            isArray,
            isList,
            source,
            key,
            parameter.Type is { IsReferenceType: true, NullableAnnotation: not NullableAnnotation.Annotated },
            parameter.HasExplicitDefaultValue,
            EndpointSymbols.FormFile(parameter.Type));
    }

    private static bool IsBindFrom(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "NativeEndpoints.BindFromAttribute")
                return true;
        }

        return false;
    }

    private static void Report(
        SourceProductionContext production,
        EndpointModel model,
        EquatableArray<string> boundTypes)
    {
        if (model.Shape is EndpointShape.Unsupported)
            production.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnmappableBase, Location.None, model.DisplayName));

        if (!model.HasRoute && model.Shape is not EndpointShape.Unsupported)
            production.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingRoute, Location.None, model.DisplayName));

        foreach (var read in model.ConfigureReads)
        {
            production.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ConfigureTouchesState, read.Location, model.DisplayName, read.Member));
        }

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

        // A form field or a file on a bodyless method. Unlike NE0002 this needs no bindability
        // check: the member binds perfectly well, there is simply never anything to bind it from.
        foreach (var parameter in model.Contract.Where(IsFormBound))
        {
            production.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.FormMemberWithoutBody, Location.None,
                model.ContractName, parameter.Name, model.DisplayName, model.HttpMethod));
        }
    }

    /// <summary>Whether a member can only ever come from a form.</summary>
    private static bool IsFormBound(ContractParameter parameter) =>
        parameter.FormFile is not FormFileKind.None || parameter.DeclaredSource == "Form";

    private static Microsoft.CodeAnalysis.Text.SourceText SourceText(string assemblyName, ImmutableArray<EndpointModel> endpoints)
    {
        var identifier = new string(assemblyName.Where(char.IsLetterOrDigit).ToArray());
        var generated = endpoints.Where(model => model.Generatable).ToImmutableArray();
        var reflective = endpoints.Where(model => !model.Generatable).ToImmutableArray();

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("#pragma warning disable CS1591");
        builder.AppendLine();
        builder.AppendLine("namespace NativeEndpoints.Generated;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>Registrations for every endpoint class in this assembly.</summary>");
        builder.AppendLine("/// <remarks>");
        builder.AppendLine($"/// Generated. {generated.Length} endpoint(s) bind and activate through emitted code with no");
        builder.AppendLine($"/// reflection; {reflective.Length} fall back to the reflective mapper because their shape is not");
        builder.AppendLine("/// statically resolvable. Both produce identical endpoints.");
        builder.AppendLine("/// </remarks>");
        builder.AppendLine($"public static class {identifier}Endpoints");
        builder.AppendLine("{");

        for (var index = 0; index < generated.Length; index++)
            Emitter.Endpoint(builder, generated[index], index);

        builder.AppendLine($"    /// <summary>Maps all {endpoints.Length} endpoint class(es) declared in this assembly.</summary>");
        builder.AppendLine("    public static global::NativeEndpoints.EndpointGroup Map(");
        builder.AppendLine("        this global::NativeEndpoints.EndpointGroup group,");
        builder.AppendLine("        string? routePrefix = null)");
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(group);");
        builder.AppendLine();

        for (var index = 0; index < generated.Length; index++)
            Emitter.Map(builder, generated[index], index);

        foreach (var endpoint in reflective)
        {
            builder.AppendLine($"        // {endpoint.DisplayName}: mapped reflectively; its shape is not statically resolvable.");
            builder.AppendLine($"        group.MapEndpoint<{endpoint.QualifiedName}>(routePrefix);");
            builder.AppendLine();
        }

        builder.AppendLine("        return group;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine(Helpers);
        builder.AppendLine("}");
        return Microsoft.CodeAnalysis.Text.SourceText.From(builder.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Shared by every generated mapping: run Configure, then apply what it and the attributes asked
    /// for. Configure is arbitrary code, so it has to run rather than be read at compile time.
    /// </summary>
    private const string Helpers = """
        private static global::NativeEndpoints.ApiEndpointOptions Describe<
            [global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
                global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
                global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicConstructors)] TEndpoint>(
            string method, string route, string operation, string? routePrefix)
            where TEndpoint : global::NativeEndpoints.ApiEndpointBase
        {
            var options = new global::NativeEndpoints.ApiEndpointOptions
            {
                Method = method,
                Route = string.IsNullOrEmpty(routePrefix) ? route : routePrefix!.TrimEnd('/') + "/" + route.TrimStart('/'),
                Operation = operation
            };

            // Configure runs at map time on an uninitialized instance, exactly as the reflective
            // mapper runs it. It is arbitrary code, so it cannot be evaluated at compile time.
            var describer = (global::NativeEndpoints.ApiEndpointBase)global::System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(TEndpoint));
            describer.Configure(options);
            return options;
        }

        private static void Apply<TEndpoint>(
            global::Microsoft.AspNetCore.Builder.IEndpointConventionBuilder builder,
            global::NativeEndpoints.ApiEndpointOptions options)
        {
            foreach (var attribute in typeof(TEndpoint).GetCustomAttributes(false))
            {
                if (attribute is global::NativeEndpoints.IEndpointConventionAttribute convention)
                    convention.Apply(builder);
            }

            foreach (var convention in options.Conventions)
                convention(builder);
        }
        """;
}

using Microsoft.CodeAnalysis;

namespace NativeEndpoints.Generator;

/// <summary>
/// What the generator can tell you at build time that reflection could only tell you at request time.
/// </summary>
internal static class Diagnostics
{
    private const string Category = "NativeEndpoints";

    internal static readonly DiagnosticDescriptor MissingRoute = new(
        "NE0001",
        "Endpoint declares no route",
        "Endpoint '{0}' declares no route. Add a [Get]/[Post]/[Put]/[Patch]/[Delete] attribute, or set options.Method and options.Route in Configure.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An endpoint class with no route attribute is only mappable if Configure supplies one, which the generator cannot see.");

    internal static readonly DiagnosticDescriptor UnsupportedParameterType = new(
        "NE0002",
        "Contract parameter type cannot be bound",
        "Contract '{0}' has parameter '{1}' of unsupported type '{2}'. Implement IParsable<{2}>, or register a parser with AddNativeEndpoints(o => o.ValueBinders.Add<{2}>(...)).",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reported at build time rather than on the first request that reaches the route.");

    internal static readonly DiagnosticDescriptor ConfigureTouchesState = new(
        "NE0003",
        "Configure reads constructor-injected state",
        "'{0}.Configure' reads '{1}'. Configure runs at map time on an uninitialized instance, so constructor-injected state is null there.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Configure is invoked once at mapping time before any constructor runs. Reading instance state observes null rather than the injected dependency.");

    internal static readonly DiagnosticDescriptor AmbiguousConstructor = new(
        "NE0004",
        "Contract has more than one public constructor",
        "Contract '{0}' declares {1} public constructors. The binder requires exactly one, and will throw when this endpoint is first called.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnmappableBase = new(
        "NE0005",
        "Endpoint derives from ApiEndpointBase directly",
        "Endpoint '{0}' derives ApiEndpointBase directly and cannot be mapped. Derive the non-generic ApiEndpoint to write the response yourself, or one of the four contract shapes.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every mapper dispatches through a handler method a base type declares, and ApiEndpointBase declares none. The generated registration excludes such a class, and the reflective scan throws for it at startup.");
}

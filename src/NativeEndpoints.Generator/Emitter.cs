using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace NativeEndpoints.Generator;

/// <summary>Writes the binder, the activator, and the mapping for one endpoint.</summary>
internal static class Emitter
{
    internal static void Endpoint(StringBuilder builder, EndpointModel endpoint, int index)
    {
        var slot = $"Endpoint{index}";
        builder.AppendLine($"    private static class {slot}");
        builder.AppendLine("    {");

        // A raw or response-only endpoint binds nothing, so its slot is just the activator.
        if (endpoint.Shape is not (EndpointShape.Raw or EndpointShape.ResponseOnly))
        {
            Binder(builder, endpoint);
            builder.AppendLine();
        }

        Activator(builder, endpoint, slot);
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    /// <summary>A binder that reads every member by name, with no reflection anywhere.</summary>
    private static void Binder(StringBuilder builder, EndpointModel endpoint)
    {
        var contract = endpoint.Contract;

        // The per-element converters close over nothing, so each is allocated once per endpoint
        // rather than once per request. Which of the pair applies is decided by the per-call strict
        // flag, keeping the emitted conversion identical to the captured lambda it replaces.
        // File collections are excluded: their elements are not converted from a string at all, so
        // there is no element converter to hoist and emitting one writes a call with no method name.
        foreach (var parameter in contract.Where(item => (item.IsArray || item.IsList) && item.FormFile is FormFileKind.None))
        {
            var converter = parameter.ElementConverter!;
            builder.AppendLine($"        private static readonly global::System.Func<string?, {parameter.ElementTypeName}> Convert{parameter.Name}Strict =");
            builder.AppendLine($"            static raw => global::NativeEndpoints.EndpointValue.{converter}(raw, true, \"{parameter.Name}\")!;");
            builder.AppendLine($"        private static readonly global::System.Func<string?, {parameter.ElementTypeName}> Convert{parameter.Name}Lenient =");
            builder.AppendLine($"            static raw => global::NativeEndpoints.EndpointValue.{converter}(raw, false, \"{parameter.Name}\")!;");
            builder.AppendLine();
        }

        // Whether any member could fall back from the body to the query, which is the only reader
        // of the supplied-property set. Members bound from a route value or a declared source never
        // consult it, so a contract made only of those lets the body stream through the serializer
        // in one pass instead of buffering a DOM.
        var needsSupplied = contract.Any(parameter =>
            parameter.DeclaredSource is null &&
            !endpoint.RouteKeys.Any(key => string.Equals(key, parameter.Name, System.StringComparison.OrdinalIgnoreCase)));

        builder.AppendLine($"        internal static async global::System.Threading.Tasks.ValueTask<global::NativeEndpoints.EndpointBindingResult<{endpoint.RequestType}>> Bind(");
        builder.AppendLine("            global::Microsoft.AspNetCore.Http.HttpContext context,");
        builder.AppendLine("            global::System.Text.Json.JsonSerializerOptions jsonOptions,");
        builder.AppendLine("            global::NativeEndpoints.EndpointBindingOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            // Body reading is shared with the reflective binder so the media-type rules cannot drift.");
        builder.AppendLine($"            var read = await global::NativeEndpoints.EndpointRequestBinder.ReadBodyAsync<{endpoint.RequestType}>(context, jsonOptions, options.BodyMode, needsSuppliedProperties: {(needsSupplied ? "true" : "false")}, options.BodyKind);");
        builder.AppendLine("            if (!read.Succeeded)");
        builder.AppendLine("                return read.Failure;");
        builder.AppendLine();
        builder.AppendLine("            var body = read.Body;");
        builder.AppendLine("            var supplied = read.SuppliedProperties;");
        builder.AppendLine("            var valueBinders = options.ValueBinders;");
        builder.AppendLine("            var strict = options.StrictTypedParsing;");
        builder.AppendLine("            try");
        builder.AppendLine("            {");
        builder.AppendLine($"                return new(new {endpoint.RequestType}(");

        for (var index = 0; index < contract.Length; index++)
        {
            var parameter = contract[index];
            var separator = index == contract.Length - 1 ? string.Empty : ",";
            var expression = Expression(parameter, endpoint);
            if (parameter.SuppressNull && expression.Contains(" ? "))
                expression = $"({expression})!";

            builder.AppendLine($"                    {expression}{separator}");
        }

        builder.AppendLine("                ), null, null);");
        builder.AppendLine("            }");
        builder.AppendLine("            catch (global::NativeEndpoints.EndpointStrictValueException failure)");
        builder.AppendLine("            {");
        builder.AppendLine("                // Reported under the wire name the query string documents, matching the reflective binder.");
        builder.AppendLine("                return new(default, global::NativeEndpoints.EndpointBindingFailure.InvalidTypedValue,");
        builder.AppendLine("                    $\"Value [{failure.RawValue}] is not valid for a [{failure.TypeName}] property!\",");
        builder.AppendLine("                    global::System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(failure.Name));");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
    }

    /// <summary>The expression that produces one contract member.</summary>
    private static string Expression(ContractParameter parameter, EndpointModel endpoint)
    {
        // Files first, ahead of every other branch. A file is not converted from a string, and it
        // never participates in route or query inference, so none of what follows applies to it.
        if (parameter.FormFile is not FormFileKind.None)
            return FormFile(parameter);

        if (parameter.DeclaredSource is { } declared)
            return Read(parameter, declared, parameter.DeclaredKey ?? parameter.Name);

        // Case-insensitively, as routing itself matches, and using the template's own key so the
        // lookup matches what routing put in RouteValues.
        var routeKey = endpoint.RouteKeys.FirstOrDefault(key =>
            string.Equals(key, parameter.Name, System.StringComparison.OrdinalIgnoreCase));

        if (routeKey is not null)
            return Read(parameter, "Route", routeKey);

        // Route wins over the body, the body wins over the query, and a form *is* the body — so the
        // form sits in the body's place rather than adding a step. Both conditionals are emitted
        // unconditionally on purpose: `body` is null whenever no JSON body was read and
        // SuppliedByForm is false whenever the request was not a form, so this stays correct
        // whatever Configure does to the body mode or kind, where a compile-time guess would not.
        var form = Read(parameter, "Form", parameter.Name);
        var query = Read(parameter, "Query", parameter.Name);
        return $"body is not null && global::NativeEndpoints.EndpointValue.Supplied(supplied, \"{parameter.Name}\") "
               + $"? body.{parameter.Name} "
               + $": global::NativeEndpoints.EndpointValue.SuppliedByForm(context, \"{parameter.Name}\") "
               + $"? {form} : {query}";
    }

    /// <summary>The expression that produces a file-typed member.</summary>
    private static string FormFile(ContractParameter parameter)
    {
        var key = parameter.DeclaredKey ?? parameter.Name;
        switch (parameter.FormFile)
        {
            case FormFileKind.All:
                return "global::NativeEndpoints.EndpointValue.AllFiles(context)";

            case FormFileKind.Many:
                var files = $"global::NativeEndpoints.EndpointValue.Files(context, \"{key}\")";
                return parameter.IsList
                    ? $"new global::System.Collections.Generic.List<global::Microsoft.AspNetCore.Http.IFormFile>({files})"
                    : files;

            default:
                // An absent file is null, exactly as the reflective binder leaves it. Where the
                // member is a non-nullable reference the generated code says so rather than
                // pretending otherwise, so the two binders stay identical.
                var file = $"global::NativeEndpoints.EndpointValue.File(context, \"{key}\")";
                return parameter.SuppressNull ? file + "!" : file;
        }
    }

    private static string Read(ContractParameter parameter, string source, string key)
    {
        if (parameter.IsArray || parameter.IsList)
        {
            var shape = parameter.IsArray ? "Array" : "List";
            return $"global::NativeEndpoints.EndpointValue.{shape}<{parameter.ElementTypeName}>("
                   + $"global::NativeEndpoints.EndpointValue.{source}Values(context, \"{key}\"), "
                   // The converter may yield null for an absent element, exactly as the reflective
                   // binder does. Harmless on value types, and required for non-nullable references.
                   + $"strict ? Convert{parameter.Name}Strict : Convert{parameter.Name}Lenient)";
        }

        var raw = $"global::NativeEndpoints.EndpointValue.{source}(context, \"{key}\")";
        var converted = string.IsNullOrEmpty(parameter.Converter)
            ? $"global::NativeEndpoints.EndpointValue.Registered<{parameter.TypeName}>({raw}, valueBinders, strict, \"{parameter.Name}\")"
            : $"global::NativeEndpoints.EndpointValue.{parameter.Converter}({raw}, strict, \"{parameter.Name}\")";

        // The reflective binder can genuinely produce null for an absent value bound to a
        // non-nullable reference parameter. The generated equivalent says so rather than pretending
        // otherwise, so the two behave identically.
        return parameter.SuppressNull ? converted + "!" : converted;
    }

    /// <summary>An activator that news the endpoint up directly from request services.</summary>
    private static void Activator(StringBuilder builder, EndpointModel endpoint, string slot)
    {
        builder.AppendLine($"        internal static {endpoint.QualifiedName} Create(global::System.IServiceProvider services) =>");
        if (endpoint.Dependencies.IsEmpty)
        {
            builder.AppendLine($"            new {endpoint.QualifiedName}();");
            return;
        }

        var arguments = endpoint.Dependencies
            .Select(dependency => $"global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<{dependency}>(services)")
            .ToArray();

        builder.AppendLine($"            new {endpoint.QualifiedName}(");
        for (var index = 0; index < arguments.Length; index++)
        {
            var separator = index == arguments.Length - 1 ? string.Empty : ",";
            builder.AppendLine($"                {arguments[index]}{separator}");
        }

        builder.AppendLine("            );");
    }

    /// <summary>The mapping call for one endpoint, wiring its generated binder and activator in.</summary>
    internal static void Map(StringBuilder builder, EndpointModel endpoint, int index)
    {
        var slot = $"Endpoint{index}";
        builder.AppendLine($"        // {endpoint.DisplayName}");
        builder.AppendLine("        {");
        builder.AppendLine($"            var options = Describe<{endpoint.QualifiedName}>(");
        builder.AppendLine($"                \"{endpoint.HttpMethod}\", \"{endpoint.RoutePattern}\", \"{endpoint.Operation}\", routePrefix);");

        var call = endpoint.Shape switch
        {
            EndpointShape.RequestResponse =>
                $"group.MapGenerated<{endpoint.QualifiedName}, {endpoint.RequestType}, {endpoint.ResponseType}>("
                + $"options, {slot}.Bind, {slot}.Create, static (endpoint, request, token) => endpoint.HandleAsync(request, token));",
            EndpointShape.RequestOnly =>
                $"group.MapGeneratedNoContent<{endpoint.QualifiedName}, {endpoint.RequestType}>("
                + $"options, {slot}.Bind, {slot}.Create, static (endpoint, request, token) => endpoint.HandleAsync(request, token));",
            EndpointShape.ResponseOnly =>
                $"group.MapGeneratedUnbound<{endpoint.QualifiedName}, {endpoint.ResponseType}>("
                + $"options, {slot}.Create, static (endpoint, token) => endpoint.HandleAsync(token));",
            EndpointShape.RequestResult =>
                $"group.MapGeneratedResult<{endpoint.QualifiedName}, {endpoint.RequestType}, {endpoint.ResponseType}>("
                + $"options, {slot}.Bind, {slot}.Create, static (endpoint, request, token) => endpoint.HandleAsync(request, token));",
            EndpointShape.Raw =>
                $"group.MapRaw(options, static async context => {{ var endpoint = {slot}.Create(context.RequestServices); "
                + "endpoint.HttpContext = context; await endpoint.HandleAsync(context.RequestAborted); });",
            _ => string.Empty
        };

        builder.AppendLine($"            var builder = {call}");
        builder.AppendLine($"            Apply<{endpoint.QualifiedName}>(builder, options);");
        builder.AppendLine("        }");
        builder.AppendLine();
    }
}

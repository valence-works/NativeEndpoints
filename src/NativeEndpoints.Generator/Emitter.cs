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
        Binder(builder, endpoint);
        builder.AppendLine();
        Activator(builder, endpoint, slot);
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    /// <summary>A binder that reads every member by name, with no reflection anywhere.</summary>
    private static void Binder(StringBuilder builder, EndpointModel endpoint)
    {
        var contract = endpoint.Contract;
        builder.AppendLine($"        internal static async global::System.Threading.Tasks.ValueTask<global::NativeEndpoints.EndpointBindingResult<{endpoint.RequestType}>> Bind(");
        builder.AppendLine("            global::Microsoft.AspNetCore.Http.HttpContext context,");
        builder.AppendLine("            global::System.Text.Json.JsonSerializerOptions jsonOptions,");
        builder.AppendLine("            global::NativeEndpoints.EndpointBindingOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            // Body reading is shared with the reflective binder so the media-type rules cannot drift.");
        builder.AppendLine($"            var read = await global::NativeEndpoints.EndpointRequestBinder.ReadBodyAsync<{endpoint.RequestType}>(context, jsonOptions, options.BodyMode);");
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
        if (parameter.DeclaredSource is { } declared)
            return Read(parameter, declared, parameter.DeclaredKey ?? parameter.Name);

        // Case-insensitively, as routing itself matches, and using the template's own key so the
        // lookup matches what routing put in RouteValues.
        var routeKey = endpoint.RouteKeys.FirstOrDefault(key =>
            string.Equals(key, parameter.Name, System.StringComparison.OrdinalIgnoreCase));

        if (routeKey is not null)
            return Read(parameter, "Route", routeKey);

        // Route wins over the body, and the body wins over the query, exactly as the reflective
        // binder orders them. The conditional is emitted unconditionally on purpose: `body` is null
        // whenever no body was read, so this stays correct whatever Configure does to the body mode,
        // where a compile-time guess about it would not.
        var query = Read(parameter, "Query", parameter.Name);
        return $"body is not null && global::NativeEndpoints.EndpointValue.Supplied(supplied, \"{parameter.Name}\") "
               + $"? body.{parameter.Name} : {query}";
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
                   + $"raw => global::NativeEndpoints.EndpointValue.{parameter.ElementConverter}(raw, strict, \"{parameter.Name}\")!)";
        }

        var raw = $"global::NativeEndpoints.EndpointValue.{source}(context, \"{key}\")";
        var converted = string.IsNullOrEmpty(parameter.Converter)
            ? $"global::NativeEndpoints.EndpointValue.Registered<{parameter.TypeName}>({raw}, valueBinders)"
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
            EndpointShape.RequestResult =>
                $"group.MapGeneratedResult<{endpoint.QualifiedName}, {endpoint.RequestType}, {endpoint.ResponseType}>("
                + $"options, {slot}.Bind, {slot}.Create, static (endpoint, request, token) => endpoint.HandleAsync(request, token));",
            _ => string.Empty
        };

        builder.AppendLine($"            var builder = {call}");
        builder.AppendLine($"            Apply<{endpoint.QualifiedName}>(builder, options);");
        builder.AppendLine("        }");
        builder.AppendLine();
    }
}

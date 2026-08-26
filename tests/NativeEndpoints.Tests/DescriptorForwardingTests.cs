using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// Every descriptor field reaches the convention on every mapping path.
/// </summary>
/// <remarks>
/// This is the invariant <see cref="EndpointOperationDescriptor"/> was introduced to hold, and it
/// has now failed twice: <c>StrictTypedParsing</c> was dropped by the forwarding wrappers in
/// preview.2, and <c>DocumentedStatus</c> and <c>DocumentAuthResponses</c> were dropped by the
/// unbound path in preview.3. Both were invisible to the existing suites, which compared the two
/// binders against each other — and they agreed, on doing the wrong thing. So this asserts against
/// the descriptor's own shape rather than against a list of fields someone has to remember to
/// extend.
/// </remarks>
public class DescriptorForwardingTests
{
    /// <summary>
    /// Descriptor fields that deliberately do not appear on the context, with the reason. A new
    /// descriptor field that is neither forwarded nor listed here fails
    /// <see cref="Every_descriptor_field_is_either_forwarded_to_the_context_or_explicitly_exempt"/>,
    /// which is the point: the decision has to be made, not defaulted into silence.
    /// </summary>
    private static readonly Dictionary<string, string> ExemptFromContext = new()
    {
        ["BodyMode"] = "Binding input. Reaches the convention as the derived ReadsBody instead.",
        ["StrictTypedParsing"] = "Binding behaviour. Nothing about the document depends on it.",
        ["ContainFailures"] = "Runtime failure routing. Nothing about the document depends on it."
    };

    [Fact]
    public void Every_descriptor_field_is_either_forwarded_to_the_context_or_explicitly_exempt()
    {
        var contextFields = typeof(EndpointOperationContext)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = typeof(EndpointOperationDescriptor)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => !contextFields.Contains(name) && !ExemptFromContext.ContainsKey(name))
            .ToArray();

        Assert.True(unaccounted.Length == 0,
            $"EndpointOperationDescriptor gained {string.Join(", ", unaccounted)}, which the convention " +
            "never sees. Either forward it in EndpointGroup.Contextualize, or add it to ExemptFromContext " +
            "with the reason it is not part of an operation's description.");
    }

    [Theory]
    [InlineData(Path.Bound)]
    [InlineData(Path.Unbound)]
    public void The_documented_status_survives_every_path(Path path)
    {
        var context = Capture(path, options =>
        {
            options.SuccessStatus = StatusCodes.Status201Created;
            options.DocumentedStatus = StatusCodes.Status200OK;
        });

        Assert.Equal(StatusCodes.Status201Created, context.SuccessStatus);
        Assert.Equal(StatusCodes.Status200OK, context.DocumentedStatus);
    }

    [Theory]
    [InlineData(Path.Bound, true)]
    [InlineData(Path.Bound, false)]
    [InlineData(Path.Unbound, true)]
    [InlineData(Path.Unbound, false)]
    public void The_forced_auth_documentation_survives_every_path(Path path, bool forced)
    {
        var context = Capture(path, options => options.DocumentAuthResponses = forced);

        Assert.Equal(forced, context.DocumentAuthResponses);
    }

    [Theory]
    [InlineData(Path.Bound)]
    [InlineData(Path.Unbound)]
    public void The_name_override_survives_every_path(Path path)
    {
        var context = Capture(path, options => options.Name = "FrozenOperationId");

        Assert.Equal("FrozenOperationId", context.Name);
    }

    [Theory]
    [InlineData(Path.Bound)]
    [InlineData(Path.Unbound)]
    public void The_success_content_type_survives_every_path(Path path)
    {
        var context = Capture(path, options => options.SuccessContentType = "text/event-stream");

        Assert.Equal("text/event-stream", context.SuccessContentType);
    }

    /// <summary>An unset descriptor still documents the runtime status, as it always has.</summary>
    [Theory]
    [InlineData(Path.Bound)]
    [InlineData(Path.Unbound)]
    public void An_undeclared_documented_status_falls_back_to_the_runtime_status(Path path)
    {
        var context = Capture(path, options => options.SuccessStatus = StatusCodes.Status202Accepted);

        Assert.Equal(StatusCodes.Status202Accepted, context.DocumentedStatus);
    }

    public enum Path
    {
        /// <summary>MapOperation — binds a contract.</summary>
        Bound,

        /// <summary>MapRaw — the dispatch owns the response. Where the preview.3 drop lived.</summary>
        Unbound
    }

    private static EndpointOperationContext Capture(Path path, Action<ApiEndpointOptions> configure)
    {
        EndpointOperationContext? captured = null;

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNativeEndpoints(options =>
            options.OperationConvention = (_, context) => captured = context);

        var app = builder.Build();
        var group = ((IEndpointRouteBuilder)app).MapEndpointGroup("Forwarding");

        var options = new ApiEndpointOptions
        {
            Method = "GET",
            Route = "probe",
            Operation = "Probe"
        };
        configure(options);

        switch (path)
        {
            case Path.Bound:
                group.MapOperation<string>(
                    new EndpointOperationDescriptor
                    {
                        Method = options.Method!,
                        Pattern = options.Route!,
                        Operation = options.Operation!,
                        Name = options.Name,
                        SuccessContentType = options.SuccessContentType,
                        SuccessStatus = options.SuccessStatus,
                        DocumentedStatus = options.DocumentedStatus,
                        DocumentAuthResponses = options.DocumentAuthResponses
                    },
                    (_, _, _) => Task.CompletedTask);
                break;
            case Path.Unbound:
                group.MapRaw(options, _ => Task.CompletedTask);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }

        return captured ?? throw new InvalidOperationException("The convention was never invoked.");
    }
}

/// <summary>
/// The capabilities a host needs to reproduce a published document it did not originally generate:
/// a frozen operation identifier, a non-JSON success content type, and a tag that is not the group
/// name. Each is inert unless asked for, so an existing host's document does not move.
/// </summary>
public class PublishedDocumentFidelityTests
{
    [Fact]
    public void A_declared_name_replaces_the_derived_one()
    {
        var endpoint = Map(group => group.MapRaw(
            Options(options => options.Name = "AspNetCoreIdentityLoginPage"),
            _ => Task.CompletedTask));

        Assert.Equal("AspNetCoreIdentityLoginPage",
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
    }

    [Fact]
    public void An_undeclared_name_still_derives_from_the_group_and_operation()
    {
        var endpoint = Map(group => group.MapRaw(Options(), _ => Task.CompletedTask));

        Assert.Equal("Fidelity_Probe", endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
    }

    [Fact]
    public void A_declared_success_content_type_is_what_the_response_documents()
    {
        var endpoint = Map(group => group.MapOperation<string>(
            new EndpointOperationDescriptor
            {
                Method = "GET",
                Pattern = "stream",
                Operation = "Stream",
                ResponseType = typeof(string),
                SuccessContentType = "text/event-stream"
            },
            (_, _, _) => Task.CompletedTask));

        var produces = Assert.Single(endpoint.Metadata.OfType<IProducesResponseTypeMetadata>());
        Assert.Contains("text/event-stream", produces.ContentTypes);
        Assert.DoesNotContain("application/json", produces.ContentTypes);
    }

    [Fact]
    public void An_undeclared_success_content_type_still_documents_json()
    {
        var endpoint = Map(group => group.MapHandler<string>(
            "GET", "things", "ThingsList", (_, _) => Task.FromResult("ok")));

        var produces = Assert.Single(endpoint.Metadata.OfType<IProducesResponseTypeMetadata>());
        Assert.Contains("application/json", produces.ContentTypes);
    }

    /// <summary>
    /// Owning the response is not the same as having nothing to say about it. A raw endpoint that
    /// writes its own body can still document what that body is.
    /// </summary>
    [Fact]
    public void A_response_owning_endpoint_can_document_the_body_it_writes()
    {
        var endpoint = Map(group => group.MapRaw(
            Options(options =>
            {
                options.ResponseType = typeof(string);
                options.SuccessStatus = StatusCodes.Status201Created;
                options.DocumentedStatus = StatusCodes.Status200OK;
            }),
            _ => Task.CompletedTask));

        var produces = Assert.Single(endpoint.Metadata.OfType<IProducesResponseTypeMetadata>());
        Assert.Equal(typeof(string), produces.Type);
        Assert.Equal(StatusCodes.Status200OK, produces.StatusCode);
    }

    [Fact]
    public void A_response_owning_endpoint_that_declares_nothing_documents_no_body()
    {
        var endpoint = Map(group => group.MapRaw(Options(), _ => Task.CompletedTask));

        var produces = Assert.Single(endpoint.Metadata.OfType<IProducesResponseTypeMetadata>());
        Assert.Equal(typeof(void), produces.Type);
        Assert.Empty(produces.ContentTypes);
    }

    [Fact]
    public void A_group_tag_is_published_instead_of_the_group_name()
    {
        var endpoint = Map(
            group => group.MapHandler<string>("GET", "things", "ThingsList", (_, _) => Task.FromResult("ok")),
            tag: "Identity");

        Assert.Contains("Identity", endpoint.Metadata.GetMetadata<ITagsMetadata>()!.Tags);
    }

    /// <summary>
    /// The tag is presentation; the name and the group marker are identity. A shared tag must not
    /// collapse two groups into one for naming or for a lifetime violation report.
    /// </summary>
    [Fact]
    public void A_group_tag_does_not_change_endpoint_names_or_group_membership()
    {
        var endpoint = Map(
            group => group.MapHandler<string>("GET", "things", "ThingsList", (_, _) => Task.FromResult("ok")),
            tag: "Identity");

        Assert.Equal("Fidelity_ThingsList", endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Equal("Fidelity", endpoint.Metadata.GetMetadata<EndpointGroupMetadata>()?.Name);
    }

    [Fact]
    public void An_undeclared_tag_still_falls_back_to_the_group_name()
    {
        var endpoint = Map(group => group.MapHandler<string>(
            "GET", "things", "ThingsList", (_, _) => Task.FromResult("ok")));

        Assert.Contains("Fidelity", endpoint.Metadata.GetMetadata<ITagsMetadata>()!.Tags);
    }

    private static ApiEndpointOptions Options(Action<ApiEndpointOptions>? configure = null)
    {
        var options = new ApiEndpointOptions { Method = "GET", Route = "probe", Operation = "Probe" };
        configure?.Invoke(options);
        return options;
    }

    private static Endpoint Map(Action<EndpointGroup> map, string? tag = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNativeEndpoints();

        var app = builder.Build();
        var routes = (IEndpointRouteBuilder)app;
        map(routes.MapEndpointGroup("Fidelity", tag: tag));

        return routes.DataSources.SelectMany(source => source.Endpoints).Single();
    }
}

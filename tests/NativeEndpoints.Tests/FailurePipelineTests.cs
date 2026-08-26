using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// The shared failure path: fault renderers first, then exception translators in registration order
/// with the group-keyed registrations ahead of the unkeyed ones, then the sanitized generic 500.
/// </summary>
public class FailurePipelineTests
{
    private const string GroupName = "Faults";

    private sealed class TeapotException : Exception;

    private sealed class ConflictException : Exception;

    private sealed class Translator(Func<Exception, EndpointProblem?> translate) : IEndpointExceptionTranslator
    {
        public EndpointProblem? Translate(Exception exception) => translate(exception);
    }

    private sealed class Renderer(bool handles) : IEndpointFaultRenderer
    {
        public async ValueTask<bool> TryWriteAsync(HttpContext context, Exception exception)
        {
            if (!handles)
                return false;

            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await context.Response.WriteAsync("rendered");
            return true;
        }
    }

    [Fact]
    public async Task A_registered_translator_turns_a_domain_exception_into_its_status()
    {
        using var host = Host(services => services.AddSingleton<IEndpointExceptionTranslator>(
            new Translator(exception => exception is ConflictException
                ? EndpointProblem.General(StatusCodes.Status409Conflict, "state conflict")
                : null)));

        var (status, body) = await Send(host, "/throw/conflict");

        Assert.Equal(409, status);
        Assert.Contains("state conflict", body);
    }

    [Fact]
    public async Task Translators_run_in_registration_order_and_the_first_non_null_result_wins()
    {
        using var host = Host(services =>
        {
            services.AddSingleton<IEndpointExceptionTranslator>(
                new Translator(exception => exception is TeapotException
                    ? EndpointProblem.General(StatusCodes.Status418ImATeapot, "first")
                    : null));
            services.AddSingleton<IEndpointExceptionTranslator>(
                new Translator(exception => exception switch
                {
                    TeapotException => EndpointProblem.General(StatusCodes.Status409Conflict, "second"),
                    ConflictException => EndpointProblem.General(StatusCodes.Status409Conflict, "second"),
                    _ => null
                }));
        });

        // The first translator claims the teapot, so the second never sees it.
        var (teapotStatus, teapotBody) = await Send(host, "/throw/teapot");
        Assert.Equal(418, teapotStatus);
        Assert.Contains("first", teapotBody);

        // The first returns null for the conflict, so the second is consulted.
        var (conflictStatus, conflictBody) = await Send(host, "/throw/conflict");
        Assert.Equal(409, conflictStatus);
        Assert.Contains("second", conflictBody);
    }

    [Fact]
    public async Task A_translator_keyed_by_the_group_name_wins_over_the_unkeyed_one()
    {
        using var host = Host(services =>
        {
            services.AddSingleton<IEndpointExceptionTranslator>(
                new Translator(_ => EndpointProblem.General(StatusCodes.Status409Conflict, "unkeyed")));
            services.AddKeyedSingleton<IEndpointExceptionTranslator>(GroupName,
                new Translator(_ => EndpointProblem.General(StatusCodes.Status402PaymentRequired, "keyed")));
        });

        var (status, body) = await Send(host, "/throw/conflict");

        Assert.Equal(402, status);
        Assert.Contains("keyed", body);
    }

    [Fact]
    public async Task An_untranslated_exception_is_a_sanitized_500_problem()
    {
        using var host = Host(_ => { });

        var (status, body) = await Send(host, "/throw/other");

        Assert.Equal(500, status);
        Assert.Contains("Unexpected error occurred", body);
        Assert.DoesNotContain("sensitive connection string detail", body);
    }

    [Fact]
    public async Task A_fault_renderer_runs_before_translators_and_short_circuits_when_it_handles()
    {
        using var host = Host(services =>
        {
            services.AddSingleton<IEndpointFaultRenderer>(new Renderer(handles: true));
            services.AddSingleton<IEndpointExceptionTranslator>(
                new Translator(_ => EndpointProblem.General(StatusCodes.Status409Conflict, "translated")));
        });

        var (status, body) = await Send(host, "/throw/conflict");

        Assert.Equal(422, status);
        Assert.Equal("rendered", body);
    }

    [Fact]
    public async Task A_fault_renderer_that_declines_lets_translation_proceed()
    {
        using var host = Host(services =>
        {
            services.AddSingleton<IEndpointFaultRenderer>(new Renderer(handles: false));
            services.AddSingleton<IEndpointExceptionTranslator>(
                new Translator(_ => EndpointProblem.General(StatusCodes.Status409Conflict, "translated")));
        });

        var (status, body) = await Send(host, "/throw/conflict");

        Assert.Equal(409, status);
        Assert.Contains("translated", body);
    }

    private static async Task<(int Status, string Body)> Send(IHost host, string url)
    {
        using var client = host.GetTestClient();
        var response = await client.GetAsync(url);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static IHost Host(Action<IServiceCollection> configure) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints();
                    configure(services);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints
                        .MapEndpointGroup(GroupName)
                        .MapHandler<string>("GET", "throw/{kind}", "Throw", (context, _) =>
                        {
                            var kind = context.Request.RouteValues["kind"]?.ToString();
                            throw kind switch
                            {
                                "teapot" => new TeapotException(),
                                "conflict" => new ConflictException(),
                                _ => new InvalidOperationException("sensitive connection string detail")
                            };
                        }));
                }))
            .Start();
}

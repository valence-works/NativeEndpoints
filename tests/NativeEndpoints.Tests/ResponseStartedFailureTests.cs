using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NativeEndpoints.Tests;

/// <summary>
/// A handler that throws after the response started streaming cannot be answered with a problem
/// document: setting the status would throw an InvalidOperationException that replaces the real
/// failure. The pipeline logs the original exception and aborts the connection instead, so the
/// truncated response is not mistaken for a complete one.
/// </summary>
public class ResponseStartedFailureTests
{
    /// <summary>The original failure, distinct from any secondary InvalidOperationException.</summary>
    private sealed class MidWriteException() : Exception("boom after the response started");

    /// <summary>Would translate anything, proving translators are not consulted after start.</summary>
    private sealed class RecordingTranslator : IEndpointExceptionTranslator
    {
        public bool Consulted { get; private set; }

        public EndpointProblem? Translate(Exception exception)
        {
            Consulted = true;
            return EndpointProblem.General(StatusCodes.Status409Conflict, "translated");
        }
    }

    private sealed class RecordingRenderer : IEndpointFaultRenderer
    {
        public bool Consulted { get; private set; }

        public ValueTask<bool> TryWriteAsync(HttpContext context, Exception exception)
        {
            Consulted = true;
            return ValueTask.FromResult(false);
        }
    }

    private sealed record CapturedLog(string Category, Exception? Exception, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new CapturedLog(category, exception, formatter(state, exception)));
        }
    }

    [Theory]
    [InlineData("/bound-partial")]    // the bound pipeline (MapOperation)
    [InlineData("/unbound-partial")]  // the no-contract pipeline (MapUnbound)
    public async Task A_failure_after_the_response_started_logs_the_original_and_aborts(string url)
    {
        var logs = new CapturingLoggerProvider();
        var translator = new RecordingTranslator();
        var renderer = new RecordingRenderer();
        using var host = Host(logs, translator, renderer);
        using var client = host.GetTestClient();

        // TestServer surfaces an aborted response as a failed send or a truncated read; either is
        // acceptable here. What must NOT happen is the pre-fix behavior: the problem writer setting
        // Response.StatusCode after the start, whose InvalidOperationException ("StatusCode cannot
        // be set because the response has already started") replaced the real failure and surfaced
        // to the TestHost client instead of MidWriteException ever being logged.
        Exception? clientFailure = null;
        try
        {
            using var response = await client.GetAsync(url);
            await response.Content.ReadAsStringAsync();
        }
        catch (Exception exception)
        {
            clientFailure = exception;
        }

        for (var failure = clientFailure; failure is not null; failure = failure.InnerException)
            Assert.IsNotType<InvalidOperationException>(failure);

        // The original exception is what got logged, through the shared unexpected-failure log.
        var logged = Assert.Single(logs.Entries, entry => entry.Exception is not null);
        Assert.IsType<MidWriteException>(logged.Exception);
        Assert.Contains("Unexpected error occurred", logged.Message);
        Assert.Equal(typeof(EndpointGroup).FullName, logged.Category);

        // Renderers and translators write responses, so neither is consulted after the start.
        Assert.False(renderer.Consulted);
        Assert.False(translator.Consulted);
    }

    private static async Task WritePartialThenThrow(HttpContext context)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("partial");
        await context.Response.Body.FlushAsync();
        throw new MidWriteException();
    }

    private static IHost Host(CapturingLoggerProvider logs, RecordingTranslator translator, RecordingRenderer renderer) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddNativeEndpoints();
                    services.AddLogging(logging => logging.AddProvider(logs));
                    services.AddSingleton<IEndpointExceptionTranslator>(translator);
                    services.AddSingleton<IEndpointFaultRenderer>(renderer);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        var group = endpoints.MapEndpointGroup("Streaming");
                        group.MapHandler<StreamingProbe, string>("GET", "bound-partial", "BoundPartial",
                            async (context, _, _) =>
                            {
                                await WritePartialThenThrow(context);
                                return "unreachable";
                            });
                        group.MapHandler<string>("GET", "unbound-partial", "UnboundPartial",
                            async (context, _) =>
                            {
                                await WritePartialThenThrow(context);
                                return "unreachable";
                            });
                    });
                }))
            .Start();
}

/// <summary>An empty contract, so the bound pipeline runs with nothing to bind from a GET.</summary>
public sealed record StreamingProbe;

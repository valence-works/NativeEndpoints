using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace NativeEndpoints.Benchmarks;

/// <summary>
/// POST /items on each stack: a ~5-property JSON body deserialized into a request contract, a small
/// JSON echo out. Measures the framework's body-binding + serialization layer, with raw minimal
/// APIs as the baseline. The payload is pre-encoded once so the client side adds the same constant
/// cost to every stack.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("PostJsonBody")]
public class PostJsonBodyBenchmarks
{
    private const string Payload = """{"name":"Widget","sku":"W-1001","quantity":3,"price":19.95,"active":true}""";

    /// <summary>What every stack must return, so a misbinding stack fails setup instead of "winning".</summary>
    private const string Expected = """{"name":"Widget","sku":"W-1001","quantity":3,"price":19.95,"active":true}""";

    private static readonly byte[] PayloadBytes = Encoding.UTF8.GetBytes(Payload);

    private Stack _raw = null!;
    private Stack _reflective = null!;
    private Stack _generated = null!;
    private Stack _fastEndpoints = null!;

    [GlobalSetup]
    public void Setup()
    {
        _raw = Stacks.RawMinimalApi();
        _reflective = Stacks.NativeReflective();
        _generated = Stacks.NativeGenerated();
        _fastEndpoints = Stacks.FastEndpoints();

        // One conformance pass before measuring: identical status and semantically identical body
        // everywhere, or the run aborts. This is what keeps the four benchmarks comparable.
        foreach (var stack in (Stack[])[_raw, _reflective, _generated, _fastEndpoints])
            Verify(stack).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task RawMinimalApi() => await Send(_raw);

    [Benchmark]
    public async Task NativeReflective() => await Send(_reflective);

    [Benchmark]
    public async Task NativeGenerated() => await Send(_generated);

    [Benchmark]
    public async Task FastEndpoints() => await Send(_fastEndpoints);

    [GlobalCleanup]
    public void Cleanup()
    {
        _raw.Dispose();
        _reflective.Dispose();
        _generated.Dispose();
        _fastEndpoints.Dispose();
    }

    private static async Task Send(Stack stack)
    {
        using var response = await stack.Client.PostAsync("/items", Content());
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Expected 200, got {(int)response.StatusCode}.");
    }

    private static async Task Verify(Stack stack)
    {
        using var response = await stack.Client.PostAsync("/items", Content());
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Conformance: expected 200, got {(int)response.StatusCode}: {body}");

        if (!JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse(Expected)))
            throw new InvalidOperationException($"Conformance: expected {Expected}, got {body}");
    }

    private static ByteArrayContent Content() =>
        new(PayloadBytes)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        };
}

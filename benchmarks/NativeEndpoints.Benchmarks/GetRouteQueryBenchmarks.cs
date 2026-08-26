using System.Net;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace NativeEndpoints.Benchmarks;

/// <summary>
/// GET /items/42?tag=a&amp;tag=b&amp;page=5 on each stack: an int bound from the route, a string[]
/// and an int bound from the query, and a small JSON echo out. Measures the per-request cost of the
/// framework's routing + binding + serialization layer, with raw minimal APIs as the baseline.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("GetRouteQuery")]
public class GetRouteQueryBenchmarks
{
    private const string Url = "/items/42?tag=a&tag=b&page=5";

    /// <summary>What every stack must return, so a misbinding stack fails setup instead of "winning".</summary>
    private const string Expected = """{"id":42,"tags":["a","b"],"page":5}""";

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
        using var response = await stack.Client.GetAsync(Url);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Expected 200, got {(int)response.StatusCode}.");
    }

    private static async Task Verify(Stack stack)
    {
        using var response = await stack.Client.GetAsync(Url);
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Conformance: expected 200, got {(int)response.StatusCode}: {body}");

        if (!JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse(Expected)))
            throw new InvalidOperationException($"Conformance: expected {Expected}, got {body}");
    }
}

using NativeEndpoints.Testing.Collectibility;
using Xunit;

namespace NativeEndpoints.Tests.Kit;

/// <summary>
/// The README tells readers not to take its word for it and shows them this exact code. If these
/// fail, the claim on the front page is false.
/// </summary>
public class ReadmeClaimTests
{
    [Fact]
    public void Endpoint_assemblies_are_collected()
    {
        var evidence = CollectibleEndpointFixture.RunCycles(cycles: 3);

        UnloadEvidence.AssertAllCollected(evidence);
    }

    [Fact]
    public void The_harness_can_still_detect_a_leak()
    {
        // A harness that always says "collected" proves nothing. Introduce a deliberate root, keep it,
        // and confirm the evidence reports the context as still alive.
        using var run = CollectibleEndpointFixture.Create(RetentionStage.Route);

        var evidence = UnloadEvidence.Verify(run, maxAttempts: 4);

        Assert.False(evidence.Collected);
        Assert.Equal(RetentionStage.Route, evidence.Stage);
    }
}

using System.Runtime.CompilerServices;

namespace NativeEndpoints.Testing.Collectibility;

/// <summary>
/// Weak-reference-only unload evidence for one collectible endpoint cycle.
/// </summary>
public sealed class UnloadEvidence
{
    public const int DefaultMaxCollectionAttempts = 12;
    private const int MaximumCollectionAttempts = 32;

    private UnloadEvidence(
        Guid cycle,
        RetentionStage stage,
        WeakReference loadContext,
        WeakReference assembly,
        WeakReference endpointType,
        bool collected,
        int collectionAttempts,
        string? diagnostic)
    {
        Cycle = cycle;
        Stage = stage;
        LoadContext = loadContext;
        Assembly = assembly;
        EndpointType = endpointType;
        Collected = collected;
        CollectionAttempts = collectionAttempts;
        Diagnostic = diagnostic;
    }

    public Guid Cycle { get; }

    /// <summary>The stage that still owns a strong reference, or <see cref="RetentionStage.Clean"/>.</summary>
    public RetentionStage Stage { get; }

    public WeakReference LoadContext { get; }

    public WeakReference Assembly { get; }

    public WeakReference EndpointType { get; }

    public bool Collected { get; }

    public int CollectionAttempts { get; }

    /// <summary>
    /// A short, stable classification. It contains only static text and never includes a loaded
    /// assembly, type, route object, service object, or serializer object.
    /// </summary>
    public string? Diagnostic { get; }

    /// <summary>
    /// Throws unless every cycle's context was collected, naming the stage that still roots it.
    /// </summary>
    /// <remarks>
    /// The assertion most test suites want. <see cref="Verify(CollectibleEndpointCycle, int)"/>
    /// reports; this one fails the test, with the diagnostic that says which stage held on.
    /// </remarks>
    public static void AssertAllCollected(IEnumerable<UnloadEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var uncollected = evidence.Where(item => !item.Collected).ToArray();
        if (uncollected.Length == 0)
            return;

        var detail = string.Join(Environment.NewLine, uncollected.Select(item =>
            $"- cycle {item.Cycle}; stage={item.Stage}; attempts={item.CollectionAttempts}; " +
            $"stillRooted={Rooted(item)}{(item.Diagnostic is null ? "" : $"; {item.Diagnostic}")}"));

        throw new InvalidOperationException(
            $"{uncollected.Length} collectible endpoint context(s) were not released:{Environment.NewLine}{detail}");

        static string Rooted(UnloadEvidence item) => string.Join(
            ", ",
            new[]
            {
                item.LoadContext.IsAlive ? "loadContext" : null,
                item.Assembly.IsAlive ? "assembly" : null,
                item.EndpointType.IsAlive ? "endpointType" : null
            }.Where(name => name is not null));
    }

    public static UnloadEvidence Verify(CollectibleEndpointCycle cycle, int maxAttempts = DefaultMaxCollectionAttempts)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return Verify(cycle.CycleId, cycle.LoadContext, cycle.Assembly, cycle.EndpointType, maxAttempts);
    }

    /// <summary>
    /// Verifies arbitrary collectible endpoint evidence without requiring the caller to expose
    /// framework-specific route or service owners through the shared compatibility model.
    /// </summary>
    public static UnloadEvidence Verify(
        Guid cycleId,
        WeakReference loadContext,
        WeakReference assembly,
        WeakReference endpointType,
        int maxAttempts = DefaultMaxCollectionAttempts)
    {
        ArgumentNullException.ThrowIfNull(loadContext);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(endpointType);
        if (maxAttempts is < 1 or > MaximumCollectionAttempts)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts,
                $"Collection attempts must be between 1 and {MaximumCollectionAttempts}.");

        var collected = false;
        var attempts = 0;
        for (; attempts < maxAttempts; attempts++)
        {
            ForceCollection();
            if (!loadContext.IsAlive && !assembly.IsAlive && !endpointType.IsAlive)
            {
                collected = true;
                attempts++;
                break;
            }
        }

        var stage = collected ? RetentionStage.Clean : RetentionStageProbe.PublishedStage(cycleId);
        var diagnostic = collected ? null : Describe(stage);
        return new UnloadEvidence(
            cycleId,
            stage,
            loadContext,
            assembly,
            endpointType,
            collected,
            attempts,
            diagnostic);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static string Describe(RetentionStage stage) => stage switch
    {
        RetentionStage.Route => "route retention",
        RetentionStage.Services => "DI/services retention",
        RetentionStage.Serializer => "serializer retention",
        RetentionStage.Harness => "harness retention",
        _ => "harness retention (unexpected collectible reference)"
    };
}

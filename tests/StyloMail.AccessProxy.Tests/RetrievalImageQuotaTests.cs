using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Tests;

public sealed class RetrievalImageQuotaTests
{
    [Fact]
    public async Task ImageScratchAdapterChargesSessionAndPoolAndRollsBack()
    {
        await using var pool = new ProxyRetrievalWorkPool(maximumBytes: 1024, workers: 1);
        var assembler = new ProxyRetrievalWarningAssembler(
            "acme", "acct",
            new NullSink(),
            new NullAnalyzer(),
            TimeProvider.System,
            new ProxyRetrievalWarningAssemblerOptions
            {
                MaximumMessageBytes = 256,
                MaximumSessionBufferedBytes = 512,
                MaximumQueuedChunks = 1,
            },
            elementOrchestrator: null,
            pool);

        var quota = new ProxyRetrievalImageScratchQuota(assembler);

        Assert.True(quota.TryReserve(512));
        Assert.Equal(512, assembler.ReservedBytes);
        Assert.Equal(512, pool.Counters.BufferedBytes);

        Assert.False(quota.TryReserve(512));
        Assert.Equal(512, assembler.ReservedBytes);
        Assert.Equal(512, pool.Counters.BufferedBytes);

        quota.Release(512);
        Assert.Equal(0, assembler.ReservedBytes);
        Assert.Equal(0, pool.Counters.BufferedBytes);
        await assembler.DisposeAsync();
    }

    private sealed class NullSink : IProxyWarningEventSink
    {
        public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class NullAnalyzer : IProxyWarningAnalyzer
    {
        public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(
            ProxyWarningAnalysisInput input,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new ProxyWarningAnalysisResult
            {
                MessageToken = "m",
                ThreadToken = "t",
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxyWarningAvailabilityReason.None,
                Findings = [],
            });
    }
}

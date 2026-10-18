using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Host.Proxy;

/// <summary>One content-free loss warning per interval; no I/O on the retrieval-copy path.</summary>
internal sealed class ProxyRetrievalObservationLog(ILogger<ProxyRetrievalObservationLog> logger) : BackgroundService
{
    private readonly ProxyRetrievalObservationCounters _initial = ProxyRetrievalWarningAssemblerFactory.Counters;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var previous = _initial;
        var start = _started;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var current = ProxyRetrievalWarningAssemblerFactory.Counters;
                var end = DateTimeOffset.UtcNow;
                var chunks = current.DroppedChunks - previous.DroppedChunks;
                if (chunks > 0)
                    logger.LogWarning("Retrieval analysis {Availability}/{Reason}: dropped {DroppedChunks} chunks/{DroppedBytes} bytes during {IntervalStart} to {IntervalEnd}.",
                        ProxyWarningAvailability.NotProduced, ProxyWarningAvailabilityReason.ServiceUnavailable,
                        chunks, current.DroppedBytes - previous.DroppedBytes, start, end);
                previous = current; start = end;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

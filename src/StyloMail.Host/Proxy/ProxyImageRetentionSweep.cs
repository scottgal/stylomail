using Microsoft.Extensions.Options;

namespace StyloMail.Host.Proxy;

/// <summary>
/// Deletes expired image observation rows in bounded batches.
/// </summary>
/// <remarks>
/// One batch per tick, at most 1000 messages, so a large backlog drains over several ticks rather
/// than holding one long write transaction. The sweep is independent of whether image inspection is
/// currently enabled: rows written while it was enabled still expire.
/// </remarks>
public sealed class ProxyImageRetentionSweep : BackgroundService
{
    internal const int BatchSize = 1000;
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IProxyImageObservationReader _store;
    private readonly IOptions<HostProxyOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProxyImageRetentionSweep> _logger;

    public ProxyImageRetentionSweep(
        IProxyImageObservationReader store,
        IOptions<HostProxyOptions> options,
        TimeProvider clock,
        ILogger<ProxyImageRetentionSweep> logger)
    {
        _store = store;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sweep once at startup so a backlog from a previous process is not held for a full
        // interval, then on the fixed cadence.
        await SweepAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var days = _options.Value.ImageObservationRetentionDays;
        if (days is < 1 or > 365)
        {
            _logger.LogWarning(
                "Image observation retention is outside its bounded range; the sweep is skipped.");
            return;
        }

        try
        {
            var deleted = await _store.DeleteExpiredAsync(
                _clock.GetUtcNow(),
                BatchSize,
                cancellationToken).ConfigureAwait(false);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Expired {MessageCount} image observation message(s) past the {RetentionDays}-day window.",
                    deleted,
                    days);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Retention is maintenance; a failed pass is logged without a content-bearing message
            // and does not stop the host. Rows stay until a later pass succeeds.
            _logger.LogWarning(exception, "Image observation retention sweep failed; rows were not deleted.");
        }
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Submission;

namespace StyloMail.Host.Proxy;

/// <summary>
/// Sweeps abandoned submission staging handles older than the configured TTL.
/// </summary>
/// <remarks>
/// The in-memory stager's handles are disposed by the SMTP session on every terminal path, and a
/// process crash releases them with the process. The sweep exists for the contract a future
/// file-backed stager must honour, and it is wired now so that swapping the implementation does not
/// require remembering to add the sweep. Running it against the in-memory implementation is a
/// no-op, not a false claim that durable cleanup happened.
/// </remarks>
public sealed class ProxySubmissionStagerSweep : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IEphemeralSubmissionStager _stager;
    private readonly IOptions<HostProxyOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProxySubmissionStagerSweep> _logger;

    public ProxySubmissionStagerSweep(
        IEphemeralSubmissionStager stager,
        IOptions<HostProxyOptions> options,
        TimeProvider clock,
        ILogger<ProxySubmissionStagerSweep> logger)
    {
        _stager = stager;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _stager.DeleteExpiredAsync(
                    _clock.GetUtcNow() - _options.Value.SubmissionStagingTtl,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A failed sweep must not take the host down; the next tick retries, and the
                // stager's own bounded handles remain the primary protection.
                _logger.LogWarning("Proxy submission staging sweep failed.");
            }
        }
    }
}

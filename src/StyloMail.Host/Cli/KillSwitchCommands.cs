using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Controls;

namespace StyloMail.Host.Cli;

/// <summary>
/// The emergency stop, engaged and released from the executable.
/// </summary>
/// <remarks>
/// <para>
/// <b>A CLI verb rather than a screen, on the same reasoning as minting a key.</b> Pulling the stop is
/// a bootstrap action an operator takes while something is going wrong, and the executable is the
/// surface that exists whether or not a console is running.
/// </para>
/// <para>
/// <b><c>--by</c> is required.</b> Engaging the emergency stop is the most consequential thing this
/// CLI can do, and it has no identity to sign with. Inventing one would put a name in the audit record
/// that nobody asserted, which is the same rule <c>quarantine release --by</c> and <c>key create
/// --by</c> already carry.
/// </para>
/// </remarks>
public static class KillSwitchCommands
{
    private const int Ok = 0;

    public static async Task<int> SetAsync(
        IServiceProvider services,
        KillSwitchCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var store = services.GetRequiredService<SqliteEmergencyKillSwitch>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var changed = command.Engage
            ? store.Engage(command.Actor, now)
            : store.Disengage(command.Actor, now);

        if (!changed)
        {
            await WriteUnchangedAsync(store, command, output);
            return Ok;
        }

        if (command.Engage)
        {
            await output.WriteLineAsync(
                $"Emergency stop ENGAGED by {command.Actor}. Every assessment on every channel now "
                + "reads the switch as engaged and the policy engine refuses to allow on risk, so "
                + "traffic is held rather than delivered. The stop persists across a restart: "
                + "release it with 'killswitch disengage --by <you>'.");
        }
        else
        {
            await output.WriteLineAsync(
                $"Emergency stop released by {command.Actor}. Assessments return to their ordinary "
                + "policy.");
        }

        return Ok;
    }

    /// <summary>
    /// Reports a verb that changed nothing, and says since when.
    /// </summary>
    /// <remarks>
    /// Exit 0 rather than an error, because the state the caller asked for already holds. The message
    /// names when it started holding, so an operator who did not know the stop was already engaged
    /// learns that from this rather than from an investigation. Same reasoning as <c>key revoke</c> on
    /// a principal that is already revoked.
    /// </remarks>
    private static async Task WriteUnchangedAsync(
        SqliteEmergencyKillSwitch store,
        KillSwitchCommand command,
        TextWriter output)
    {
        if (!command.Engage)
        {
            await output.WriteLineAsync(
                "The emergency stop is not engaged; nothing recorded. Assessments are running "
                + "ordinarily.");
            return;
        }

        var latest = store.History(1);
        var since = latest.Count > 0 ? latest[0].OccurredAt.ToString("u") : "an unknown time";

        await output.WriteLineAsync(
            $"The emergency stop is already engaged, since {since}; nothing recorded. It stays "
            + $"engaged until it is released.");
    }
}

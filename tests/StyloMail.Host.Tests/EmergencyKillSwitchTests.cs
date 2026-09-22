using Microsoft.Extensions.DependencyInjection;
using StyloMail.Assessment;
using StyloMail.Host.Cli;
using StyloMail.Host.Controls;
using StyloMail.Host.Hosting;

namespace StyloMail.Host.Tests;

/// <summary>The emergency stop, which before this existed could not be engaged at all.</summary>
/// <remarks>
/// The specification lists it second in policy precedence, and the only context source supplied
/// nothing, so it was false on every assessment in every deployment. These pin the three things that
/// make it a control rather than a field: it outlives the process, it records who pulled it, and an
/// empty history reads as disengaged rather than as unknown.
/// </remarks>
public sealed class EmergencyKillSwitchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    [Fact]
    public void A_stop_nobody_engaged_reads_as_disengaged()
    {
        // An empty history is the correct reading of "not engaged" rather than a default standing in
        // for a missing answer, which is what the old static source effectively did.
        using var host = new TestHost();

        Assert.False(host.Services.GetRequiredService<IEmergencyKillSwitch>().IsEngaged);
    }

    [Fact]
    public void An_engaged_stop_survives_the_process_that_engaged_it()
    {
        // The property that makes it worth persisting: a stop that silently disengages on restart is
        // worse than one that was never wired, because it teaches an operator to trust it.
        using var host = new TestHost();
        host.Services.GetRequiredService<SqliteEmergencyKillSwitch>().Engage("ops@acme", Now);

        using var reopened = new TestHost().ReusingStorageOf(host);

        Assert.True(reopened.Services.GetRequiredService<IEmergencyKillSwitch>().IsEngaged);
    }

    [Fact]
    public void Engaging_twice_records_one_transition()
    {
        // The history is a list of transitions. A caller holding the button down must not bury the
        // entry that says when the system actually stopped.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<SqliteEmergencyKillSwitch>();

        Assert.True(store.Engage("ops@acme", Now));
        Assert.False(store.Engage("ops@acme", Now.AddMinutes(1)));

        Assert.Single(store.History(10));
    }

    [Fact]
    public void Disengaging_a_stop_that_was_never_engaged_changes_nothing()
    {
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<SqliteEmergencyKillSwitch>();

        Assert.False(store.Disengage("ops@acme", Now));
        Assert.Empty(store.History(10));
        Assert.False(store.IsEngaged);
    }

    [Fact]
    public void The_history_names_who_pulled_it_and_when()
    {
        // "Is it engaged" is the easy question. "Who stopped the system, and when" is the one asked
        // afterwards, and a single mutable flag cannot answer it.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<SqliteEmergencyKillSwitch>();

        store.Engage("ops@acme", Now);
        store.Disengage("ops@acme", Now.AddMinutes(5));
        store.Engage("oncall@acme", Now.AddMinutes(9));

        var history = store.History(10);

        Assert.Equal(3, history.Count);
        Assert.Equal("oncall@acme", history[0].Actor);
        Assert.True(history[0].Engaged);
        Assert.Equal(Now.AddMinutes(9), history[0].OccurredAt);
        Assert.False(history[1].Engaged);
        Assert.Equal(Now, history[2].OccurredAt);
    }

    [Fact]
    public void An_actor_is_required()
    {
        // The same rule the key CLI established: a mutating command with no identity to sign with
        // must not invent one, and the store refuses rather than recording an anonymous transition.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<SqliteEmergencyKillSwitch>();

        Assert.Throws<ArgumentException>(() => { store.Engage("  ", Now); });
    }

    [Fact]
    public async Task The_context_the_mail_path_reads_carries_the_engaged_stop()
    {
        // The gap this closes. The only context source the pipeline could be given supplied nothing,
        // so EmergencyKillSwitchEngaged was false on every assessment in every deployment. This pins
        // that the host supplies one that reads the switch.
        using var host = new TestHost();
        host.Services.GetRequiredService<SqliteEmergencyKillSwitch>().Engage("ops@acme", Now);

        var source = new HostPolicyContextSource(
            host.Services.GetRequiredService<IEmergencyKillSwitch>());

        // The message and the assessment context are deliberately absent: the source ignores both,
        // which is exactly why the switch is a port of its own rather than a field on a port typed on
        // a mail input. A channel with a different input can still read it.
        var context = await source.GetAsync(null!, default!, CancellationToken.None);

        Assert.True(context.EmergencyKillSwitchEngaged);
    }

    [Fact]
    public void The_cli_refuses_to_move_the_stop_without_an_actor()
    {
        // The most consequential verb in the executable, and it has no identity to sign with.
        Assert.False(CliApplication.TryParse(["killswitch", "engage"], out _, out var error));
        Assert.Contains("--by", error!);

        Assert.False(CliApplication.TryParse(["killswitch", "disengage"], out _, out _));
        Assert.False(CliApplication.TryParse(["killswitch", "engage", "--by", "   "], out _, out _));
        Assert.False(CliApplication.TryParse(["killswitch", "sideways", "--by", "ops@acme"], out _, out _));
    }

    [Fact]
    public void The_cli_parses_both_verbs_with_an_actor()
    {
        Assert.True(CliApplication.TryParse(
            ["killswitch", "engage", "--by", "ops@acme"], out var engage, out _));
        Assert.Equal(new KillSwitchCommand(true, "ops@acme"), engage);

        Assert.True(CliApplication.TryParse(
            ["killswitch", "disengage", "--by", "ops@acme"], out var release, out _));
        Assert.Equal(new KillSwitchCommand(false, "ops@acme"), release);
    }
}

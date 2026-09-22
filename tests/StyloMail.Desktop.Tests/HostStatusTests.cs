using System.Net;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// What the console tells an operator about the Host it is pointed at.
/// </summary>
/// <remarks>
/// This is the first thing anyone sees and the thing they read when nothing
/// works, so each state has to name a different remedy. A console that renders
/// all six of these as "connection error" has taken the one screen whose whole
/// job is diagnosis and made it useless.
/// </remarks>
public sealed class HostStatusTests
{
    private static StyloMailApiException Failure(
        StyloMailApiFailure kind,
        HttpStatusCode? status = null,
        string? code = null) => new(kind, "test", status, code);

    [Fact]
    public void A_ready_host_is_ready()
    {
        var status = HostStatus.From(new ReadinessResponse { Status = "ready" });

        Assert.Equal(HostStatusKind.Ready, status.Kind);
        Assert.Empty(status.FailedChecks);
    }

    /// <summary>
    /// Not ready is the state an operator most needs described: the Host is up
    /// and is deliberately refusing mail. The failed checks are the answer to
    /// "why", so they are carried through rather than summarised.
    /// </summary>
    [Fact]
    public void A_host_that_cannot_accept_mail_names_what_failed()
    {
        var status = HostStatus.From(new ReadinessResponse
        {
            Status = "not_ready",
            FailedChecks = ["spool_directory_not_writable"],
        });

        Assert.Equal(HostStatusKind.NotReady, status.Kind);
        Assert.Equal(["spool_directory_not_writable"], status.FailedChecks);
    }

    /// <summary>
    /// The first-run state, and the only one whose remedy is entirely within
    /// this app. It is also the state the client reaches without sending
    /// anything, so nothing about it should read as a network problem.
    /// </summary>
    [Fact]
    public void A_console_with_no_key_asks_for_one()
    {
        var status = HostStatus.FromFailure(Failure(StyloMailApiFailure.ApiKeyNotConfigured));

        Assert.Equal(HostStatusKind.NeedsApiKey, status.Kind);
        Assert.DoesNotContain("error", status.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refused key is not a missing one: the operator has set something and
    /// it is wrong. Telling them to enter a key they already entered is the
    /// specific unhelpfulness this case exists to avoid.
    /// </summary>
    [Fact]
    public void A_refused_key_is_distinguished_from_a_missing_one()
    {
        var status = HostStatus.FromFailure(Failure(
            StyloMailApiFailure.HostRefused,
            HttpStatusCode.Unauthorized));

        Assert.Equal(HostStatusKind.ApiKeyRejected, status.Kind);
    }

    [Fact]
    public void An_unreachable_host_says_so_rather_than_blaming_the_key()
    {
        var status = HostStatus.FromFailure(Failure(StyloMailApiFailure.Unreachable));

        Assert.Equal(HostStatusKind.Unreachable, status.Kind);
    }

    /// <summary>
    /// Version skew between console and Host is its own state. Retrying will
    /// not help and the key is not at fault, so presenting it as either would
    /// send the operator to fix the wrong thing.
    /// </summary>
    [Fact]
    public void A_response_this_build_cannot_read_is_reported_as_version_skew()
    {
        var status = HostStatus.FromFailure(Failure(StyloMailApiFailure.UnreadableResponse));

        Assert.Equal(HostStatusKind.VersionSkew, status.Kind);
    }

    /// <summary>
    /// A refusal that is not about the key, for instance a missing review
    /// privilege. The Host's own code is carried so the operator can look it
    /// up rather than guess.
    /// </summary>
    [Fact]
    public void Any_other_refusal_keeps_the_hosts_code()
    {
        var status = HostStatus.FromFailure(Failure(
            StyloMailApiFailure.HostRefused,
            HttpStatusCode.Forbidden,
            "forbidden"));

        Assert.Equal(HostStatusKind.Refused, status.Kind);
        Assert.Contains("forbidden", status.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Something answered at the address that is not StyloMail.
    /// </summary>
    /// <remarks>
    /// Not hypothetical, and found by photographing the window rather than by
    /// reading the code: macOS runs AirPlay Receiver on port 5000, which is the
    /// Host's own documented default, and it answers 403 with an HTML body. The
    /// console reported "the Host refused the request", which sends an operator
    /// to look for an authentication problem at a service that is simply not
    /// there.
    /// </remarks>
    [Fact]
    public void A_refusal_with_no_code_from_something_that_is_not_stylomail_says_so()
    {
        var status = HostStatus.FromFailure(Failure(
            StyloMailApiFailure.HostRefused,
            HttpStatusCode.Forbidden));

        Assert.Equal(HostStatusKind.Refused, status.Kind);
        Assert.Contains("not a StyloMail Host", status.Headline, StringComparison.Ordinal);

        // And says what to check. "Something answered" on its own leaves the
        // operator with the same address and no next step.
        Assert.Contains("port", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Before anything has been asked, the console claims nothing.</summary>
    [Fact]
    public void An_unknown_state_claims_nothing()
    {
        var status = HostStatus.Unknown;

        Assert.Equal(HostStatusKind.Unknown, status.Kind);
        Assert.Empty(status.FailedChecks);
    }
}

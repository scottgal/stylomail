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
/// works, so the failures have to name different remedies. A console that
/// renders all eight of these as "connection error" has taken the one screen
/// whose whole job is diagnosis and made it useless.
/// </remarks>
public sealed class HostStatusTests
{
    private static StyloMailApiException Failure(
        StyloMailApiFailure kind,
        HttpStatusCode? status = null,
        string? code = null) => new(kind, "test", status, code);

    /// <summary>
    /// One status per kind, for the coverage below.
    /// </summary>
    /// <remarks>
    /// The discard arm throws rather than returning something plausible. A
    /// representative that quietly fell back to <see cref="HostStatus.Unknown"/>
    /// would make the coverage pass while checking the same object eight times,
    /// and a kind added to the enum without a line here would be skipped in
    /// silence rather than failing the build.
    ///
    /// Three of the kinds cannot come from <c>FromFailure</c> at all, which is
    /// why this is a function of the kind rather than a single factory call.
    /// </remarks>
    private static HostStatus Representative(HostStatusKind kind) => kind switch
    {
        HostStatusKind.Unknown => HostStatus.Unknown,
        HostStatusKind.Ready => HostStatus.From(new ReadinessResponse { Status = "ready" }),
        HostStatusKind.NotReady => HostStatus.From(new ReadinessResponse { Status = "not_ready" }),

        HostStatusKind.NeedsApiKey =>
            HostStatus.FromFailure(Failure(StyloMailApiFailure.ApiKeyNotConfigured)),

        HostStatusKind.ApiKeyRejected => HostStatus.FromFailure(
            Failure(StyloMailApiFailure.HostRefused, HttpStatusCode.Unauthorized)),

        HostStatusKind.Unreachable =>
            HostStatus.FromFailure(Failure(StyloMailApiFailure.Unreachable)),

        HostStatusKind.VersionSkew =>
            HostStatus.FromFailure(Failure(StyloMailApiFailure.UnreadableResponse)),

        // The code is carried so this lands on the branch that keeps the Host's
        // own words, rather than the one for a refusal with neither code nor
        // sentence, which is the AirPlay case and has its own test.
        HostStatusKind.Refused => HostStatus.FromFailure(
            Failure(StyloMailApiFailure.HostRefused, HttpStatusCode.Forbidden, "forbidden")),

        _ => throw new InvalidOperationException(
            $"HostStatusKind.{kind} has no representative, so the coverage below would skip it."),
    };

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

        // This exclusion is only worth anything while the headline is populated,
        // and for a long time nothing in this file said it was: a Headline
        // regressing to empty would have left this line passing over a blank
        // string while the claim around it went false. The control is
        // Every_status_kind_is_phrased below, which asserts every kind has a
        // headline and a detail and checks each representative really is of the
        // kind it stands for.
        //
        // The second control the broadcast asks for, a filter shown to still
        // fire on something it must match, does not have a subject here. "error"
        // is not a literal mirrored from the source that could drift out of
        // match: it is a word this type deliberately never emits, so there is no
        // status carrying it to point the same predicate at.
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

    /// <summary>
    /// Every kind is phrased, and the representative used to say so is of the
    /// kind it stands for.
    /// </summary>
    /// <remarks>
    /// This is the control the rest of the file was missing. Several assertions
    /// here are exclusions over <see cref="HostStatus.Headline"/> and
    /// <see cref="HostStatus.Detail"/>, and an exclusion over an empty string is
    /// green for a reason that has nothing to do with the claim it makes. The
    /// sibling type has had this from the start, in
    /// <c>TrafficFeedTests.Every_state_is_phrased_with_a_headline_and_a_detail</c>,
    /// and there was no equivalent here.
    ///
    /// The <c>Assert.Equal</c> is not decoration. Without it a representative
    /// could be wrong about its own kind and the loop would check the same
    /// status eight times rather than eight statuses once each, which is the
    /// failure mode a coverage test is supposed to prevent.
    ///
    /// What this does not claim: that the eight headlines differ from one
    /// another. Telling the states apart is the mapping in
    /// <c>HostStatus.FromFailure</c>, and that is covered by the named cases
    /// above rather than by a count.
    /// </remarks>
    [Fact]
    public void Every_status_kind_is_phrased()
    {
        foreach (var kind in Enum.GetValues<HostStatusKind>())
        {
            var status = Representative(kind);

            Assert.Equal(kind, status.Kind);
            Assert.False(string.IsNullOrWhiteSpace(status.Headline), $"{kind} has no headline");
            Assert.False(string.IsNullOrWhiteSpace(status.Detail), $"{kind} has no detail");
        }
    }
}

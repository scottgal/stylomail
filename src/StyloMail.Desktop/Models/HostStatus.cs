using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Models;

/// <summary>
/// Which state the console is in with respect to its Host.
/// </summary>
/// <remarks>
/// Six states rather than one "disconnected", because each has a different
/// remedy and the remedy is the only reason to show this at all: set a key,
/// correct a key, start the Host, point at the right address, upgrade the
/// console, or act on whatever the Host refused. Collapsing them would leave
/// the operator with a red dot and nothing to do about it.
/// </remarks>
public enum HostStatusKind
{
    /// <summary>Nothing has been asked yet.</summary>
    Unknown,

    /// <summary>The Host is up and can durably accept mail.</summary>
    Ready,

    /// <summary>The Host is up and is deliberately refusing mail.</summary>
    NotReady,

    /// <summary>This console holds no API key. Nothing has been sent.</summary>
    NeedsApiKey,

    /// <summary>The key was presented and refused.</summary>
    ApiKeyRejected,

    /// <summary>The Host could not be reached at all.</summary>
    Unreachable,

    /// <summary>The Host answered with something this build cannot read.</summary>
    VersionSkew,

    /// <summary>The Host refused for a reason that is not about the key.</summary>
    Refused,
}

/// <summary>
/// The Host's state, phrased for the person reading the sidebar.
/// </summary>
/// <remarks>
/// A plain value rather than a view model, so the mapping from an API answer to
/// what an operator is told can be tested without a display, a dispatcher or an
/// application instance. That mapping is the console's whole diagnostic surface
/// and it is worth more than the pixels it is rendered with.
/// </remarks>
public sealed record HostStatus
{
    private HostStatus(
        HostStatusKind kind,
        string headline,
        string detail,
        IReadOnlyList<string> failedChecks)
    {
        Kind = kind;
        Headline = headline;
        Detail = detail;
        FailedChecks = failedChecks;
    }

    public HostStatusKind Kind { get; }

    /// <summary>One short line: the state, said plainly.</summary>
    public string Headline { get; }

    /// <summary>The next thing to do about it, or the Host's own words.</summary>
    public string Detail { get; }

    /// <summary>
    /// Which capabilities failed, when the Host is up but not ready.
    /// </summary>
    /// <remarks>
    /// Carried as a list rather than folded into <see cref="Detail"/>, because
    /// the message list needs to show more than one and an operator scanning
    /// for a familiar failure should not have to parse a sentence.
    /// </remarks>
    public IReadOnlyList<string> FailedChecks { get; }

    public bool IsReachable => Kind is HostStatusKind.Ready or HostStatusKind.NotReady;

    public static HostStatus Unknown { get; } = new(
        HostStatusKind.Unknown,
        "Not checked yet",
        "The console has not asked this Host anything yet.",
        []);

    /// <summary>Builds the status from a readiness answer.</summary>
    public static HostStatus From(ReadinessResponse readiness)
        => readiness.Ready
            ? new HostStatus(
                HostStatusKind.Ready,
                "Connected",
                "The Host is running and can accept mail.",
                [])
            : new HostStatus(
                HostStatusKind.NotReady,
                "Connected, not accepting mail",
                "The Host is running but cannot durably accept mail right now.",
                readiness.FailedChecks ?? []);

    /// <summary>Builds the status from a failure, by the kind of failure it was.</summary>
    public static HostStatus FromFailure(StyloMailApiException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Failure switch
        {
            StyloMailApiFailure.ApiKeyNotConfigured => new HostStatus(
                HostStatusKind.NeedsApiKey,
                "No API key set",
                "Enter an API key to connect. It is stored in your keychain and never shown again.",
                []),

            // Separate from the missing-key case on purpose. Telling an
            // operator to enter a key they have already entered is the
            // specific unhelpfulness this branch exists to avoid.
            StyloMailApiFailure.Unreachable => new HostStatus(
                HostStatusKind.Unreachable,
                "Cannot reach the Host",
                "Nothing answered. Check the address, and that the Host is running.",
                []),

            StyloMailApiFailure.UnreadableResponse => new HostStatus(
                HostStatusKind.VersionSkew,
                "This console and the Host disagree",
                "The Host sent something this build cannot read. They are likely different versions.",
                []),

            _ when failure.Status == System.Net.HttpStatusCode.Unauthorized => new HostStatus(
                HostStatusKind.ApiKeyRejected,
                "API key refused",
                "The Host rejected the stored key. It may have been rotated or revoked.",
                []),

            // A refusal carrying neither the Host's error code nor its sentence
            // is not the Host refusing. It is something else answering at that
            // address: macOS runs AirPlay Receiver on port 5000, which is the
            // Host's own documented default, and it answers 403 with an HTML
            // body. Reading that as an authentication failure sends an operator
            // to look for a problem in a service that is not there.
            _ when failure.Code is null && failure.Detail is null => new HostStatus(
                HostStatusKind.Refused,
                "Something answered, but it is not a StyloMail Host",
                "That address replied without StyloMail's error format. Check the port: "
                    + "another service may be listening on it.",
                []),

            _ => new HostStatus(
                HostStatusKind.Refused,
                "The Host refused the request",
                failure.Code is null
                    ? failure.Detail ?? "No further detail was given."
                    : $"{failure.Code}: {failure.Detail}",
                []),
        };
    }
}

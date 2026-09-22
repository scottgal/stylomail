using System.Net;

namespace StyloMail.Desktop.Api;

/// <summary>
/// What kind of failure this is, which is the part an operator can act on.
/// </summary>
/// <remarks>
/// The three are deliberately distinct, because each has a different remedy and
/// collapsing them into one "call failed" would be the reason an operator sees
/// a generic error where the answer was a specific one.
/// </remarks>
public enum StyloMailApiFailure
{
    /// <summary>This console has no API key. Nothing was sent. The remedy is to configure one.</summary>
    ApiKeyNotConfigured,

    /// <summary>The Host answered, and refused. <see cref="StyloMailApiException.Code"/> says why.</summary>
    HostRefused,

    /// <summary>
    /// No answer at all: the transport failed.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="HostRefused"/> because the remedy is different
    /// and the two look identical if they are collapsed. A refusal means the
    /// Host is running and disagrees with this request; this means the Host was
    /// never reached, and the thing to check is the address, the port and
    /// whether the process is up.
    /// </remarks>
    Unreachable,

    /// <summary>
    /// The Host answered with a body this build cannot bind.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="HostRefused"/> because it is not a refusal and
    /// not a transient fault: the server sent something well-formed by its own
    /// lights that names a field or an enum member this client does not know.
    /// That is version skew between the console and the Host, and retrying it
    /// will not help.
    /// </remarks>
    UnreadableResponse,
}

/// <summary>
/// A call to the Host that did not produce a usable answer.
/// </summary>
/// <remarks>
/// <b>This exception never carries the API key.</b> The message is built from
/// the route and the Host's own error text, and nothing else. An exception that
/// described the credential would put it into every log, crash report and
/// screenshot that ever saw the message, which is the failure mode spec 10.3
/// exists to prevent.
/// </remarks>
public sealed class StyloMailApiException : Exception
{
    public StyloMailApiException(
        StyloMailApiFailure failure,
        string message,
        HttpStatusCode? status = null,
        string? code = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        Status = status;
        Code = code;
        Detail = detail;
    }

    public StyloMailApiFailure Failure { get; }

    /// <summary>The HTTP status, or null when no request was sent.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>
    /// The Host's machine-readable code, for example <c>decision_not_found</c>.
    /// </summary>
    /// <remarks>
    /// Machine-readable on purpose. The console branches on this and never on
    /// <see cref="Exception.Message"/>, which is prose the Host is free to
    /// reword.
    /// </remarks>
    public string? Code { get; }

    /// <summary>The Host's own sentence, shown to the operator as written.</summary>
    public string? Detail { get; }

    internal static StyloMailApiException KeyNotConfigured(string path) => new(
        StyloMailApiFailure.ApiKeyNotConfigured,
        $"No API key is configured, so the request to {path} was not sent.");

    internal static StyloMailApiException Refused(HttpStatusCode status, string? code, string? detail)
        => new(
            StyloMailApiFailure.HostRefused,
            $"The Host refused the request: {(int)status} {status}"
                + (code is null ? "." : $", {code}.")
                + (detail is null ? string.Empty : $" {detail}"),
            status,
            code,
            detail);

    internal static StyloMailApiException Unreachable(string path, Exception inner) => new(
        StyloMailApiFailure.Unreachable,
        $"The Host could not be reached at {path}. Check the address and that the host is running.",
        innerException: inner);

    internal static StyloMailApiException Unreadable(string path, Exception inner) => new(
        StyloMailApiFailure.UnreadableResponse,
        $"The Host's answer to {path} could not be read by this build. "
            + "The console and the Host are likely different versions.",
        innerException: inner);
}

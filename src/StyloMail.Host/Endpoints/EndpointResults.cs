namespace StyloMail.Host.Endpoints;

/// <summary>
/// Results shared by the endpoint modules.
/// </summary>
internal static class EndpointResults
{
    internal static Task<IResult> NotImplemented()
        => Task.FromResult<IResult>(Results.StatusCode(StatusCodes.Status501NotImplemented));

    /// <summary>
    /// A temporary failure that must never be mistaken for acceptance.
    /// </summary>
    /// <remarks>
    /// Used whenever durable storage cannot be written. Spec §10 is explicit: an SMTP <c>250</c>
    /// or an HTTP success transfers delivery responsibility, so answering success when the queue
    /// could not be committed destroys mail. This is the one place that translation happens.
    /// </remarks>
    internal static IResult StorageUnavailable(string detail)
        => Results.Json(
            new ErrorResponse("storage_unavailable", detail),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    internal static IResult Invalid(string code, string detail)
        => Results.Json(new ErrorResponse(code, detail), statusCode: StatusCodes.Status400BadRequest);

    internal static IResult Forbidden(string code, string detail)
        => Results.Json(new ErrorResponse(code, detail), statusCode: StatusCodes.Status403Forbidden);

    internal static IResult NotFound(string code, string detail)
        => Results.Json(new ErrorResponse(code, detail), statusCode: StatusCodes.Status404NotFound);

    internal static IResult Conflict(string code, string detail)
        => Results.Json(new ErrorResponse(code, detail), statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// A message that could not be read as a complete message.
    /// </summary>
    /// <remarks>
    /// <c>422</c> rather than <c>400</c>: the request was well formed, the message inside it was
    /// not usable. The disposition says which limit was hit, so a caller can distinguish "oversize"
    /// from "not a message at all" without parsing prose.
    /// </remarks>
    internal static IResult Unprocessable(
        string code,
        string detail,
        string disposition,
        string? reason = null,
        string? limitName = null)
        => Results.Json(
            new UnprocessableResponse(code, detail, disposition, reason, limitName),
            statusCode: StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// No assessor is configured. Refusing is the only safe answer.
    /// </summary>
    /// <remarks>
    /// An assessment is what decides whether mail is delivered. Fabricating a verdict would be
    /// worse than an outage: it would launder an unexamined message into an "Allow".
    /// </remarks>
    internal static IResult AssessorUnavailable()
        => Results.Json(
            new ErrorResponse(
                "assessor_unavailable",
                "No mail assessor is configured on this host, so no assessment can be produced."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>An error body. Carries a machine-readable code and a human sentence — never message content.</summary>
public sealed record ErrorResponse(string Error, string Detail);

/// <summary>An error body for a message that could not be analysed as a complete message.</summary>
public sealed record UnprocessableResponse(
    string Error,
    string Detail,
    string Disposition,
    string? Reason,
    string? LimitName);


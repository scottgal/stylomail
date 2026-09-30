using System.Net;

namespace StyloMail.Nimble;

/// <summary>
/// Raised when the local server rejects our request shape, or when the configured model is not
/// there to serve it.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are configuration and programming faults, not transient conditions, so they are raised
/// rather than converted into an unavailable state.</b> A model name that does not resolve, an
/// endpoint pointed at a server that does not serve the model, or a request body the server refuses
/// would otherwise make every message quietly lose its semantic evidence, and the symptom would look
/// like a calm inbox rather than a broken provider.
/// </para>
/// <para>
/// <b>This is not the same list as the hosted adapter's.</b> Jev raises for a rejected key, for a
/// rejected body, and for exceeding its own deadline. This adapter has no key to reject, and it
/// deliberately does <em>not</em> raise on a deadline: measured stalls here reach 103 s on a healthy
/// machine, so a timeout is a transient condition rather than a misconfiguration, and the port's
/// unavailable state is what a transient condition is for. See <see cref="NimbleSemanticMailClassifier"/>.
/// </para>
/// </remarks>
public sealed class NimbleContractException : Exception
{
    public NimbleContractException(string message, HttpStatusCode? statusCode = null, string? body = null)
        : base(message)
    {
        StatusCode = statusCode;

        // The response body is kept short and is never a request body. The request carries message
        // content, and an exception is exactly the kind of object that ends up in a log.
        ResponseExcerpt = body is null ? null : body[..Math.Min(body.Length, 200)];
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseExcerpt { get; }
}

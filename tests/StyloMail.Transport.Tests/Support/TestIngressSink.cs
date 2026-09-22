using StyloMail.Transport.Ingress;

namespace StyloMail.Transport.Tests.Support;

/// <summary>
/// A stand-in for the composition root's accept path.
/// </summary>
/// <remarks>
/// Injectable failure is the point: "durable storage was unavailable" is the one condition that must
/// never produce a <c>250</c>, and manufacturing a genuinely unwritable spool in a test is
/// environment-dependent — it depends on the user the suite runs as and on whether /tmp is full.
/// A sink that defers, or that throws, expresses the condition exactly.
/// </remarks>
internal sealed class TestIngressSink : ISmtpIngressSink
{
    private readonly Func<IngressSubmission, IngressDecision> _respond;

    public TestIngressSink(Func<IngressSubmission, IngressDecision>? respond = null) =>
        _respond = respond ?? (_ => IngressDecision.Accepted("q_test_1"));

    /// <summary>Every submission the listener handed over, in order.</summary>
    public List<IngressSubmission> Submissions { get; } = [];

    /// <summary>When set, throwing this instead of deciding — the sink failing in an unclassified way.</summary>
    public Exception? ThrowOnSubmit { get; set; }

    public ValueTask<IngressDecision> SubmitAsync(
        IngressSubmission submission,
        CancellationToken cancellationToken)
    {
        Submissions.Add(submission);

        if (ThrowOnSubmit is not null)
        {
            throw ThrowOnSubmit;
        }

        return ValueTask.FromResult(_respond(submission));
    }
}

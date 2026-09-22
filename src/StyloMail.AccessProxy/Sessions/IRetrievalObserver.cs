namespace StyloMail.AccessProxy.Sessions;

/// <summary>
/// An optional tap on the bytes flowing from the backend to the client.
/// </summary>
/// <remarks>
/// <b>Assessment on the retrieval path is optional, and this is how it stays optional.</b> The brief
/// for this component is explicit that a client fetching mail must not be held hostage by the
/// semantic provider, and that assuming the store-and-forward pipeline drops in unchanged would be
/// the wrong move.
///
/// <para>
/// It would be wrong because the pipeline assumes it owns a message: it takes a
/// <c>MailAnalysisInput</c>, runs parsing and policy, and is content with taking seconds to do it.
/// Here there is no message — there is a byte stream on its way to a client that is waiting for it.
/// A relay that awaited an assessment would turn every Jev timeout into a stalled mailbox, which is
/// precisely the coupling this seam exists to avoid.
/// </para>
///
/// <para>
/// So the tap is shaped so that coupling is impossible rather than merely discouraged:
/// <list type="bullet">
/// <item>
/// <b>It is synchronous.</b> There is no <c>Task</c> to await, so the relay structurally cannot
/// block on it. An implementation that needs I/O must enqueue a copy and return; holding work here
/// holds up a user's mail.
/// </item>
/// <item>
/// <b>It receives a span, not an array.</b> It cannot retain the buffer, so anything worth keeping
/// must be copied explicitly — which is the moment an implementation confronts how much message
/// content it is choosing to hold.
/// </item>
/// <item>
/// <b>It must not throw.</b> The relay treats a throwing observer as a protocol failure and the
/// session would drop. An observer that cannot keep up must drop data, not the session.
/// </item>
/// <item>
/// <b>It is absent by default.</b> With no observer configured the relay does no work at all — no
/// allocation, no copy, no call.
/// </item>
/// </list>
/// </para>
///
/// <para>
/// <b>What an implementation receives is the raw backend stream, not messages.</b> That is a
/// deliberate consequence of the byte pump: identifying which bytes belong to a
/// <c>FETCH ... BODY[]</c> response requires parsing IMAP, and parsing IMAP on the relay path is
/// exactly what guarantees we never rewrite a message. So an implementation that wants messages
/// must parse its own copy, off the critical path, and own the consequences — including the fact
/// that it is now making a privacy decision.
/// </para>
///
/// <para>
/// <b>That privacy decision is not this project's to make.</b> Spec §7 item 2 lists "permitted cloud
/// content and provider data-handling terms" as an open operator decision, and spec §8.3 rule 3 says
/// a connector supplies analysis input but never widens the assessment path. Until an operator
/// decides what may be sent where, the installed implementation is
/// <see cref="NullRetrievalObserver"/>: nothing is inspected, retained or transmitted.
/// </para>
/// </remarks>
public interface IRetrievalObserver
{
    /// <summary>
    /// Observes a chunk of backend-to-client bytes as they are relayed.
    /// </summary>
    /// <param name="bytes">
    /// The chunk. Valid only for the duration of the call — retaining it requires copying.
    /// </param>
    /// <remarks>
    /// Called on the relay path. Must not block, must not perform I/O, and must not throw.
    /// </remarks>
    void Observe(ReadOnlySpan<byte> bytes);
}

/// <summary>
/// The observer installed when no assessment is configured: it observes nothing.
/// </summary>
/// <remarks>
/// Present as a named type rather than as a null so the default is a decision someone can point at,
/// and so a deployment can tell "assessment is off" from "assessment was never wired".
/// </remarks>
public sealed class NullRetrievalObserver : IRetrievalObserver
{
    public static NullRetrievalObserver Instance { get; } = new();

    private NullRetrievalObserver()
    {
    }

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        // Deliberately empty. Nothing is inspected, copied, retained or transmitted.
    }
}

namespace StyloMail.Core;

/// <summary>
/// Shared rules about sender addresses — in particular the null sender.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-sourced deliberately, for the same reason as <see cref="PayloadReferences"/>.</b> This
/// predicate previously existed twice — in the queue and in assessment — mirrored by convention with
/// each copy citing the other. That is a divergence class with a silent failure: if the two ever
/// disagree, the assessor and the queue disagree about whether the *same message* is a DSN, with one
/// refusing before provider spend and the other after. One copy of it had already been wrong
/// (recognising <c>""</c> but not <c>"&lt;&gt;"</c>), so identical input was decided by notation.
/// </para>
/// <para>
/// The mitigation it replaces was "ping the other owner if this changes", which depends on whoever
/// edits one copy remembering at that moment that another exists. <b>That is exactly the condition
/// that fails</b>, and it fails quietly.
/// </para>
/// </remarks>
public static class SenderAddresses
{
    /// <summary>
    /// The value that travels between components for a null sender.
    /// </summary>
    /// <remarks>
    /// <b><see cref="MailEnvelope.MailFrom"/> holds the empty string for a null sender</b>, not the
    /// literal <c>"&lt;&gt;"</c>. It is an *address* field and the null sender is the empty address;
    /// <c>&lt;&gt;</c> is RFC 5321 wire notation and belongs at the parse boundary, normalised before
    /// the value enters the pipeline.
    /// </remarks>
    public const string NullSenderValue = "";

    /// <summary>RFC 5321's written form of the null sender, for tolerance at parse boundaries.</summary>
    public const string NullSenderWireForm = "<>";

    /// <summary>
    /// Whether this sender address is the null sender.
    /// </summary>
    /// <remarks>
    /// Tolerant of the travelling value (<c>""</c>), surrounding whitespace, and the wire form
    /// <c>"&lt;&gt;"</c> — a caller passing the un-normalised form must not be misread as having a
    /// real address, because the consequence of that misreading is treating a DSN as ordinary mail.
    /// </remarks>
    public static bool IsNullSender(string? address)
    {
        if (address is null)
        {
            return false;
        }

        var trimmed = address.AsSpan().Trim();
        return trimmed.IsEmpty || trimmed.SequenceEqual(NullSenderWireForm);
    }
}

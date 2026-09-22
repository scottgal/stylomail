namespace StyloMail.Core;

/// <summary>
/// Well-known schemes for <see cref="MailEnvelope.PayloadReference"/>, and the durability
/// invariant they encode.
/// </summary>
/// <remarks>
/// <see cref="MailEnvelope.PayloadReference"/> stays <b>required and non-nullable</b> deliberately.
/// Making it nullable would push a null-check onto every consumer and would not distinguish
/// "assessment-only, no payload was ever expected" from "a submission whose payload went missing" —
/// two situations with opposite urgency. A required reference that names its own scheme keeps the
/// two apart and lets the queue assert durability with a precise error.
///
/// <para>
/// <b>This is enforced where it matters, not by convention.</b> Only a <c>spool://</c> reference may
/// reach the delivery queue; passing an ephemeral one is a programming error that must surface at
/// acceptance rather than as mail that vanishes after a restart.
/// </para>
/// </remarks>
public static class PayloadReferences
{
    /// <summary>A durably spooled payload. The only scheme the delivery queue may accept.</summary>
    public const string SpoolScheme = "spool://";

    /// <summary>A non-durable reference for assessment-only calls, which by definition store nothing.</summary>
    public const string EphemeralScheme = "ephemeral://";

    /// <summary>
    /// The canonical non-durable reference. Assessment-only calls carry no payload and must not
    /// acquire one; a single shared value makes that explicit and greppable.
    /// </summary>
    public const string Ephemeral = "ephemeral://none";

    /// <summary>True when the reference names a durably stored payload.</summary>
    public static bool IsDurable(string? reference)
        => reference is not null
        && reference.StartsWith(SpoolScheme, StringComparison.Ordinal);

    /// <summary>
    /// Returns the reference if it is durable, otherwise throws.
    /// </summary>
    /// <remarks>
    /// Used on the acceptance path: an ephemeral reference reaching the queue means a caller wired
    /// the assessment path into the delivery path. Failing here refuses the message while refusal is
    /// still safe, instead of accepting mail we cannot later produce.
    /// </remarks>
    public static string RequireDurable(string? reference)
        => IsDurable(reference)
            ? reference!
            : throw new InvalidOperationException(
                $"Expected a durable '{SpoolScheme}' payload reference but got " +
                $"'{reference ?? "<null>"}'. Assessment-only inputs carry '{Ephemeral}' and must never " +
                "reach durable acceptance.");
}

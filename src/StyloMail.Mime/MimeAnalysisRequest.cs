using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>
/// Everything the MIME adapter is allowed to know, supplied by the caller.
/// </summary>
/// <remarks>
/// The raw bytes are supplied as a read-only view and are <b>never</b> modified: the analysis
/// view is derived from a copy, because rewriting the message would invalidate DKIM and break the
/// signature guarantees this component exists to uphold.
///
/// <para>
/// Envelope facts come in through <see cref="MailEnvelope"/> rather than being inferred from
/// headers. A message cannot establish its own identity: the header <c>From</c> is data, and the
/// only identity that counts is the one the transport boundary established.
/// </para>
/// </remarks>
public sealed record MimeAnalysisRequest
{
    public required MailEnvelope Envelope { get; init; }

    /// <summary>The original message bytes, exactly as received. Read only; never mutated.</summary>
    public required ReadOnlyMemory<byte> RawMessage { get; init; }

    /// <summary>
    /// Provenance from the trusted boundary. When absent, provenance is recorded as incomplete
    /// rather than assumed clean, and no header-supplied result is promoted to evidence.
    /// </summary>
    public AuthenticationContext? Authentication { get; init; }

    /// <summary>Bounded prior conversation, when the caller has it. Absence makes continuity NotApplicable.</summary>
    public IReadOnlyList<string>? ConversationContext { get; init; }

    /// <summary>Clock for evidence timestamps. Injected so replay is reproducible.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public MimeParseLimits Limits { get; init; } = MimeParseLimits.Default;
}

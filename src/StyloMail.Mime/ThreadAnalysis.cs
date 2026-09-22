using MimeKit;
using StyloMail.Core;
using static StyloMail.Mime.Attr;

namespace StyloMail.Mime;

/// <summary>What the threading headers claim, and whether those claims hold together.</summary>
internal sealed record ThreadAnalysis
{
    /// <summary>True when the message carries any threading header at all.</summary>
    public required bool HasThreadHeaders { get; init; }

    /// <summary>The sender's <c>Message-ID</c>, recorded as untrusted diagnostics only.</summary>
    public required string? UntrustedMessageId { get; init; }

    /// <summary>Number of places the thread headers disagree with each other or with the subject.</summary>
    public required int InconsistencyCount { get; init; }

    public required IReadOnlyList<EvidenceAttribute> Inconsistencies { get; init; }

    public required int ReferenceCount { get; init; }

    /// <summary>Subject claims to be a reply or forward, by prefix.</summary>
    public required bool SubjectClaimsReply { get; init; }
}

/// <summary>
/// Checks whether the thread headers are internally consistent.
/// </summary>
/// <remarks>
/// <b>Thread headers are claims, not proof.</b> Nothing here concludes that two messages belong
/// together; it only records whether the claims a message makes about its own ancestry hold
/// together. A message that says "this replies to X" while listing a reference chain that never
/// mentions X is reporting a fact about itself, and that fact is worth keeping.
///
/// <para>
/// The <c>Message-ID</c> is treated as untrusted throughout. It is compared against the references
/// the same message supplied, which is a self-consistency check — it is never used as a key, never
/// as identity and never as a deduplication guarantee.
/// </para>
/// </remarks>
internal static class ThreadInspector
{
    public static ThreadAnalysis Analyse(MimeMessage message)
    {
        var messageId = Trim(message.MessageId);
        var inReplyTo = Trim(message.InReplyTo);
        var references = ParseReferences(message.References);
        var subject = message.Subject ?? string.Empty;

        var inconsistencies = new List<EvidenceAttribute>();

        // A bare Message-ID is an identifier, not a claim about a relationship. Only In-Reply-To,
        // References or a threaded subject assert ancestry, and only a claim can be contradicted.
        var hasThreadHeaders = inReplyTo is not null || references.Count > 0;

        // A message's own id is deliberately not expected inside its own References chain — that
        // is normal and is not a signal. Only the relationships between the claims are checked.
        if (inReplyTo is not null && references.Count > 0 &&
            !references.Contains(inReplyTo, StringComparer.OrdinalIgnoreCase))
        {
            inconsistencies.Add(Of("in-reply-to-absent-from-references", "In-Reply-To names a parent the References chain does not list"));
        }

        if (inReplyTo is not null && references.Count > 0 &&
            !string.Equals(references[^1], inReplyTo, StringComparison.OrdinalIgnoreCase))
        {
            inconsistencies.Add(Of("in-reply-to-not-last-reference", "In-Reply-To is not the last entry of the References chain"));
        }

        if (references.Count > 1 && references.Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Count)
        {
            inconsistencies.Add(Of("references-contains-duplicates", "The References chain repeats an entry"));
        }

        var subjectClaimsReply = SubjectClaimsThread(subject);
        if (subjectClaimsReply && inReplyTo is null && references.Count == 0)
        {
            inconsistencies.Add(Of("subject-claims-thread-without-headers", "The subject is threaded but no In-Reply-To or References header is present"));
        }

        if ((inReplyTo is not null || references.Count > 0) && messageId is null)
        {
            inconsistencies.Add(Of("thread-headers-without-message-id", "Threading headers are present but the message has no Message-ID"));
        }

        var threadIndex = Trim(message.Headers["Thread-Index"]);
        if (threadIndex is not null && references.Count == 0 && inReplyTo is null)
        {
            inconsistencies.Add(Of("thread-index-without-references", "Thread-Index is present without In-Reply-To or References"));
        }

        return new ThreadAnalysis
        {
            HasThreadHeaders = hasThreadHeaders || subjectClaimsReply,
            UntrustedMessageId = messageId,
            InconsistencyCount = inconsistencies.Count,
            Inconsistencies = inconsistencies,
            ReferenceCount = references.Count,
            SubjectClaimsReply = subjectClaimsReply,
        };
    }

    private static bool SubjectClaimsThread(string subject)
    {
        var trimmed = subject.TrimStart();
        if (trimmed.Length < 3)
        {
            return false;
        }

        return trimmed.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("re[", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("fwd:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("fw:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("aw:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("sv:", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseReferences(MessageIdList? references)
    {
        var result = new List<string>();
        if (references is null || references.Count == 0)
        {
            return result;
        }

        foreach (var reference in references)
        {
            var trimmed = Trim(reference);
            if (trimmed is not null && result.Count < 64)
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var start = trimmed.IndexOf('<');
        var end = trimmed.LastIndexOf('>');
        if (start >= 0 && end > start)
        {
            trimmed = trimmed[start..(end + 1)];
        }

        return EvidenceBuilder.Truncate(trimmed, 256);
    }
}

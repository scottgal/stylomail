using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StyloMail.Core;

namespace StyloMail.Assessment.Semantic;

/// <summary>
/// Produces a canonical, unambiguous rendering of everything the semantic classifier is given.
/// </summary>
/// <remarks>
/// <b>The cache key is over the whole input, and this is what "the whole input" means.</b> The
/// projection below reproduces exactly what the provider receives, the message as the classifier
/// sees it, the dimension set it was asked about, and any tagged context that was folded in. Every
/// element is length-prefixed rather than delimiter-separated, because a body containing the
/// delimiter must not be able to impersonate a different structure: with plain joining, a body of
/// <c>a|b</c> and a body of <c>a</c> followed by another field <c>b</c> hash identically, and the
/// cache would then serve one message's assessment for another's.
///
/// <para>
/// <b>What is deliberately excluded, and why.</b> Envelope fields the classifier never sees, /// <c>InternalMessageId</c>, <c>ReceivedAt</c>, <c>PayloadReference</c>, <c>MimeDigest</c>, are
/// not part of the projection. Including them would not make the key safer; it would make every
/// message a unique key, which is a cache that never hits and a feature that does not exist. The
/// rule this follows is not "hash a convenient subset" but "hash precisely the classifier's
/// input": if a field cannot change the answer, it cannot change the key, and if it can, it is
/// here. Relationship context is included when it goes in, as the mission requires.
/// </para>
///
/// <para>
/// Ordering is canonical throughout. Link and attachment sequences keep their message order, /// reordering them is a different message and must key differently, while the tagged-context
/// dictionary is sorted by key, since dictionary enumeration order is an implementation detail
/// that would otherwise make the same facts produce two different keys.
/// </para>
/// </remarks>
public static class ClassifierInputCanonicalizer
{
    /// <summary>Stamp for the canonicalisation scheme itself. A change here invalidates the corpus.</summary>
    public const string EncodingVersion = "classifier-input-canonical/1";

    /// <summary>The canonical rendering of one classifier input.</summary>
    public static string Canonicalize(SemanticMailInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var canonical = new CanonicalForm();
        canonical.Field(EncodingVersion);

        var message = input.Message;
        var envelope = message.Envelope;

        // Tenant first: two tenants asking the identical question are not the same question, and
        // a shared entry across them would leak one tenant's traffic into another's ledger.
        canonical.Field(envelope.TenantId);
        canonical.Field(envelope.Direction.ToString());

        // Which questions were asked, in the order they were supplied. A different question set
        // is a different request even if the content is identical.
        canonical.Count(input.Dimensions.Count);
        foreach (var dimension in input.Dimensions)
        {
            canonical.Field(dimension.Id);
            canonical.Field(dimension.Instructions);
            canonical.Field(dimension.CriteriaTrue);
            canonical.Field(dimension.CriteriaFalse);
        }

        canonical.Field(message.Subject);
        canonical.Field(message.BodyText);
        canonical.Field(message.QuotedText);

        canonical.Count(message.Links.Count);
        foreach (var link in message.Links)
        {
            canonical.Field(link.DisplayedText);
            canonical.Field(link.ActualTarget);
            canonical.Field(link.UnicodeHost);
            canonical.Field(link.AsciiHost);
        }

        canonical.Count(message.Attachments.Count);
        foreach (var attachment in message.Attachments)
        {
            canonical.Field(attachment.FileName);
            canonical.Field(attachment.DeclaredContentType);
            canonical.Field(attachment.ExtensionImpliedContentType);
            canonical.Number(attachment.SizeBytes);
            canonical.Flag(attachment.SizeBytesIsComplete);
            canonical.Field(attachment.ContentHash);
            canonical.Flag(attachment.ContentUnavailable);
        }

        canonical.Count(message.ConversationContext?.Count ?? 0);
        foreach (var turn in message.ConversationContext ?? [])
        {
            canonical.Field(turn);
        }

        var coverage = message.Coverage;
        canonical.Flag(coverage.BodyParsed);
        canonical.Flag(coverage.HtmlPresent);
        canonical.Flag(coverage.HasAttachments);
        canonical.Flag(coverage.HtmlTextDisagreement);
        canonical.Flag(coverage.ParserLimitExceeded);
        canonical.Flag(coverage.OversizeRejected);
        canonical.Flag(coverage.ContentEncrypted);
        canonical.Flag(coverage.Truncated);
        canonical.Flag(coverage.ConversationContextMissing);

        // Envelope facts the classifier is actually shown. Only trusted-verifier results are
        // surfaced to it, so only those can change its answer, and therefore only those key.
        canonical.Field(envelope.MailFrom);
        canonical.Count(envelope.RcptTo.Count);
        foreach (var recipient in envelope.RcptTo)
        {
            canonical.Field(recipient);
        }

        canonical.Flag(message.Authentication.ProvenanceIncomplete);
        canonical.Field(message.Authentication.AuthenticatedAccount);
        canonical.Flag(message.Authentication.ConnectingIp is not null);

        var trusted = message.Authentication.Results
            .Where(result => result.FromTrustedVerifier)
            .ToList();
        canonical.Count(trusted.Count);
        foreach (var result in trusted)
        {
            canonical.Field(result.Mechanism);
            canonical.Field(result.Result);
            canonical.Field(result.VerifierId);
            canonical.Field(result.Detail);
        }

        // Behavioural context: it went into the input, so it is part of the key.
        //
        // This is the case the cache must never merge: two messages with identical content, one from
        // an account with months of history and one from an account created yesterday fanning out to
        // strangers. If this section were omitted the key would digest only the message, and the
        // second would be served the first's assessment — a judgement formed when the sender looked
        // ordinary, reused after their behaviour changed. That is the same rule as tagged context and
        // relationship context, applied to the field that carries the most risk of being forgotten.
        canonical.Flag(input.Profile is not null);

        if (input.Profile is { } profile)
        {
            EncodeProfile(canonical, profile);
        }

        // Tagged context, sorted by key: it went into the input, so it is part of the key.
        var tagged = input.TaggedContext ?? new Dictionary<string, string>();
        canonical.Count(tagged.Count);
        foreach (var (key, value) in tagged.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            canonical.Field(key);
            canonical.Field(value);
        }

        return canonical.ToString();
    }

    /// <summary>
    /// Encodes every field of the behavioural profile.
    /// </summary>
    /// <remarks>
    /// <b>Every field, and there is a tripwire test that fails when that stops being true.</b>
    /// ``BehaviouralProfile` is Core's and will grow; a field added there and not added here would be
    /// part of the classifier input but not part of the key, which is precisely the silent merging
    /// this method exists to prevent. Reflection over the record's properties is cheaper than trust.
    /// </remarks>
    internal static void EncodeProfile(CanonicalForm canonical, BehaviouralProfile profile)
    {
        canonical.Field(profile.Direction.ToString());
        canonical.Int(profile.FirstSeenDaysAgo);
        canonical.Int(profile.MessagesObserved);
        canonical.Int(profile.TrustedSamples);
        canonical.Field(profile.Regime);
        canonical.Int(profile.DistinctRecipientsLastHour);
        canonical.Int(profile.DistinctRecipientsLast30Days);
        // Whether that count is a floor rather than a measurement. A truncated count and an exact one
        // are different observations even when the number is the same, so they are different
        // questions and must not share a key — the flag is as load-bearing as the value.
        canonical.Flag(profile.RecipientDistinctnessIsFloor);
        canonical.Int(profile.RecipientsNovelToSender);
        canonical.Int(profile.MessagesLastHour);
        canonical.Int(profile.MessagesLast24Hours);
        canonical.Number(profile.BaselineMessagesPerHour);
        canonical.Int(profile.FanoutLastHour);
        canonical.Number(profile.BaselineFanoutPerHour);
        canonical.Field(profile.TrendNarrative);

        var movements = profile.Movements ?? [];
        canonical.Count(movements.Count);
        foreach (var movement in movements)
        {
            canonical.Field(movement.DimensionId);
            canonical.Field(movement.Direction);
            canonical.Number(movement.Magnitude);
        }

        canonical.Int(profile.DimensionsWithSupport);
        canonical.Flag(profile.ProfileAvailable);
        canonical.Flag(profile.ColdStart);
    }

    /// <summary>Lowercase hexadecimal SHA-256 over the canonical form.</summary>
    /// <remarks>
    /// A digest, not the canonical text: the text contains message content, and a cache key store
    /// is not a place message content belongs. The digest is also what makes the sampling decision
    /// reproducible without retaining anything.
    /// </remarks>
    public static string Digest(SemanticMailInput input)
    {
        var canonical = Canonicalize(input);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// A length-prefixed field writer.
    /// </summary>
    /// <remarks>
    /// Null and empty encode differently on purpose. "No subject" and "an empty subject" are
    /// different inputs and the classifier can answer them differently; a scheme that collapsed
    /// them would serve one's cached assessment for the other.
    /// </remarks>
    internal sealed class CanonicalForm
    {
        private readonly StringBuilder _builder = new();

        public void Field(string? value)
        {
            if (value is null)
            {
                _builder.Append("-;");
                return;
            }

            _builder.Append('+').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':');
            _builder.Append(value).Append(';');
        }

        public void Count(int count) => _builder.Append('#').Append(count.ToString(CultureInfo.InvariantCulture)).Append(';');

        public void Flag(bool value) => _builder.Append(value ? '1' : '0').Append(';');

        public void Number(long value)
            => _builder.Append('n').Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');

        /// <summary>Null and zero must differ: "not observed" is not "observed as zero".</summary>
        public void Int(int? value)
        {
            if (value is null)
            {
                _builder.Append("i-;");
                return;
            }

            _builder.Append('i').Append(value.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        public void Number(double? value)
        {
            if (value is null)
            {
                _builder.Append("d-;");
                return;
            }

            _builder.Append('d').Append(value.Value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }

        public override string ToString() => _builder.ToString();
    }
}

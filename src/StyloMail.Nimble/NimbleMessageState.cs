using StyloMail.Core;

namespace StyloMail.Nimble;

/// <summary>
/// The bounded, structured description of one message that this provider is asked to judge.
/// </summary>
/// <remarks>
/// <para>
/// <b>This deliberately mirrors the hosted adapter's state, field for field.</b> The two providers are
/// compared on the same corpus, and a comparison between two adapters that describe a message
/// differently would be a comparison of the descriptions. Keeping the field names, the nesting and the
/// order identical is what makes one corpus answer two questions.
/// </para>
/// <para>
/// Field order is load-bearing in a second way: the state is serialised straight into the prompt and
/// hashed into the cache key, and both must be stable for a given input.
/// </para>
/// <para>
/// <b>It is a copy, and copies drift.</b> The drift is not left to discipline: a test in
/// <c>StyloMail.Nimble.Tests</c> drives both classifiers through a stub transport and asserts that the
/// state each one actually sends is equal for the same input. If the hosted state gains or renames a
/// field, that test goes red and names this file.
/// </para>
/// <para>
/// <b>The one deliberate difference is a pair of keys that appear only when the body had to be
/// shortened to fit the context window.</b> They are absent otherwise, so the "identical unless we
/// shortened something" claim is exactly true and exactly testable.
/// </para>
/// </remarks>
internal static class NimbleMessageState
{
    /// <summary>The state, and whether feeding it into the prompt cost part of the message body.</summary>
    /// <remarks>
    /// The flag is returned rather than re-derived by comparing string lengths at the call site, so
    /// the knowledge lives with the code that made the decision.
    /// </remarks>
    internal sealed record Built(Dictionary<string, object?> State, bool BodyShortened);

    /// <summary>
    /// Builds the state, capping each long text field at <paramref name="bodyCharacterBudget"/>.
    /// </summary>
    /// <remarks>
    /// <b>Truncation here is not silent.</b> The Jev/Laya finding that opened this lane was a context
    /// limit that cut a prompt without saying so, and the survey reproduced a version of it on this
    /// model: a 14,551 token prompt at the default window was evaluated as 4,099 tokens, ten thousand
    /// tokens were dropped, <c>done_reason</c> said "stop" and nothing in the response reported a
    /// problem. Omitting a tail and telling the model it is missing is a lesser failure than omitting
    /// it and letting the answer read as complete.
    /// </remarks>
    internal static Built Build(
        MailAnalysisInput message,
        IReadOnlyDictionary<string, string>? taggedContext,
        BehaviouralProfile? profile,
        int bodyCharacterBudget,
        int maxLinks,
        int maxAttachments)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegative(bodyCharacterBudget);

        var body = Truncate(message.BodyText, bodyCharacterBudget);
        var quoted = Truncate(message.QuotedText, bodyCharacterBudget);
        var shortened = Shortened(message.BodyText, body) || Shortened(message.QuotedText, quoted);

        var messageState = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["subject"] = message.Subject,
            ["body_text"] = body,
            ["quoted_text"] = quoted,

            // Bounded, like the hosted adapter's. A message with a thousand links must not be able to
            // decide how much of the context window it gets, and the bound is the same number there so
            // the two providers are reading the same message.
            ["links"] = message.Links.Take(maxLinks).Select(l => new Dictionary<string, object?>
            {
                ["displayed_text"] = l.DisplayedText,
                ["actual_target"] = l.ActualTarget,
                ["unicode_host"] = l.UnicodeHost,
                ["ascii_host"] = l.AsciiHost,
            }).ToList(),
            ["attachments"] = message.Attachments.Take(maxAttachments).Select(a => new Dictionary<string, object?>
            {
                ["file_name"] = a.FileName,
                ["declared_content_type"] = a.DeclaredContentType,
                ["extension_implied_content_type"] = a.ExtensionImpliedContentType,
                ["size_bytes"] = a.SizeBytes,
                ["content_available"] = !a.ContentUnavailable,
            }).ToList(),
        };

        if (shortened)
        {
            // Present only when the body was cut. A reader of the request (and the model reading it)
            // can then tell a short message from the front of a long one.
            messageState["body_text_shortened_for_prompt"] = true;
            messageState["body_text_characters_kept"] = body?.Length ?? 0;
        }

        var envelope = message.Envelope;

        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = messageState,
            ["envelope"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["direction"] = envelope.Direction.ToString(),
                ["mail_from"] = envelope.MailFrom,
                ["recipient_count"] = envelope.RcptTo.Count,
                ["recipient_domains"] = envelope.RcptTo
                    .Select(DomainOf)
                    .Where(d => d is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            },

            // Behavioural context, under its own key and labelled as observation rather than content.
            // Without it every message is judged in isolation, and the same words from an established
            // correspondent and from a day-old account fanning out to strangers read identically.
            ["sender_behaviour"] = BuildBehaviourProfile(profile),

            ["coverage"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["body_parsed"] = message.Coverage.BodyParsed,
                ["html_present"] = message.Coverage.HtmlPresent,
                ["html_text_disagreement"] = message.Coverage.HtmlTextDisagreement,
                ["content_encrypted"] = message.Coverage.ContentEncrypted,
                ["truncated"] = message.Coverage.Truncated,
                ["parser_limit_exceeded"] = message.Coverage.ParserLimitExceeded,
            },
        };

        // Only trusted-verifier results are surfaced. A result asserted by the message itself would
        // let a sender certify its own authenticity.
        var trustedAuth = message.Authentication.Results
            .Where(r => r.FromTrustedVerifier)
            .Select(r => new Dictionary<string, object?>
            {
                ["mechanism"] = r.Mechanism,
                ["result"] = r.Result,
                ["verifier"] = r.VerifierId,
            })
            .ToList();

        state["authentication"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["provenance_incomplete"] = message.Authentication.ProvenanceIncomplete,
            ["results"] = trustedAuth,
        };

        if (message.ConversationContext is { Count: > 0 })
        {
            state["conversation_context"] = message.ConversationContext
                .Take(10)
                .Select(m => Truncate(m, 2_000))
                .ToList();
        }

        if (taggedContext is { Count: > 0 })
        {
            state["context"] = taggedContext;
        }

        return new Built(state, shortened);
    }

    /// <summary>
    /// Encodes the sender's behaviour for the classifier's state.
    /// </summary>
    /// <remarks>
    /// <b>Absence is stated, not omitted.</b> With no profile this emits an explicit "not available"
    /// object rather than nothing, so the model is told that behavioural context is missing instead of
    /// silently receiving a message-only view. A model that does not know it is uninformed answers as
    /// confidently as one that is.
    /// </remarks>
    private static object BuildBehaviourProfile(BehaviouralProfile? profile)
    {
        if (profile is null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["available"] = false,
                ["note"] = "No behavioural context was available for this sender. This message is "
                    + "being judged on its own content only.",
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["available"] = profile.ProfileAvailable,
            ["cold_start"] = profile.ColdStart,
            ["direction"] = profile.Direction.ToString(),
            ["known_for_days"] = profile.FirstSeenDaysAgo,
            ["messages_observed"] = profile.MessagesObserved,
            ["trusted_samples"] = profile.TrustedSamples,
            ["regime"] = profile.Regime,
            ["distinct_recipients_last_hour"] = profile.DistinctRecipientsLastHour,
            ["distinct_recipients_last_30_days"] = profile.DistinctRecipientsLast30Days,
            ["recipients_novel_to_sender"] = profile.RecipientsNovelToSender,
            ["messages_last_hour"] = profile.MessagesLastHour,
            ["messages_last_24_hours"] = profile.MessagesLast24Hours,
            ["baseline_messages_per_hour"] = profile.BaselineMessagesPerHour,
            ["fanout_last_hour"] = profile.FanoutLastHour,
            ["baseline_fanout_per_hour"] = profile.BaselineFanoutPerHour,
            ["trend"] = profile.TrendNarrative,
            ["movements"] = profile.Movements?
                .Take(BehaviouralProfile.MaxMovements)
                .Select(m => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["dimension"] = m.DimensionId,
                    ["direction"] = m.Direction,
                    ["magnitude"] = m.Magnitude,
                })
                .ToList(),
            ["dimensions_with_support"] = profile.DimensionsWithSupport,
        };
    }

    private static bool Shortened(string? original, string? truncated)
        => original is not null && truncated is not null && truncated.Length < original.Length;

    private static string? DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : null;
    }

    private static string? Truncate(string? value, int maxCharacters)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maxCharacters ? value : value[..maxCharacters];
    }
}

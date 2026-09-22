using System.Security.Cryptography;
using System.Text;
using MimeKit;
using StyloMail.Core;
using static StyloMail.Mime.Attr;

namespace StyloMail.Mime;

/// <summary>
/// The MIME adapter: bounded parsing plus deterministic feature extraction.
/// </summary>
/// <remarks>
/// <b>What this class will not do.</b> It does not fetch a link, load a remote image, execute an
/// attachment or resolve a redirect, there is no code path here that opens a socket, and that is
/// a property of the design rather than a configuration flag someone can flip. It does not modify
/// the message bytes it is given: the analysis view is built alongside them, because rewriting a
/// signed message invalidates its signature and a proxy that breaks DKIM has broken the guarantee
/// it was deployed to uphold.
///
/// <para>
/// <b>What it does when it cannot do the job.</b> A message over a size or structural limit comes
/// back as a rejection with an explicit disposition and a coverage record. It is never partially
/// analysed and then presented as though the readable part were the whole message.
/// </para>
///
/// <para>
/// <b>The limits are not advisory.</b> A hostile message is not obliged to be small, shallow or
/// well formed, so size, part count, nesting depth, header count and header length are all checked
/// before real work is committed to them.
/// </para>
/// </remarks>
public sealed class BoundedMimeMessageAnalyzer : IMimeMessageAnalyzer
{
    /// <summary>
    /// How much of the shorter representation must be covered by the longer before HTML and text
    /// are considered to agree. Below this the disagreement is reported as evidence.
    /// </summary>
    private const double HtmlTextAgreementThreshold = 0.80;

    /// <summary>Minimum token count before an HTML/text comparison is meaningful at all.</summary>
    private const int MinTokensForComparison = 20;

    private static readonly AnalysisCoverage EmptyCoverage = new()
    {
        BodyParsed = false,
        HtmlPresent = false,
        HasAttachments = false,
        HtmlTextDisagreement = false,
        ParserLimitExceeded = false,
        ContentEncrypted = false,
        Truncated = false,
        ConversationContextMissing = true,
    };

    public MimeAnalysisResult Analyze(MimeAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var limits = request.Limits ?? MimeParseLimits.Default;
        var observedAt = request.TimeProvider.GetUtcNow();
        var builder = new EvidenceBuilder(observedAt, limits);
        var raw = request.RawMessage.Span;

        if (raw.Length > limits.MaxMessageBytes)
        {
            return Reject(
                request,
                builder,
                new MimeParseRejection
                {
                    Disposition = MimeParseDisposition.Oversize,
                    Reason = "message-bytes",
                    LimitName = "MaxMessageBytes",
                    Observed = raw.Length,
                    Limit = limits.MaxMessageBytes,
                },
                coverage: EmptyCoverage with { ParserLimitExceeded = true, OversizeRejected = true });
        }

        var preflight = RawMessagePreflight.Scan(raw, limits);
        if (preflight.Rejection is not null)
        {
            return Reject(request, builder, preflight.Rejection, coverage: EmptyCoverage with
            {
                ParserLimitExceeded = preflight.Rejection.Disposition == MimeParseDisposition.LimitExceeded,
            });
        }

        MimeMessage message;
        try
        {
            using var stream = new MemoryStream(request.RawMessage.ToArray(), writable: false);
            var options = new ParserOptions { MaxMimeDepth = limits.MaxMimeDepth };
            message = MimeMessage.Load(options, stream, CancellationToken.None);
        }
        catch (Exception ex) when (ex is FormatException or IOException or ArgumentException or InvalidOperationException)
        {
            return Reject(
                request,
                builder,
                new MimeParseRejection
                {
                    Disposition = MimeParseDisposition.Malformed,
                    Reason = "unparseable-message",
                    Observed = raw.Length,
                },
                coverage: EmptyCoverage);
        }

        var collected = PartCollector.Collect(message, limits);

        if (collected.LimitBreach is not null)
        {
            return Reject(
                request,
                builder,
                new MimeParseRejection
                {
                    Disposition = MimeParseDisposition.LimitExceeded,

                    // Distinct from the preflight's reason on purpose. Reaching here means the
                    // structural scan let the message through and the walk caught it afterwards,
                    // so the work had already been done. That is a different incident from an
                    // early refusal, and the ledger should not make them look alike.
                    Reason = collected.LimitBreach + "-after-parse",
                    LimitName = collected.LimitBreach == "part-count" ? "MaxParts" : "MaxMimeDepth",
                    Observed = collected.PartCount,
                    Limit = collected.LimitBreach == "part-count" ? limits.MaxParts : limits.MaxMimeDepth,
                },
                coverage: EmptyCoverage with { ParserLimitExceeded = true });
        }

        return Build(request, builder, limits, message, collected, preflight);
    }

    private static MimeAnalysisResult Build(
        MimeAnalysisRequest request,
        EvidenceBuilder builder,
        MimeParseLimits limits,
        MimeMessage message,
        CollectedContent collected,
        PreflightResult preflight)
    {
        var evidence = new List<Evidence>();

        var fullPlain = string.Join("\n", collected.PlainBodies);
        var fullHtml = string.Join("\n", collected.HtmlBodies);
        var html = HtmlExtraction.Analyze(fullHtml);

        var effectivePlain = fullPlain.Length > 0 ? fullPlain : html.VisibleText;
        var quoted = QuotedHistory.Split(effectivePlain);

        var identity = IdentityInspector.Analyse(message, request.Envelope);
        var thread = ThreadInspector.Analyse(message);
        var links = LinkExtractor.Extract(html, fullPlain, limits);
        var obfuscation = PaddingObfuscationScanner.Scan(effectivePlain, html.VisibleText, html);

        var truncated = collected.BodyTruncated || preflight.BoundaryUnterminated;

        // --- size and structure -------------------------------------------------------------
        evidence.Add(builder.Build(
            MimeSignals.MessageSize,
            EvidenceAvailability.Available,
            request.RawMessage.Length,
            attributes:
            [
                Of("headerBytes", preflight.HeaderBytes.ToString()),
                Of("bodyBytes", Math.Max(0, request.RawMessage.Length - preflight.HeaderBlockEnd).ToString()),
                Of("attachmentBytes", collected.Attachments.Sum(a => a.Metadata.SizeBytes).ToString()),
            ]));

        evidence.Add(builder.Build(
            MimeSignals.MessageStructure,
            EvidenceAvailability.Available,
            collected.PartCount,
            attributes:
            [
                Of("partCount", collected.PartCount.ToString()),
                Of("maxDepth", collected.MaxDepth.ToString()),
                Of("plainPartCount", collected.PlainBodies.Count.ToString()),
                Of("htmlPartCount", collected.HtmlBodies.Count.ToString()),
                Of("attachmentCount", collected.Attachments.Count.ToString()),
                Of("signaturePartCount", collected.SignaturePartCount.ToString()),
                Of("linkCount", links.Count.ToString()),
            ]));

        evidence.Add(builder.Build(
            MimeSignals.RecipientCount,
            EvidenceAvailability.Available,
            request.Envelope.RcptTo.Count,
            attributes:
            [
                Of("distinctRecipientDomains",
                    request.Envelope.RcptTo.Select(DomainOf).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString()),
            ]));

        // --- identity -----------------------------------------------------------------------
        evidence.Add(builder.Build(
            MimeSignals.EnvelopeHeaderIdentity,
            EvidenceAvailability.Available,
            identity.EnvelopeMismatches.Count,
            attributes: identity.EnvelopeMismatches));

        evidence.Add(builder.Build(
            MimeSignals.DisplayNameAddressMismatch,
            HasAnyDisplayName(identity) ? EvidenceAvailability.Available : EvidenceAvailability.NotApplicable,
            identity.DisplayNameMismatches.Count,
            attributes: identity.DisplayNameMismatches));

        var hasReplyTo = identity.ReplyTo.Count > 0;
        evidence.Add(builder.Build(
            MimeSignals.ReplyToDivergence,
            hasReplyTo ? EvidenceAvailability.Available : EvidenceAvailability.NotApplicable,
            hasReplyTo ? identity.ReplyToDiverges ? 1.0 : 0.0 : null,
            attributes: hasReplyTo
                ?
                [
                    Of("fromDomain", identity.From.Count > 0 ? identity.From[0].Domain : string.Empty),
                    Of("replyToDomain", identity.ReplyTo[0].Domain),
                    Of("sameAddressFamily", (!identity.ReplyToDiverges).ToString()),
                ]
                : null));

        // --- authentication -----------------------------------------------------------------
        var auth = BuildAuthenticationEvidence(request.Authentication, builder);
        evidence.AddRange(auth);

        // --- links --------------------------------------------------------------------------
        var mismatched = links.Where(l => l.DisplayMismatch).ToList();
        var labelled = links.Where(l => l.LabelMakesHostClaim).ToList();
        evidence.Add(builder.Build(
            MimeSignals.LinkDisplayMismatch,
            links.Count == 0 ? EvidenceAvailability.NotApplicable : EvidenceAvailability.Available,
            labelled.Count == 0 ? null : (double)mismatched.Count / labelled.Count,
            attributes:
            [
                Of("linkCount", links.Count.ToString()),
                Of("labelledLinkCount", labelled.Count.ToString()),
                Of("mismatchCount", mismatched.Count.ToString()),
                .. mismatched.Take(5).Select(l => Of(
                    l.MismatchKind ?? "mismatch",
                    $"{EvidenceBuilder.Truncate(l.DisplayedText, 64)} -> {EvidenceBuilder.Truncate(l.ActualTarget, 96)}")),
            ]));

        var idnLinks = links.Where(l => l.Idn is not null).ToList();
        evidence.Add(builder.Build(
            MimeSignals.LinkIdn,
            idnLinks.Count == 0 ? EvidenceAvailability.NotApplicable : EvidenceAvailability.Available,
            idnLinks.Count,
            attributes: [.. idnLinks.Take(5).Select(l => Of(
                "idn",
                $"{l.Idn!.UnicodeHost} ({l.Idn.AsciiHost})"))]));

        var homographs = idnLinks.Where(l => l.Idn!.Confusables.Count > 0 || l.Idn.IsMixedScript).ToList();
        evidence.Add(builder.Build(
            MimeSignals.LinkIdnHomograph,
            idnLinks.Count == 0 ? EvidenceAvailability.NotApplicable : EvidenceAvailability.Available,
            homographs.Count,
            attributes: [.. homographs.Take(5).Select(l => Of(
                "homograph",
                $"{l.Idn!.UnicodeHost} looks like {l.Idn.AsciiSkeleton} " +
                $"[scripts={string.Join(",", l.Idn.Scripts)} mixed={l.Idn.IsMixedScript} " +
                $"confusables={string.Join("/", l.Idn.Confusables.Take(4))}]"))]));

        evidence.Add(BuildLinkHostProfile(links, builder));

        // --- attachments --------------------------------------------------------------------
        evidence.AddRange(BuildAttachmentEvidence(collected, builder, limits));

        // --- content comparison -------------------------------------------------------------
        var plainTokens = TextTools.TokenSet(quoted.NewText.Length > 0 ? quoted.NewText : effectivePlain);
        var htmlTokens = TextTools.TokenSet(html.VisibleText);
        var comparable = collected.PlainBodies.Count > 0 &&
                         collected.HtmlBodies.Count > 0 &&
                         htmlTokens.Count >= MinTokensForComparison &&
                         plainTokens.Count >= MinTokensForComparison;
        var containment = comparable ? TextTools.Containment(plainTokens, htmlTokens) : 1.0;
        var disagreement = comparable && containment < HtmlTextAgreementThreshold;

        evidence.Add(builder.Build(
            MimeSignals.HtmlTextDisagreement,
            comparable ? EvidenceAvailability.Available : EvidenceAvailability.NotApplicable,
            comparable ? 1.0 - containment : null,
            attributes:
            [
                Of("htmlTokens", htmlTokens.Count.ToString()),
                Of("plainTokens", plainTokens.Count.ToString()),
                Of("sharedTokens", comparable ? TextTools.SharedTokenCount(plainTokens, htmlTokens).ToString() : "0"),
                Of("htmlOnlyTokens", comparable ? (htmlTokens.Count - TextTools.SharedTokenCount(plainTokens, htmlTokens)).ToString() : "0"),
            ]));

        evidence.Add(builder.Build(
            MimeSignals.PaddingObfuscation,
            EvidenceAvailability.Available,
            obfuscation.IndicatorCount,
            attributes: obfuscation.Indicators));

        var hasMarkupObservation = html.FormActions.Count > 0 ||
                                  html.PasswordInputCount > 0 ||
                                  html.RemoteImageCount > 0 ||
                                  html.TrackingPixelCount > 0;

        evidence.Add(builder.Build(
            MimeSignals.HtmlMarkupObservation,
            hasMarkupObservation ? EvidenceAvailability.Available : EvidenceAvailability.NotApplicable,
            html.FormActions.Count,
            attributes:
            [
                Of("formCount", html.FormActions.Count.ToString()),
                Of("passwordInputCount", html.PasswordInputCount.ToString()),
                Of("externalImageCount", html.RemoteImageCount.ToString()),
                .. html.FormActions.Take(3).Select(a => Of("action", EvidenceBuilder.Truncate(a, 96))),
            ]));

        // --- threading ----------------------------------------------------------------------
        evidence.Add(builder.Build(
            MimeSignals.ThreadHeaderConsistency,
            thread.HasThreadHeaders ? EvidenceAvailability.Available : EvidenceAvailability.NotApplicable,
            thread.InconsistencyCount,
            attributes:
            [
                Of("referenceCount", thread.ReferenceCount.ToString()),
                Of("subjectClaimsReply", thread.SubjectClaimsReply.ToString()),
                .. thread.Inconsistencies,
            ]));

        // --- text shape ---------------------------------------------------------------------
        var totalTokens = TextTools.Tokenize(effectivePlain).Count;
        var newTokens = TextTools.Tokenize(quoted.NewText).Count;
        evidence.Add(builder.Build(
            MimeSignals.QuotedHistory,
            EvidenceAvailability.Available,
            totalTokens == 0 ? 0.0 : 1.0 - ((double)newTokens / totalTokens),
            attributes:
            [
                Of("marker", quoted.Marker),
                Of("newTokens", newTokens.ToString()),
                Of("totalTokens", totalTokens.ToString()),
            ]));

        evidence.Add(BuildTemplateFingerprint(quoted, totalTokens, newTokens, links.Count, builder));

        // --- encryption ---------------------------------------------------------------------
        evidence.Add(builder.Build(
            MimeSignals.ContentEncrypted,
            EvidenceAvailability.Available,
            collected.HasEncryptedContainer ? 1.0 : 0.0,
            attributes:
            [
                Of("encryptedContainer", collected.HasEncryptedContainer.ToString()),
                Of("unparseableContent", collected.UnparseableContent.ToString()),
            ]));

        var coverage = new AnalysisCoverage
        {
            BodyParsed = collected.BodyParsed,
            HtmlPresent = collected.HtmlBodies.Count > 0,
            HasAttachments = collected.Attachments.Count > 0,
            HtmlTextDisagreement = disagreement,
            ParserLimitExceeded = false,
            ContentEncrypted = collected.HasEncryptedContainer,
            Truncated = truncated,
            ConversationContextMissing = request.ConversationContext is null || request.ConversationContext.Count == 0,
        };

        evidence.Add(BuildCoverageEvidence(coverage, request.Authentication, builder));

        var envelope = request.Envelope with
        {
            UntrustedMessageIdHeader = request.Envelope.UntrustedMessageIdHeader ?? thread.UntrustedMessageId,
        };

        var input = new MailAnalysisInput
        {
            Envelope = envelope,
            Authentication = request.Authentication ?? IncompleteProvenance(),
            Channel = ChannelContext.Email,
            Subject = message.Subject,
            BodyText = quoted.NewText.Length > 0 ? quoted.NewText : effectivePlain,
            QuotedText = quoted.QuotedText.Length > 0 ? quoted.QuotedText : null,
            Links = [.. links.Select(l => new LinkObservation
            {
                DisplayedText = l.DisplayedText,
                ActualTarget = l.ActualTarget,
                UnicodeHost = l.Idn?.UnicodeHost ?? l.Target?.UnicodeHost,
                AsciiHost = l.Idn?.AsciiHost ?? l.Target?.AsciiHost,
            })],
            Attachments = [.. collected.Attachments.Select(a => a.Metadata)],
            ConversationContext = request.ConversationContext,
            Coverage = coverage,
        };

        return new MimeAnalysisResult
        {
            Disposition = MimeParseDisposition.Parsed,
            Message = input,
            Evidence = evidence,
            Coverage = coverage,
        };
    }

    private static IReadOnlyList<Evidence> BuildAuthenticationEvidence(
        AuthenticationContext? authentication,
        EvidenceBuilder builder)
    {
        var results = authentication?.Results ?? [];
        var trusted = results.Where(r => r.FromTrustedVerifier).ToList();

        var failing = new[] { "fail", "softfail", "permerror", "hardfail", "temperror", "policy" };
        var trustedFailures = trusted
            .Where(r => failing.Contains(r.Result.Trim().ToLowerInvariant()))
            .ToList();

        var failureEvidence = builder.Build(
            MimeSignals.TrustedAuthenticationFailure,
            results.Count == 0
                ? EvidenceAvailability.NotApplicable
                : trusted.Count == 0
                    ? EvidenceAvailability.ReducedCoverage
                    : EvidenceAvailability.Available,
            trusted.Count == 0 ? null : trustedFailures.Count,
            attributes:
            [
                .. trusted.Take(8).Select(r => Of(
                    "trusted." + r.Mechanism.ToLowerInvariant(), r.Result.ToLowerInvariant())),
                .. results.Where(r => !r.FromTrustedVerifier).Take(8).Select(r => Of(
                    "untrusted." + r.Mechanism.ToLowerInvariant(),
                    r.Result.ToLowerInvariant() + " (not from a trusted verifier)")),
            ]);

        // A message cannot assert its own authentication. When no trusted verifier reported
        // anything, that absence is recorded as incomplete provenance, never as clean.
        var provenanceIncomplete = authentication is null ||
                                   authentication.ProvenanceIncomplete ||
                                   trusted.Count == 0;

        var provenanceEvidence = builder.Build(
            MimeSignals.AuthenticationProvenance,
            EvidenceAvailability.Available,
            provenanceIncomplete ? 1.0 : 0.0,
            attributes:
            [
                Of("provenanceIncomplete", provenanceIncomplete.ToString()),
                Of("trustedVerifierResultCount", trusted.Count.ToString()),
                Of("untrustedResultCount", (results.Count - trusted.Count).ToString()),
                Of("connectingIpPresent", (authentication?.ConnectingIp is not null).ToString()),
                Of("authenticatedAccountPresent", (authentication?.AuthenticatedAccount is not null).ToString()),
                Of("approvedSenderIdentityCount", (authentication?.ApprovedSenderIdentities.Count ?? 0).ToString()),
            ]);

        return [failureEvidence, provenanceEvidence];
    }

    private static Evidence BuildLinkHostProfile(IReadOnlyList<LinkFinding> links, EvidenceBuilder builder)
    {
        if (links.Count == 0)
        {
            return builder.Build(
                MimeSignals.LinkHostProfile,
                EvidenceAvailability.NotApplicable,
                null);
        }

        var hosts = links
            .Select(l => l.Target?.Host)
            .Where(h => !string.IsNullOrEmpty(h))
            .Select(h => h!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return builder.Build(
            MimeSignals.LinkHostProfile,
            EvidenceAvailability.Available,
            hosts.Count,
            attributes:
            [
                // The digest lets the adaptive engine compare this message's host set with a
                // recipient's baseline. Novelty itself is not decided here, it cannot be.
                Of("hostSetDigest", Digest(hosts)),
                Of("distinctHostCount", hosts.Count.ToString()),
                Of("ipLiteralCount", links.Count(l => l.Target?.IsIpLiteral == true).ToString()),
                Of("userInfoCount", links.Count(l => l.Target?.HasUserInfo == true).ToString()),
                Of("explicitPortCount", links.Count(l => l.Target?.HasExplicitPort == true).ToString()),
                Of("plaintextHttpCount", links.Count(l => l.Target?.IsPlaintext == true).ToString()),
                Of("punycodeHostCount", links.Count(l => l.Target?.AsciiHost?.Contains("xn--", StringComparison.OrdinalIgnoreCase) == true).ToString()),
            ]);
    }

    private static IEnumerable<Evidence> BuildAttachmentEvidence(
        CollectedContent collected,
        EvidenceBuilder builder,
        MimeParseLimits limits)
    {
        var mismatched = collected.Attachments.Where(a => a.Assessment.TypeMismatch).ToList();

        yield return builder.Build(
            MimeSignals.AttachmentTypeMismatch,
            collected.Attachments.Count == 0 ? EvidenceAvailability.NotApplicable : EvidenceAvailability.Available,
            mismatched.Count,
            attributes:
            [
                Of("attachmentCount", collected.Attachments.Count.ToString()),
                Of("genericDeclaredTypeCount",
                    collected.Attachments.Count(a => a.Assessment.GenericDeclaredType).ToString()),
                Of("doubleExtensionCount",
                    collected.Attachments.Count(a => a.Assessment.DoubleExtension).ToString()),
                Of("directionOverrideCount",
                    collected.Attachments.Count(a => a.Assessment.DirectionOverrideInName).ToString()),
                Of("executableExtensionCount",
                    collected.Attachments.Count(a => a.Assessment.ExecutableExtension).ToString()),
                .. mismatched.Take(6).Select(a => Of(
                    "mismatch",
                    $"{EvidenceBuilder.Truncate(a.Metadata.FileName, 64)} declares {a.Metadata.DeclaredContentType} " +
                    $"but implies {a.Assessment.ImpliedContentType}")),
            ]);

        foreach (var attachment in collected.Attachments.Take(limits.MaxAttachments))
        {
            yield return builder.Build(
                MimeSignals.AttachmentHash,
                EvidenceAvailability.Available,
                attachment.Metadata.SizeBytes,
                scope: "attachment",
                attributes:
                [
                    Of("fileName", EvidenceBuilder.Truncate(attachment.Metadata.FileName, 128)),
                    Of("declaredContentType", attachment.Metadata.DeclaredContentType),
                    Of("hash", attachment.Metadata.ContentHash ?? "unavailable"),
                    Of("hashPartial", attachment.HashPartial.ToString()),
                    Of("embeddedMessage", attachment.IsEmbeddedMessage.ToString()),
                ]);
        }

        var unavailable = collected.Attachments.Count(a => a.Metadata.ContentUnavailable);
        yield return builder.Build(
            MimeSignals.AttachmentUnavailable,
            collected.Attachments.Count == 0 ? EvidenceAvailability.NotApplicable : EvidenceAvailability.Available,
            unavailable,
            attributes:
            [
                Of("attachmentCount", collected.Attachments.Count.ToString()),
                Of("unavailableCount", unavailable.ToString()),
            ]);
    }

    private static Evidence BuildTemplateFingerprint(
        QuotedSplit quoted,
        int totalTokens,
        int newTokens,
        int linkCount,
        EvidenceBuilder builder)
    {
        var tokens = TextTools.Tokenize(quoted.NewText);
        var simhash = TextTools.SimHash(tokens);
        var skeleton = TextTools.Skeleton(quoted.NewText);

        var placeholders = skeleton.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(t => t is "email" or "url" or "id" or "num");

        return builder.Build(
            MimeSignals.TemplateFingerprint,
            EvidenceAvailability.Available,
            null,
            attributes:
            [
                // A similarity hash, not a verdict: two messages from one template land within a
                // small Hamming distance, and grouping them is the adaptive engine's job because
                // it is the component that holds the other messages.
                Of("simhash", TextTools.ToHex(simhash)),
                Of("skeletonDigest", TextTools.ToHex(TextTools.StableHash(skeleton))),
                Of("skeletonLength", skeleton.Length.ToString()),
                Of("placeholderCount", placeholders.ToString()),
                Of("newTokenCount", newTokens.ToString()),
                Of("totalTokenCount", totalTokens.ToString()),
                Of("linkCount", linkCount.ToString()),
            ]);
    }

    private static Evidence BuildCoverageEvidence(
        AnalysisCoverage coverage,
        AuthenticationContext? authentication,
        EvidenceBuilder builder)
    {
        var reduced = new List<string>();
        if (!coverage.BodyParsed)
        {
            reduced.Add("body-not-parsed");
        }

        if (coverage.ContentEncrypted)
        {
            reduced.Add("encrypted-content");
        }

        if (coverage.Truncated)
        {
            reduced.Add("truncated");
        }

        if (coverage.ParserLimitExceeded)
        {
            reduced.Add("parser-limit-exceeded");
        }

        if (coverage.ConversationContextMissing)
        {
            reduced.Add("no-conversation-context");
        }

        if (authentication is null || authentication.ProvenanceIncomplete)
        {
            reduced.Add("authentication-provenance-incomplete");
        }

        return builder.Build(
            MimeSignals.AnalysisCoverage,
            reduced.Count == 0 ? EvidenceAvailability.Available : EvidenceAvailability.ReducedCoverage,
            reduced.Count,
            attributes:
            [
                // One attribute per reason. They share a name on purpose: a reader asks "is
                // truncated among the coverage reasons", and the list shape keeps all of them
                // rather than collapsing the set down to its last member.
                .. reduced.Select(r => Of("reduced", r)),
                Of("reducedCount", reduced.Count.ToString()),
            ]);
    }

    private static MimeAnalysisResult Reject(
        MimeAnalysisRequest request,
        EvidenceBuilder builder,
        MimeParseRejection rejection,
        AnalysisCoverage coverage)
    {
        var evidence = new List<Evidence>
        {
            builder.Build(
                MimeSignals.MessageSize,
                EvidenceAvailability.Available,
                request.RawMessage.Length,
                attributes: [Of("rejected", rejection.Reason)]),
            builder.Build(
                MimeSignals.AnalysisCoverage,
                EvidenceAvailability.ReducedCoverage,
                1.0,
                attributes:
                [
                    Of("reduced", "not-analysed"),
                    Of("reason", rejection.Reason),
                    Of("disposition", rejection.Disposition.ToString()),
                ]),
            builder.Build(
                MimeSignals.MessageStructure,
                EvidenceAvailability.Unavailable,
                null,
                attributes: [Of("reason", rejection.Reason)]),
        };

        return new MimeAnalysisResult
        {
            Disposition = rejection.Disposition,
            Message = null,
            Evidence = evidence,
            Coverage = coverage,
            Rejection = rejection,
        };
    }

    /// <summary>
    /// Provenance that was never supplied. Recorded as incomplete and explicitly not as clean,     /// "we were not told" and "we were told it was fine" are different facts.
    /// </summary>
    private static AuthenticationContext IncompleteProvenance() => new()
    {
        ConnectingIp = null,
        AuthenticatedAccount = null,
        Results = [],
        ApprovedSenderIdentities = [],
        ProvenanceIncomplete = true,
    };

    private static bool HasAnyDisplayName(IdentityAnalysis identity) =>
        identity.From.Concat(identity.ReplyTo).Concat(identity.Sender).Any(f => f.DisplayName.Length > 0);

    private static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..].TrimEnd('>').Trim() : string.Empty;
    }

    private static string Digest(IReadOnlyList<string> values)
    {
        var joined = string.Join("\n", values);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}

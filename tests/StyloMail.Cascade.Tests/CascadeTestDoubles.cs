using StyloMail.Core;

namespace StyloMail.Cascade.Tests;

/// <summary>
/// A classifier that records what it was asked and answers from a script.
/// </summary>
/// <remarks>
/// <b>The recording is the assertion.</b> Which dimensions reached the strong model, and how many
/// times it was called at all, are the two things the cascade promises and the only two things a
/// rule over answers cannot show on its own.
/// </remarks>
internal sealed class RecordingClassifier(Func<SemanticMailInput, SemanticAssessment> respond)
    : ISemanticMailClassifier
{
    public List<SemanticMailInput> Calls { get; } = [];

    public int CallCount => Calls.Count;

    /// <summary>The dimensions of the last call, or empty when it was never called.</summary>
    public IReadOnlyList<string> LastAskedDimensionIds =>
        Calls.Count == 0 ? [] : [.. Calls[^1].Dimensions.Select(d => d.Id)];

    public ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken)
    {
        Calls.Add(input);
        return ValueTask.FromResult(respond(input));
    }
}

internal static class CascadeRows
{
    internal const string LocalSource = "nimble/2+nimble:latest";

    internal const string HostedSource = "jev-1.13.0";

    internal static Evidence Available(
        string signalId,
        double value,
        string sourceVersion = LocalSource,
        IReadOnlyList<EvidenceAttribute>? attributes = null) => new()
        {
            SignalId = signalId,
            Origin = EvidenceOrigin.Semantic,
            Availability = EvidenceAvailability.Available,
            Value = value,
            Confidence = null,
            SampleSupport = null,
            SourceVersion = sourceVersion,
            ObservedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero),
            ObservedScope = "message",
            Attributes = attributes,
        };

    /// <summary>An <c>Available</c> row carrying a shortening reason, as the local adapter writes one.</summary>
    internal static Evidence Shortened(
        string signalId,
        double value,
        string reason = "the client shortened the message body to fit the context window")
        => Available(
            signalId,
            value,
            attributes: [new EvidenceAttribute { Name = "reason", Value = reason }]);

    internal static Evidence Unavailable(string signalId, string sourceVersion = LocalSource, string? reason = null)
        => new()
        {
            SignalId = signalId,
            Origin = EvidenceOrigin.Semantic,
            Availability = EvidenceAvailability.Unavailable,
            Value = null,
            Confidence = null,
            SampleSupport = null,
            SourceVersion = sourceVersion,
            ObservedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero),
            ObservedScope = "message",
            Attributes = reason is null
                ? null
                : [new EvidenceAttribute { Name = "reason", Value = reason }],
        };

    internal static Evidence ReducedCoverage(string signalId, double value)
        => Available(signalId, value) with { Availability = EvidenceAvailability.ReducedCoverage };

    internal static Evidence NotApplicable(string signalId)
        => Available(signalId, 0.0) with { Availability = EvidenceAvailability.NotApplicable, Value = null };
}

internal static class CascadeInputs
{
    internal static SemanticMailInput Input(
        IReadOnlyList<SemanticDimension>? dimensions = null,
        bool withConversation = false)
    {
        var input = new SemanticMailInput
        {
            Message = new MailAnalysisInput
            {
                Envelope = new MailEnvelope
                {
                    InternalMessageId = "msg-1",
                    TenantId = "tenant-a",
                    Direction = MailDirection.Inbound,
                    TrustedPrincipalId = "connector-1",
                    MailFrom = "sender@example.com",
                    RcptTo = ["finance@contoso.com"],
                    ReceivedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero),
                    MimeDigest = "sha256:abc",
                    PayloadReference = "spool/msg-1",
                },
                Authentication = new AuthenticationContext
                {
                    Results = [],
                    ApprovedSenderIdentities = [],
                    ProvenanceIncomplete = true,
                },
                Channel = ChannelContext.Email,
                Subject = "Invoice attached",
                BodyText = "Please confirm your password to view the invoice.",
                Links = [],
                Attachments = [],
                Coverage = new AnalysisCoverage
                {
                    BodyParsed = true,
                    HtmlPresent = false,
                    HasAttachments = false,
                    HtmlTextDisagreement = false,
                    ParserLimitExceeded = false,
                    ContentEncrypted = false,
                    Truncated = false,
                    ConversationContextMissing = !withConversation,
                },
                ConversationContext = withConversation ? ["Are we still on for Tuesday?"] : null,
            },
            Dimensions = dimensions ?? SemanticDimensions.All,
        };

        return input;
    }

    /// <summary>The two dimensions the rule tests use, so a failure names one question and not twelve.</summary>
    internal static IReadOnlyList<SemanticDimension> Two => [SemanticDimensions.All[0], SemanticDimensions.All[1]];
}

internal static class CascadeAssessments
{
    internal static SemanticAssessment Of(
        IReadOnlyList<Evidence> evidence,
        string? resolvedModelVersion = "nimble:latest",
        int? inputTokens = null,
        int? outputTokens = null) => new()
        {
            Evidence = evidence,
            ResolvedModelVersion = resolvedModelVersion,
            Cache = new CacheProvenance
            {
                Hit = false,
                KeyDigest = "local-digest",
                CachedAt = null,
                ModelVersion = resolvedModelVersion,
                Stale = false,
            },
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
        };
}

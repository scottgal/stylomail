using StyloMail.Core;
using StyloMail.Host.Contracts;

namespace StyloMail.Host.Tests;

/// <summary>
/// A reason on an evidence row reaches the served response, INCLUDING on a row that is still
/// <c>Available</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is owed.</b> <c>AvailabilityReasons</c> was added to <c>EvidenceResponse</c> so that a
/// reason a producer already records stops being visible only to whatever reads the ledger.
/// <c>DecisionResponse.From</c> began copying it at <c>DecisionResponse.cs:141</c>, and until this
/// file existed <b>no test drove a row carrying a reason into the mapping at all</b>, so the member
/// was covered by nothing, and a served response containing a populated <c>AvailabilityReasons</c>
/// had never been produced in a test.
/// </para>
/// <para>
/// <b>And the live case is an <c>Available</c> row, which is why the subject below is one.</b> The
/// shape this member was first written for is a refusal, where the row is <c>Unavailable</c>. The
/// landed ruling on the shortening path goes the other way: the row <b>stays <c>Available</c> and
/// stays counted</b>, and the cut is carried in the reason attribute alone, because a capped read is
/// still a real answer about the message as bounded. <b>So the row a consumer most needs to see a
/// reason on is one it cannot distinguish by availability</b>, and that is the row asserted here.
/// </para>
/// <para>
/// <b>The mapping is agnostic to the wording, and the assertion is written that way.</b> It copies
/// every attribute named <c>reason</c> whatever its text. The literal below is the classifier's own
/// (<c>NimbleSemanticMailClassifier.PromptShortenedReason</c>, which is <c>private</c>), quoted so the
/// fixture reads like the wire rather than like a placeholder; a reworded constant must NOT fail this
/// test, because what is under test is the mapping and not that constant.
/// </para>
/// <para>
/// <b>LANDED AND RUN.</b> The file is committed at <c>68234f5</c>, and these six arms were first
/// exercised by <c>nimble-</c>'s filtered run of this project on 2026-10-02, which reported
/// <c>Passed! Failed: 0, Passed: 6, Skipped: 0, Total: 6</c>. That run is the slot this paragraph used
/// to ask for: it replaces one reading "UNCOMPILED AND UNRUN", which was true when written and false
/// the moment the file was committed, and which no reader of this file could have known either way.
/// </para>
/// </remarks>
public sealed class AvailabilityReasonsTests
{
    /// <summary>The classifier's own literal, quoted here rather than referenced, because it is private.</summary>
    private const string ShortenedReason = "the client shortened the message body to fit the context window";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    // Hoisted rather than written inline, because CA1861 is raised to ERROR in this project: a constant
    // array passed as an argument is a build failure here, not a suggestion. Each of these is one arm's
    // expected value, so hoisting also lets the expectations be read side by side.
    private static readonly string[] ShortenedOnly = [ShortenedReason];

    private static readonly string[] ShortenedThenParsed =
        [ShortenedReason, "the body was parsed under a limit"];

    private static readonly string[] RefusalOnly =
        ["server evaluated 20498 prompt tokens at an applied window of 16384"];

    /// <summary>
    /// The subject: an <c>Available</c> semantic row carrying the shortening reason serves that reason.
    /// </summary>
    [Fact]
    public void An_available_row_carrying_a_reason_serves_it()
    {
        var decision = DecisionResponse.From(Assessment(Row(EvidenceAvailability.Available, ShortenedReason)));

        var row = Assert.Single(decision.Evidence);
        Assert.Equal(EvidenceAvailability.Available, row.Availability);
        Assert.Equal(ShortenedOnly, row.AvailabilityReasons);
    }

    /// <summary>
    /// POPULATION CONTROL. A row with no attributes must serve an EMPTY list rather than null, so a
    /// consumer can read the member unconditionally.
    /// </summary>
    /// <remarks>
    /// Without this arm the subject above would also pass for an implementation that returned the
    /// reason for EVERY row, which is the failure that would put a cut on a message that had none.
    /// </remarks>
    [Fact]
    public void A_row_with_no_reason_serves_an_empty_list()
    {
        var decision = DecisionResponse.From(Assessment(Row(EvidenceAvailability.Available)));

        var row = Assert.Single(decision.Evidence);
        Assert.NotNull(row.AvailabilityReasons);
        Assert.Empty(row.AvailabilityReasons);
    }

    /// <summary>
    /// FIRING CONTROL. A row carrying an attribute under some OTHER name serves an empty list, which is
    /// what proves the reader keys on the name <c>reason</c> rather than on the mere presence of an
    /// attribute. A zero with no firing control proves nothing about the thing it names.
    /// </summary>
    [Fact]
    public void A_row_whose_attribute_is_named_something_else_serves_nothing()
    {
        var decision = DecisionResponse.From(
            Assessment(RowWith(EvidenceAvailability.Available, "window", ShortenedReason)));

        var row = Assert.Single(decision.Evidence);
        Assert.Empty(row.AvailabilityReasons);
    }

    /// <summary>
    /// EVERY reason, in order, and not the first: the member is a list deliberately, because a row can
    /// carry more than one and a reader that takes <c>[0]</c> names one of a cause's causes.
    /// </summary>
    [Fact]
    public void A_row_carrying_two_reasons_serves_both_in_order()
    {
        var decision = DecisionResponse.From(
            Assessment(Row(EvidenceAvailability.Available, ShortenedReason, "the body was parsed under a limit")));

        var row = Assert.Single(decision.Evidence);
        Assert.Equal(ShortenedThenParsed, row.AvailabilityReasons);
    }

    /// <summary>
    /// An attribute written with an empty value is DROPPED rather than published as an empty string: a
    /// producer that names the attribute and says nothing has said nothing, and an empty entry would
    /// read as a reason a consumer could render.
    /// </summary>
    [Fact]
    public void An_empty_reason_value_is_dropped_rather_than_published_blank()
    {
        var decision = DecisionResponse.From(Assessment(Row(EvidenceAvailability.Available, string.Empty)));

        var row = Assert.Single(decision.Evidence);
        Assert.Empty(row.AvailabilityReasons);
    }

    /// <summary>
    /// A refusal still serves its reason, which is the shape the member was first written for and which
    /// the shortening ruling did not replace. Asserted so that a later change cannot trade one case for
    /// the other.
    /// </summary>
    [Fact]
    public void An_unavailable_row_carrying_a_reason_still_serves_it()
    {
        var decision = DecisionResponse.From(
            Assessment(Row(EvidenceAvailability.Unavailable, "server evaluated 20498 prompt tokens at an applied window of 16384")));

        var row = Assert.Single(decision.Evidence);
        Assert.Equal(EvidenceAvailability.Unavailable, row.Availability);
        Assert.Equal(RefusalOnly, row.AvailabilityReasons);
    }

    private static Evidence Row(EvidenceAvailability availability, params string[] reasons)
        => new()
        {
            SignalId = "semantic.credential_request",
            Origin = EvidenceOrigin.Semantic,
            Availability = availability,
            Value = 0.9,
            SourceVersion = "p/nimble",
            ObservedAt = At,
            ObservedScope = "message",
            Attributes = reasons.Length == 0
                ? null
                : [.. reasons.Select(value => new EvidenceAttribute { Name = "reason", Value = value })],
        };

    private static Evidence RowWith(EvidenceAvailability availability, string name, string value)
        => new()
        {
            SignalId = "semantic.credential_request",
            Origin = EvidenceOrigin.Semantic,
            Availability = availability,
            Value = 0.9,
            SourceVersion = "p/nimble",
            ObservedAt = At,
            ObservedScope = "message",
            Attributes = [new EvidenceAttribute { Name = name, Value = value }],
        };

    /// <summary>
    /// The smallest <see cref="MailAssessment"/> that maps, so the fixture cannot fail for a reason
    /// unrelated to the row under test.
    /// </summary>
    private static MailAssessment Assessment(Evidence row)
        => new()
        {
            AssessmentId = "asm_availability_reasons",
            InternalMessageId = "msg_availability_reasons",
            TenantId = "tenant",
            Channel = ChannelContext.Email,
            Evidence = [row],
            RiskDimensions = [],
            RiskIndex = 0.0,
            RiskIndexDenominator = 1.0,
            CoveredWeightFraction = 1.0,
            Action = MailAction.Allow,
            DeliveryTiming = DeliveryTiming.PostDelivery,
            Reasons = [],
            Versions = new AssessmentVersions
            {
                PolicyVersion = "policy/1",
                QuestionSchemaVersion = "q/1",
                PreprocessingVersion = "p/1",
            },
            Coverage = new AnalysisCoverage
            {
                BodyParsed = true,
                HtmlPresent = false,
                HasAttachments = false,
                HtmlTextDisagreement = false,
                ParserLimitExceeded = false,
                ContentEncrypted = false,
                Truncated = false,
                ConversationContextMissing = true,
            },
            RecipientDispositions = [],
            AssessedAt = At,
        };
}

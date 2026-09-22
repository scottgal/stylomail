using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

/// <summary>
/// Loads the <c>.eml</c> fixtures and builds the request the adapter expects.
/// </summary>
/// <remarks>
/// The envelope is supplied explicitly rather than derived from the message, because that is the
/// whole point of the contract: the transport boundary establishes identity, and a message never
/// gets to assert its own.
/// </remarks>
internal static class FixtureMessage
{
    public static readonly DateTimeOffset ReceivedAt = new(2026, 9, 22, 9, 30, 0, TimeSpan.Zero);

    public static byte[] Bytes(string fixtureFileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureFileName));

    public static MimeAnalysisRequest Request(
        string fixtureFileName,
        string? mailFrom = "sender@example.com",
        IReadOnlyList<string>? rcptTo = null,
        AuthenticationContext? authentication = null,
        MimeParseLimits? limits = null,
        IReadOnlyList<string>? conversationContext = null)
    {
        var bytes = Bytes(fixtureFileName);

        return new MimeAnalysisRequest
        {
            Envelope = new MailEnvelope
            {
                InternalMessageId = "msg-" + fixtureFileName,
                TenantId = "tenant-test",
                Direction = MailDirection.Inbound,
                TrustedPrincipalId = "connector-test",
                MailFrom = mailFrom ?? string.Empty,
                RcptTo = rcptTo ?? ["bob@example.org"],
                ReceivedAt = ReceivedAt,
                MimeDigest = "sha256:test",
                PayloadReference = "spool://test/" + fixtureFileName,
                UntrustedMessageIdHeader = null,
            },
            RawMessage = bytes,
            Authentication = authentication,
            ConversationContext = conversationContext,
            TimeProvider = TimeProvider.System,
            Limits = limits ?? MimeParseLimits.Default,
        };
    }

    public static MimeAnalysisRequest FromBytes(
        byte[] bytes,
        MimeParseLimits? limits = null,
        TimeProvider? timeProvider = null) => new()
    {
        Envelope = new MailEnvelope
        {
            InternalMessageId = "msg-inline",
            TenantId = "tenant-test",
            Direction = MailDirection.Inbound,
            TrustedPrincipalId = "connector-test",
            MailFrom = "sender@example.com",
            RcptTo = ["bob@example.org"],
            ReceivedAt = ReceivedAt,
            MimeDigest = "sha256:test",
            PayloadReference = "spool://test/inline",
            UntrustedMessageIdHeader = null,
        },
        RawMessage = bytes,
        TimeProvider = timeProvider ?? TimeProvider.System,
        Limits = limits ?? MimeParseLimits.Default,
    };

    /// <summary>A trusted boundary verifier, for tests that care about trusted authentication results.</summary>
    public static AuthenticationContext TrustedAuthentication(params AuthenticationResult[] results) => new()
    {
        ConnectingIp = "203.0.113.9",
        AuthenticatedAccount = null,
        Results = results,
        ApprovedSenderIdentities = [],
        ProvenanceIncomplete = false,
    };

    public static AuthenticationResult Result(string mechanism, string result, bool trusted = true) => new()
    {
        Mechanism = mechanism,
        Result = result,
        VerifierId = trusted ? "boundary-verifier-1" : null,
        FromTrustedVerifier = trusted,
        Detail = null,
    };
}

/// <summary>Convenience accessors for reading a single signal out of a result.</summary>
internal static class EvidenceLookup
{
    public static Evidence Signal(this MimeAnalysisResult result, string signalId) =>
        result.Evidence.Single(e => e.SignalId == signalId);

    /// <summary>The first attribute with this name, or null.</summary>
    public static string? Attribute(this Evidence evidence, string name) =>
        evidence.Attributes?.FirstOrDefault(a => a.Name == name)?.Value;

    /// <summary>
    /// Every attribute with this name. Several signals are multi-valued, coverage reasons above
    /// all, and the whole point of the list shape is that all of them survive.
    /// </summary>
    public static IReadOnlyList<string> AttributesNamed(this Evidence evidence, string name) =>
        evidence.Attributes?.Where(a => a.Name == name).Select(a => a.Value).ToList() ?? [];

    public static IReadOnlyList<string> AttributeNames(this Evidence evidence) =>
        evidence.Attributes?.Select(a => a.Name).ToList() ?? [];
}

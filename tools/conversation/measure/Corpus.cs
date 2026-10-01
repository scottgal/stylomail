using System.Security.Cryptography;
using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Conversation.Measure;

/// <summary>
/// Loads the same committed fixtures the provider lanes measure over, through the real MIME parser.
/// </summary>
/// <remarks>
/// Shared on purpose rather than copied. A conversation measurement taken over a hand-built message
/// would be a measurement of the description we imagined, and a case that means one thing here and
/// another in the Jev and Nimble recordings would make every join across those lanes meaningless.
/// </remarks>
internal static class Corpus
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    internal static string FixturesDirectory =>
        Path.Combine(RepositoryRoot.Value, "tests", "fixtures", "jev");

    /// <summary>The committed bytes of a case, for a caller that has to hand them to the assessor.</summary>
    internal static byte[] Bytes(string caseName) =>
        File.ReadAllBytes(Path.Combine(FixturesDirectory, caseName + ".eml"));

    internal static IReadOnlyList<string> Cases =>
        Directory.Exists(FixturesDirectory)
            ? [.. Directory.GetFiles(FixturesDirectory, "*.eml")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)]
            : [];

    /// <summary>
    /// Parses a committed fixture with the shipping analyser, optionally with a supplied window.
    /// </summary>
    /// <remarks>
    /// The window is threaded exactly as <c>POST /v1/assessments</c> threads it, through
    /// <see cref="MimeAnalysisRequest.ConversationContext"/>, so the coverage flag this returns is
    /// the one the Host would produce for the same request rather than a value this tool computed
    /// for itself.
    /// </remarks>
    internal static MimeAnalysisResult Analyze(
        string caseName,
        IReadOnlyList<string>? conversation,
        bool authenticated = false,
        string verifierId = "measure-boundary",
        string tenantId = "measure")
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDirectory, caseName + ".eml"));

        var envelope = new MailEnvelope
        {
            InternalMessageId = $"conversation-measure-{caseName}",
            TenantId = tenantId,
            Direction = MailDirection.Inbound,
            TrustedPrincipalId = "conversation-measure",
            MailFrom = "sender@example.test",
            RcptTo = ["recipient@example.test"],
            ReceivedAt = DateTimeOffset.UnixEpoch,
            MimeDigest = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            PayloadReference = PayloadReferences.Ephemeral,
        };

        var result = new BoundedMimeMessageAnalyzer().Analyze(new MimeAnalysisRequest
        {
            Envelope = envelope,
            RawMessage = bytes,
            ConversationContext = conversation,
            Authentication = authenticated ? TrustedAuthentication(verifierId) : null,
        });

        if (!result.IsAnalysable)
        {
            throw new InvalidOperationException(
                $"The corpus message '{caseName}' was not analysable ({result.Disposition}). A case that "
                + "does not parse would make this a measurement of a rejection rather than an assessment.");
        }

        return result;
    }

    /// <summary>The classifier input for a case, with a window supplied or absent.</summary>
    /// <remarks>
    /// <paramref name="authenticated"/>, <paramref name="profile"/> and <paramref name="tenantId"/>
    /// are here for the site experiment, which compares this lane's measurement shape against the
    /// shape the Host sends. All three are part of what the provider is shown, so a candidate that
    /// differs in any of them is a different question rather than a variation of this one.
    /// <para>
    /// <b>The tenant is the Host's to choose, not the caller's.</b> The request body carries no
    /// tenant: the Host takes it from the authenticated principal's claim, so the same request
    /// against a differently-configured deployment is a different question and keys differently. A
    /// candidate that has to reproduce a digest the Host served therefore has to carry the Host's
    /// tenant, which is why this is a parameter rather than a constant. It is <em>not</em> an
    /// identity claim: nothing here is authorised by it, and the value this lane measures under is
    /// its own.
    /// </para>
    /// </remarks>
    internal static SemanticMailInput Input(
        string caseName,
        IReadOnlyList<string>? conversation,
        bool authenticated = false,
        string verifierId = "measure-boundary",
        BehaviouralProfile? profile = null,
        string tenantId = "measure")
        => new()
        {
            Message = Analyze(caseName, conversation, authenticated, verifierId, tenantId).Message!,
            Dimensions = SemanticDimensions.All,
            Profile = profile,
        };

    /// <summary>
    /// Provenance as a trusted boundary would report it, so the conversation reason is the only one left.
    /// </summary>
    /// <remarks>
    /// Without this the coverage measurement is confounded: <c>authentication-provenance-incomplete</c>
    /// reduces coverage on its own, so the availability would stay <c>ReducedCoverage</c> whatever the
    /// conversation did, and the claim "supplying a window changes the coverage outcome" would be
    /// unprovable either way. Supplying provenance the way the Host supplies it isolates the one reason
    /// this lane is measuring.
    /// <para>
    /// <b>The verifier id is part of the question, not a label on it.</b> The provider's state carries
    /// each trusted result as <c>(mechanism, result, verifier)</c>, so two inputs that differ only in
    /// this string are two different prompts and hash to two different cache keys. It is a parameter
    /// for that reason: the site experiment has to reproduce the exact string the Host records, and
    /// the Host records the caller's own value as-is.
    /// </para>
    /// </remarks>
    internal static AuthenticationContext TrustedAuthentication(string verifierId) => new()
    {
        ConnectingIp = "203.0.113.10",
        Results =
        [
            new AuthenticationResult
            {
                Mechanism = "spf",
                Result = "pass",
                VerifierId = verifierId,
                FromTrustedVerifier = true,
                Detail = "domain=example.test; ip=203.0.113.10",
            },
        ],
        ApprovedSenderIdentities = [],
        ProvenanceIncomplete = false,
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StyloMail.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root by walking up from '{AppContext.BaseDirectory}'.");
    }
}

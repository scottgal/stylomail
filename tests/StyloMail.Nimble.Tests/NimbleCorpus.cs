using System.Security.Cryptography;
using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Nimble.Tests;

/// <summary>
/// The same six sample messages the hosted corpus uses, loaded the same way.
/// </summary>
/// <remarks>
/// <b>Shared corpus, not a parallel one.</b> The comparison this lane exists to make is a join on
/// case name and dimension id with the hosted adapter's recordings, and a case that means one thing
/// to one adapter and another to the other would make that join meaningless. The fixtures are read
/// from <c>tests/fixtures/jev</c> rather than copied, so the two can never diverge.
/// </remarks>
internal static class NimbleCorpus
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    internal static string FixturesDirectory => Path.Combine(RepositoryRoot.Value, "tests", "fixtures", "jev");

    internal static IReadOnlyList<string> Cases =>
        Directory.Exists(FixturesDirectory)
            ? [.. Directory.GetFiles(FixturesDirectory, "*.eml")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)]
            : [];

    /// <summary>
    /// Builds the classifier input for a case by parsing its message with the real MIME adapter.
    /// </summary>
    /// <remarks>
    /// Parsed rather than hand-built, so the request the provider sees is the one the sample message
    /// actually produces. A hand-built input would make the measurement a record of what we imagined
    /// the message contained.
    /// </remarks>
    internal static SemanticMailInput BuildInput(string caseName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDirectory, caseName + ".eml"));
        var envelope = new MailEnvelope
        {
            InternalMessageId = $"corpus-{caseName}",
            TenantId = "corpus",
            Direction = MailDirection.Inbound,
            TrustedPrincipalId = "corpus-loader",
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
        });

        if (!result.IsAnalysable)
        {
            throw new InvalidOperationException(
                $"The corpus message '{caseName}' was not analysable ({result.Disposition}). "
                + "A corpus case has to parse, or its measurement describes a rejection rather than an assessment.");
        }

        // Conversational continuity is only asked when prior context is present, so the threaded case
        // supplies it and every other case does not. That difference is the whole reason the case
        // exists: without it the corpus never exercises the asked branch.
        var conversation = caseName == "reply-in-thread"
            ? new[] { "From: colleague@example.test\nSubject: Re: quarterly figures\n\nHere are the numbers you asked for." }
            : null;

        return new SemanticMailInput
        {
            Message = result.Message! with { ConversationContext = conversation },
            Dimensions = SemanticDimensions.All,
        };
    }

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

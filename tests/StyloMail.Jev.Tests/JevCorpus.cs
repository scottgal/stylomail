using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Jev.Tests;

/// <summary>Raised when a recording is attempted with no credential available.</summary>
/// <remarks>
/// Loud by design. The alternative is writing an empty corpus that looks like a completed recording,
/// which is the failure this whole tier exists to avoid.
/// </remarks>
public sealed class JevCredentialMissingException : Exception
{
    public JevCredentialMissingException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The recorded Jev corpus: where it lives, how a case becomes an input, and what a recording carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>One loader, used by both halves.</b> The recorder and the replay test must agree on the input
/// that produced a response, or the corpus stops corresponding to its own fixtures and the replay
/// assertions quietly measure something else. Sharing this type is what prevents that.
/// </para>
/// <para>
/// <b>The response is stored as received.</b> A recording is the provider's body verbatim, byte for
/// byte, because a normalised capture discards exactly the differences a replay test exists to catch.
/// Provenance goes in a sidecar file rather than a header, so nothing is wrapped around the payload.
/// </para>
/// </remarks>
internal static class JevCorpus
{
    /// <summary>Environment variable holding the bearer key. Same name the adapter's options use.</summary>
    internal const string ApiKeyEnvironmentVariable = JevOptions.ApiKeyEnvironmentVariable;

    // There is deliberately no file fallback here, and that is a ruling rather than an omission.
    //
    // A key file at the repository root is a hard prohibition in the mission this lane grew out of,
    // and the spec reserves that file for the overview's own live verification runs. Offering it as
    // a fallback would mean two rules pointing opposite ways and somebody having to decide which
    // wins. The environment variable is the only source this harness reads, so there is nothing to
    // decide.

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    internal static string FixturesDirectory => Path.Combine(RepositoryRoot.Value, "tests", "fixtures", "jev");

    /// <summary>
    /// The names of the sample messages, without extension, in a stable order.
    /// </summary>
    internal static IReadOnlyList<string> Cases =>
        Directory.Exists(FixturesDirectory)
            ? [.. Directory.GetFiles(FixturesDirectory, "*.eml")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)]
            : [];

    internal static bool HasAnyRecording => RecordedCases.Count > 0;

    /// <summary>The cases that have a recording committed beside them.</summary>
    internal static IReadOnlyList<string> RecordedCases =>
        [.. Cases.Where(c => File.Exists(RecordingPath(c)))];

    internal static string MessagePath(string caseName) => Path.Combine(FixturesDirectory, caseName + ".eml");

    /// <summary>Where the provider's response body is stored, verbatim.</summary>
    internal static string RecordingPath(string caseName) => Path.Combine(FixturesDirectory, caseName + ".response.json");

    /// <summary>Where the recording's provenance is stored, in a sidecar so the body stays untouched.</summary>
    internal static string ProvenancePath(string caseName) => Path.Combine(FixturesDirectory, caseName + ".response.meta.json");

    internal static string ReadRecording(string caseName) => File.ReadAllText(RecordingPath(caseName), Encoding.UTF8);

    internal static JevRecordingProvenance ReadProvenance(string caseName) =>
        JsonSerializer.Deserialize<JevRecordingProvenance>(
            File.ReadAllText(ProvenancePath(caseName), Encoding.UTF8),
            JsonOptions)
        ?? throw new InvalidOperationException($"{ProvenancePath(caseName)} did not parse.");

    internal static async Task WriteRecordingAsync(
        string caseName,
        string responseBody,
        JevRecordingProvenance provenance,
        CancellationToken cancellationToken)
    {
        // The body first and the provenance second. A provenance file without a body would describe a
        // recording that does not exist, which is worse than an unfinished pair.
        await File.WriteAllTextAsync(RecordingPath(caseName), responseBody, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                ProvenancePath(caseName),
                JsonSerializer.Serialize(provenance, IndentedJson),
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the classifier input for a case by parsing its message with the real MIME adapter.
    /// </summary>
    /// <remarks>
    /// Parsed rather than hand-built, so the request the provider sees is the one the sample message
    /// actually produces. A hand-built input would make the corpus a record of what we imagined the
    /// message contained.
    /// </remarks>
    internal static SemanticMailInput BuildInput(string caseName)
    {
        var bytes = File.ReadAllBytes(MessagePath(caseName));
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

        var analyzer = new BoundedMimeMessageAnalyzer();
        var result = analyzer.Analyze(new MimeAnalysisRequest
        {
            Envelope = envelope,
            RawMessage = bytes,
        });

        if (!result.IsAnalysable)
        {
            throw new InvalidOperationException(
                $"The corpus message '{caseName}' was not analysable ({result.Disposition}). "
                + "A corpus case has to parse, or its recording describes a rejection rather than an assessment.");
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

    /// <summary>
    /// The message shown when no credential is available. Names both places, never a value.
    /// </summary>
    internal static string MissingCredentialMessage =>
        $"No semantic provider credential is available, so nothing can be recorded. Supply it as the "
        + $"{ApiKeyEnvironmentVariable} environment variable, for example by exporting it from a file "
        + "outside the repository so the value never reaches a command line or an output: "
        + $"export {ApiKeyEnvironmentVariable}=\"$(cat /path/to/key)\". The key is never printed, "
        + "logged, written into a fixture or committed.";

    /// <summary>
    /// Reads the credential from the environment, and from nowhere else.
    /// </summary>
    /// <remarks>
    /// <b>The value is returned and never rendered.</b> Nothing here writes it to a log, an exception,
    /// a fixture or a report, and the exception raised elsewhere carries only the message above. A
    /// key read from a file would also be a key on disk inside a working copy, which is one careless
    /// `git add -f` from being committed; an environment variable is scoped to the process holding it.
    /// </remarks>
    internal static bool TryReadCredential(out string apiKey)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            apiKey = fromEnvironment;
            return true;
        }

        apiKey = string.Empty;
        return false;
    }

    internal static string RequireCredential()
        => TryReadCredential(out var key) ? key : throw new JevCredentialMissingException(MissingCredentialMessage);

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Walks up from the test binary until it finds the solution file.
    /// </summary>
    /// <remarks>
    /// A relative path from the binary would be a guess about configuration depth, and a guess that
    /// breaks the first time somebody builds to a different output path.
    /// </remarks>
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

/// <summary>
/// What a recording must carry so it can be re-recorded and trusted later.
/// </summary>
/// <remarks>
/// Stored as received rather than reformatted, per the corpus README. The model requested and the
/// model reported are both kept because an alias that resolves elsewhere is a discrepancy this
/// project has already been bitten by once.
/// </remarks>
public sealed record JevRecordingProvenance
{
    public required string Case { get; init; }

    /// <summary>The model id we asked for, from configuration. Pinned, never an alias.</summary>
    public required string RequestedModel { get; init; }

    /// <summary>The model id the provider reported answering with.</summary>
    public string? ReportedModel { get; init; }

    public required string QuestionSchemaVersion { get; init; }

    /// <summary>When the recording was made.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>Always "live" for a committed recording. A fixture dressed as a recording is worse than none.</summary>
    public required string Source { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    /// <summary>How many dimensions were asked, so a question-set change is visible without diffing bodies.</summary>
    public required int AskedDimensions { get; init; }
}

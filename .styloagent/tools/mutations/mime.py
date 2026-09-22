"""Mutation set for the `mime-` lane (StyloMail.Mime — MIME parsing and deterministic evidence).

Each entry is (name, file, old_text, new_text, claims_test):
  * `old_text` must appear in the file EXACTLY ONCE, or the entry is INVALID.
  * `new_text` must change behaviour. A no-op replacement is INVALID, never a verdict.
  * `claims_test` names the test whose *name* asserts this behaviour, so the harness can tell
    CLAIMED (that test went red — the claim is verified) from ELSEWHERE (some other test did, so
    the claim is not verified and is either redundant or untested).

All 19 were previously run from a per-lane shell harness and every one was confirmed to go red;
this file is the port, not a first run. Verdicts from the shared harness are recorded in
`.styloagent/channel/saved-context/mime--context.md`.

`R15` needs two edits (declare the static field, then use it); `old`/`new` are lists of equal-length
pairs for that case. Every mutation in this set now has a reproducible entry — none rest on a
recorded run alone.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "src/StyloMail.Mime"

PROJECT = "tests/StyloMail.Mime.Tests/StyloMail.Mime.Tests.csproj"

MUTATIONS = [
    # --- structural limits: refused before the parser is handed the message -------------------
    ("R1: preflight nesting-depth check disabled",
     SRC / "RawMessagePreflight.cs",
     """if (structure.MaxDepth > limits.MaxMimeDepth)""",
     """if (false && structure.MaxDepth > limits.MaxMimeDepth)""",
     "DeeplyNestedMessage_IsRejectedRatherThanSilentlyTruncated"),

    ("R8: pre-parse part-count gate removed",
     SRC / "RawMessagePreflight.cs",
     """if (structure.PartCount is not null)""",
     """if (false && structure.PartCount is not null)""",
     "MessageWithTooManyParts_IsRejectedBeforeParsing"),

    ("R5: message-size ceiling disabled",
     SRC / "BoundedMimeMessageAnalyzer.cs",
     """if (raw.Length > limits.MaxMessageBytes)""",
     """if (false && raw.Length > limits.MaxMessageBytes)""",
     "MessageLargerThanTheLimit_IsRejectedAsOversize"),

    # --- what is and is not a message ----------------------------------------------------------
    ("R10: header-block validation too loose",
     SRC / "RawMessagePreflight.cs",
     """if (validFieldLines == 0)""",
     """if (false && validFieldLines == 0)""",
     "BytesThatAreNotAMessage_AreRejectedAsMalformed"),

    ("R11: header-block validation too strict",
     SRC / "RawMessagePreflight.cs",
     """private static bool HasFieldName(ReadOnlySpan<byte> line)
    {""",
     """private static bool HasFieldName(ReadOnlySpan<byte> line)
    {
        return false;""",
     "AHeadersOnlyMessage_IsStillAMessage"),

    # --- the analysis view must never carry hidden text or a partial read ----------------------
    ("R2: hidden elements no longer elided from the visible text",
     SRC / "HtmlExtraction.cs",
     """var visible = ToText(elided.Html);""",
     """var visible = ToText(working);""",
     "HiddenTextIsNotReportedAsPartOfTheAnalysisView"),

    ("R9: truncated-boundary path removed from the truncation flag",
     SRC / "BoundedMimeMessageAnalyzer.cs",
     """var truncated = collected.BodyTruncated || preflight.BoundaryUnterminated;""",
     """var truncated = collected.BodyTruncated;""",
     "TruncatedMultipart_IsMarkedTruncatedRatherThanComplete"),

    ("R7: a rejected message is reported as Parsed",
     SRC / "BoundedMimeMessageAnalyzer.cs",
     """Disposition = rejection.Disposition,""",
     """Disposition = MimeParseDisposition.Parsed,""",
     "ARejectedMessage_DoesNotBecomeACleanVerdict"),

    # --- reduced coverage stays explicit and complete ------------------------------------------
    ("R4: coverage reasons collapse to the last one",
     SRC / "BoundedMimeMessageAnalyzer.cs",
     """.. reduced.Select(r => Of("reduced", r)),""",
     """Of("reduced", reduced.Count > 0 ? reduced[^1] : string.Empty),""",
     "EveryReducedCoverageReasonSurvives_NotJustTheLastOne"),

    ("R12: encrypted-container path removed from ContentUnavailable",
     SRC / "PartCollector.cs",
     """ContentUnavailable = unavailable || encryptedContainer,""",
     """ContentUnavailable = unavailable,""",
     "EncryptedAttachment_IsUnavailableAndReducesCoverage"),

    # --- identity, links and attachments --------------------------------------------------------
    ("R3: IDN normalisation removed from address comparison",
     SRC / "UrlTools.cs",
     """: string.Concat(trimmed[..at].ToLowerInvariant(), "@", ToAsciiDomain(trimmed[(at + 1)..]));""",
     """: string.Concat(trimmed[..at].ToLowerInvariant(), "@", trimmed[(at + 1)..].ToLowerInvariant());""",
     "InternationalisedDomain_IsNotReportedAsAnIdentityMismatch"),

    ("R17: every label treated as making a host claim",
     SRC / "LinkExtractor.cs",
     """LabelMakesHostClaim = claimsHost,""",
     """LabelMakesHostClaim = true,""",
     "LabelsThatNameNoDestination_AreNotTreatedAsMismatches"),

    ("R19: label host comparison always reports a mismatch",
     SRC / "LinkExtractor.cs",
     """!string.Equals(TrimWww(labelUrl.Host), TrimWww(targetHost), StringComparison.OrdinalIgnoreCase)""",
     """true""",
     "MatchingLabelAndTarget_IsNotAMismatch"),

    ("R18: octet-stream treated as a specific declared type",
     SRC / "AttachmentTypes.cs",
     """GenericDeclaredType = IsGenericDeclaredType(declared),""",
     """GenericDeclaredType = false,""",
     "AnUninformativeDeclaredType_IsRecordedButNotCountedAsAMismatch"),

    # --- content comparison ---------------------------------------------------------------------
    ("R13: both-parts-required gate for HTML/text removed",
     SRC / "BoundedMimeMessageAnalyzer.cs",
     """var comparable = collected.PlainBodies.Count > 0 &&
                         collected.HtmlBodies.Count > 0 &&""",
     """var comparable =""",
     "AMessageWithOnlyOneRepresentation_IsNotApplicable"),

    ("R16: template skeleton stops normalising numbers",
     SRC / "TextTools.cs",
     """work = NumberLike().Replace(work, " num ");""",
     """// number normalisation disabled by mutation""",
     "SameTemplateWithDifferentFillers_GivesTheSameSkeleton"),

    # --- the adapter's own safety properties -----------------------------------------------------
    ("R6: adapter gains a compiling networking dependency",
     SRC / "BoundedMimeMessageAnalyzer.cs",
     """public sealed class BoundedMimeMessageAnalyzer : IMimeMessageAnalyzer
{""",
     """public sealed class BoundedMimeMessageAnalyzer : IMimeMessageAnalyzer
{
    internal static System.Threading.Tasks.Task<string> ProbeAsync(string url) =>
        new System.Net.Http.HttpClient().GetStringAsync(url);""",
     "TheAdapterReferencesNoNetworkingAssemblies"),

    ("R14: analyzer gains instance state",
     SRC / "BoundedMimeMessageAnalyzer.cs",
     """public sealed class BoundedMimeMessageAnalyzer : IMimeMessageAnalyzer
{""",
     """public sealed class BoundedMimeMessageAnalyzer : IMimeMessageAnalyzer
{
    private readonly List<Evidence> _scratch = [];

    internal int ScratchCount => _scratch.Count;""",
     "TheAnalyzerHoldsNoMutableState_SoOneInstanceMayBeShared"),

    ("R15: the evidence list becomes shared static scratch (classic concurrency bug)",
     SRC / "BoundedMimeMessageAnalyzer.cs",
     ["""    private static readonly AnalysisCoverage EmptyCoverage = new()""",
      """        var evidence = new List<Evidence>();"""],
     ["""    private static readonly List<Evidence> SharedEvidence = [];

    private static readonly AnalysisCoverage EmptyCoverage = new()""",
      """        var evidence = SharedEvidence;
        evidence.Clear();"""],
     "ConcurrentUseProducesExactlyTheSameEvidenceAsSequentialUse"),

    ("R20: a type gains a static (non-readonly) memoisation cache",
     SRC / "UrlTools.cs",
     """    private static readonly Dictionary<char, char> ConfusableMap = BuildConfusables();""",
     """    private static readonly Dictionary<char, char> ConfusableMap = BuildConfusables();

    internal static Dictionary<string, string> HostMemo = new();

    internal static int HostMemoCount => HostMemo.Count;""",
     "NoTypeInTheAdapterHoldsMutableStaticState"),
]

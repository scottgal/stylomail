using System.Reflection;
using System.Runtime.CompilerServices;
using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

/// <summary>
/// Properties that must hold for every input, not just the ones with fixtures.
/// </summary>
public class AdapterGuaranteeTests
{
    private static readonly BoundedMimeMessageAnalyzer Analyzer = new();

    private static readonly string[] AllFixtures =
    [
        "benign-plain.eml",
        "benign-multipart.eml",
        "display-name-mismatch.eml",
        "link-display-mismatch.eml",
        "idn-homograph.eml",
        "attachment-type-mismatch.eml",
        "html-text-disagreement.eml",
        "encrypted-smime.eml",
        "many-parts.eml",
    ];

    [Fact]
    public void TheSameInputAlwaysProducesTheSameEvidence()
    {
        // Replayability is the point: a decision has to be reproducible from the ledger.
        foreach (var fixture in AllFixtures)
        {
            var first = Analyzer.Analyze(FixtureMessage.Request(fixture));
            var second = Analyzer.Analyze(FixtureMessage.Request(fixture));

            Assert.Equal(first.Disposition, second.Disposition);
            Assert.Equal(Render(first), Render(second));
        }
    }

    [Fact]
    public void AnalysingAMessageNeverModifiesTheSuppliedBytes()
    {
        // The bytes are the transport artefact. A proxy that rewrites them invalidates DKIM, so the
        // analysis view has to be built alongside them, never in place.
        foreach (var fixture in AllFixtures)
        {
            var request = FixtureMessage.Request(fixture);
            var before = request.RawMessage.ToArray();

            Analyzer.Analyze(request);

            Assert.Equal(before, request.RawMessage.ToArray());
        }
    }

    [Fact]
    public void EverySignalIsDeterministicOrigin()
    {
        // A model opinion wearing a deterministic label would be given authority it has not earned.
        foreach (var fixture in AllFixtures)
        {
            var result = Analyzer.Analyze(FixtureMessage.Request(fixture));

            Assert.All(result.Evidence, e => Assert.Equal(EvidenceOrigin.Deterministic, e.Origin));
            Assert.All(result.Evidence, e => Assert.Equal(MimeSignals.SourceVersion, e.SourceVersion));
        }
    }

    [Fact]
    public void NoSignalEverCarriesAConfidence()
    {
        // Confidence belongs to the semantic classifier. A deterministic count is a fact, and
        // attaching a confidence to it would imply a distribution that does not exist.
        foreach (var fixture in AllFixtures)
        {
            var result = Analyzer.Analyze(FixtureMessage.Request(fixture));

            Assert.All(result.Evidence, e => Assert.Null(e.Confidence));
        }
    }

    [Fact]
    public void SignalIdentifiersAreUniqueWithinOneMessage()
    {
        // Except for the per-attachment hashes, which are deliberately one signal per attachment.
        foreach (var fixture in AllFixtures)
        {
            var result = Analyzer.Analyze(FixtureMessage.Request(fixture));
            var duplicates = result.Evidence
                .Where(e => e.SignalId != MimeSignals.AttachmentHash)
                .GroupBy(e => e.SignalId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0, $"{fixture} repeated: {string.Join(", ", duplicates)}");
        }
    }

    [Fact]
    public void NoHostileInputThrows()
    {
        // Malformed input is normal. The adapter reports it; it does not throw at the caller.
        string[] hostile =
        [
            "",
            "\r\n",
            "From:",
            "From: <\r\n\r\nbody",
            "Content-Type: multipart/mixed; boundary=\"\"\r\n\r\n--",
            "\0\0\0\0\0\0",
            new string('a', 10000),
            "From: a@b\r\nContent-Type: text/html\r\n\r\n<html><body><div style=\"display:none\">",
            "From: a@b\r\nSubject: 😀😀\r\n\r\n😀",
        ];

        foreach (var input in hostile)
        {
            var result = Analyzer.Analyze(FixtureMessage.FromBytes(SyntheticMessage.Utf8(input)));

            Assert.NotNull(result.Coverage);
            Assert.NotNull(result.Evidence);
            Assert.NotEmpty(result.Evidence);
        }
    }

    [Fact]
    public void TheAdapterReferencesNoNetworkingAssemblies()
    {
        // The MVP safety rule is that nothing here fetches links, loads remote images, executes
        // attachments or resolves redirects. This is the structural check: the adapter cannot open
        // a socket, because it does not reference anything that can.
        string[] forbidden = ["System.Net.Http", "System.Net.Sockets", "System.Net.Mail",
            "System.Net.NetworkInformation", "System.Net.Requests", "System.Net.WebClient"];

        var referenced = typeof(BoundedMimeMessageAnalyzer).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        var offenders = referenced.Where(r => forbidden.Contains(r, StringComparer.Ordinal)).ToList();
        Assert.True(offenders.Count == 0, $"Networking references present: {string.Join(", ", offenders)}");

        // And nothing in the compiled surface names one either, in case it is reached by a type
        // forwarder or a package that pulls it in transitively.
        var assembly = typeof(BoundedMimeMessageAnalyzer).Assembly;
        var referencedTypes = assembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.ToString() ?? string.Empty))
            .Where(n => n.Contains("System.Net.Http", StringComparison.Ordinal) ||
                        n.Contains("System.Net.Sockets", StringComparison.Ordinal));

        Assert.Empty(referencedTypes);
    }

    [Fact]
    public void TheAnalyzerHoldsNoMutableState_SoOneInstanceMayBeShared()
    {
        // Host will hold a single analyzer and call it from whatever thread a message arrives on.
        // That is only safe because this type carries no instance state at all: the only shared
        // data is static and read-only after construction. If someone adds a scratch buffer or a
        // cached list here, this test is the tripwire, sharing would start producing results
        // that depend on what else happened to be in flight.
        var instanceFields = typeof(BoundedMimeMessageAnalyzer)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.True(
            instanceFields.Length == 0,
            "BoundedMimeMessageAnalyzer gained instance state: " +
            string.Join(", ", instanceFields.Select(f => f.Name)) +
            ". A shared instance is no longer safe for concurrent use, either make it stateless " +
            "again, or stop sharing one and say so in the constructor.");
    }

    [Fact]
    public void NoTypeInTheAdapterHoldsMutableStaticState()
    {
        // The instance-field tripwire above is necessary but not sufficient. The likeliest future
        // mistake in a parser is not an instance field, it is a *static* cache: a memoised regex,
        // a reused decode buffer, a lookup table built lazily on first use. That is shared by every
        // thread in the process and would corrupt results under load while looking, from the
        // caller's side, like a message-handling bug somewhere else entirely.
        //
        // Every piece of shared state in this assembly must be static readonly, populated during
        // construction and only read afterwards. Compiler-generated types are excluded: Roslyn
        // emits non-readonly static fields for its own lambda caches, which are not ours.
        var offenders = typeof(BoundedMimeMessageAnalyzer).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .SelectMany(type => type
                .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsLiteral && !field.IsInitOnly)
                .Select(field => $"{type.Name}.{field.Name}"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Mutable static state found: " + string.Join(", ", offenders) +
            ". Anything static is shared by every thread using this adapter, make it readonly " +
            "and populated at construction, or keep it per-call.");
    }

    [Fact]
    public void ConcurrentUseProducesExactlyTheSameEvidenceAsSequentialUse()
    {
        // A shared stateless instance is fine in principle; this is the check that it stays fine
        // in practice. A shared scratch buffer would show up here as results that vary with the
        // number of messages in flight.
        var sequential = AllFixtures.ToDictionary(
            fixture => fixture,
            fixture => Render(Analyzer.Analyze(FixtureMessage.Request(fixture))),
            StringComparer.Ordinal);

        var concurrent = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        Parallel.For(
            0,
            256,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i =>
            {
                var fixture = AllFixtures[i % AllFixtures.Length];
                var rendered = Render(Analyzer.Analyze(FixtureMessage.Request(fixture)));
                concurrent[$"{i}:{fixture}"] = rendered;
            });

        Assert.Equal(256, concurrent.Count);

        foreach (var (key, rendered) in concurrent)
        {
            var fixture = key[(key.IndexOf(':') + 1)..];
            Assert.Equal(sequential[fixture], rendered);
        }
    }

    [Fact]
    public void AMessageIsNeverBothRejectedAndAnalysable()
    {
        foreach (var fixture in AllFixtures)
        {
            var result = Analyzer.Analyze(FixtureMessage.Request(fixture));

            if (result.Disposition == MimeParseDisposition.Parsed)
            {
                Assert.NotNull(result.Message);
                Assert.Null(result.Rejection);
            }
            else
            {
                Assert.Null(result.Message);
                Assert.NotNull(result.Rejection);
            }
        }
    }

    [Fact]
    public void ARejectedMessage_DoesNotBecomeACleanVerdict()
    {
        // The failure mode this guards against is a parser that gives up and reports "nothing
        // found". A rejection says what happened and stays visible in coverage.
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "many-parts.eml",
            mailFrom: "bulk@example-bulk.test",
            limits: MimeParseLimits.Default with { MaxParts = 10 }));

        Assert.NotEqual(MimeParseDisposition.Parsed, result.Disposition);
        Assert.True(result.Coverage.ParserLimitExceeded);
        Assert.Equal(EvidenceAvailability.ReducedCoverage, result.Signal(MimeSignals.AnalysisCoverage).Availability);
        Assert.Contains("not-analysed", result.Signal(MimeSignals.AnalysisCoverage).AttributesNamed("reduced"));
    }

    [Fact]
    public void AnInjectedClock_MakesTheTimestampsReplayable()
    {
        // Wall-clock time in a decision record makes replay impossible to compare. Every timestamp
        // here comes from the injected clock, never from DateTimeOffset.Now.
        var frozen = new DateTimeOffset(2026, 9, 22, 9, 30, 0, TimeSpan.Zero);

        var result = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com") with
        {
            TimeProvider = new FixedTimeProvider(frozen),
        });

        Assert.All(result.Evidence, e => Assert.Equal(frozen, e.ObservedAt));
    }

    [Fact]
    public void AMessageCannotChangeItsOwnEnvelope()
    {
        // The message names a different sender in every header it controls. The envelope the caller
        // supplied must come back unchanged: identity comes from the transport boundary, and only
        // the untrusted Message-ID is filled in from the content.
        var bytes = SyntheticMessage.Utf8(
            """
            From: Someone Else <somewhere@else.test>
            Sender: another@else.test
            Return-Path: <return@else.test>
            To: victim@else.test
            Subject: Synthetic
            Date: Mon, 22 Sep 2026 09:15:00 +0100
            Message-ID: <claims@else.test>
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            body
            """);

        var request = FixtureMessage.FromBytes(bytes);
        var result = Analyzer.Analyze(request);

        Assert.Equal(request.Envelope.MailFrom, result.Message!.Envelope.MailFrom);
        Assert.Equal(request.Envelope.RcptTo, result.Message.Envelope.RcptTo);
        Assert.Equal(request.Envelope.TrustedPrincipalId, result.Message.Envelope.TrustedPrincipalId);
        Assert.Equal(request.Envelope.InternalMessageId, result.Message.Envelope.InternalMessageId);

        // Only the untrusted diagnostic field is filled from the content, and it stays labelled.
        Assert.Equal("claims@else.test", result.Message.Envelope.UntrustedMessageIdHeader);
    }

    private static string Render(MimeAnalysisResult result)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var evidence in result.Evidence)
        {
            builder.Append(evidence.SignalId)
                .Append('|').Append(evidence.Availability)
                .Append('|').Append(evidence.Value?.ToString("R"))
                .Append('|').Append(evidence.ObservedScope);

            if (evidence.Attributes is not null)
            {
                // Sorted by name and value so the comparison is order-insensitive between runs
                // while still rendering every repeated entry.
                foreach (var attribute in evidence.Attributes
                             .OrderBy(a => a.Name, StringComparer.Ordinal)
                             .ThenBy(a => a.Value, StringComparer.Ordinal))
                {
                    builder.Append('|').Append(attribute.Name).Append('=').Append(attribute.Value);
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}

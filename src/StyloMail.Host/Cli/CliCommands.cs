using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Assessors;
using StyloMail.Host.Hosting;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Host.Submissions;
using StyloMail.Mime;
using StyloMail.Queue;

namespace StyloMail.Host.Cli;

/// <summary>The commands the standalone executable understands.</summary>
public abstract record CliCommand;

public sealed record ServeCommand : CliCommand;

public sealed record AssessCommand(string Path, string? TenantId, bool UseSemanticProvider, bool AsJson) : CliCommand;

public sealed record ReplayCommand(string Directory, string? TenantId, bool AsJson) : CliCommand;

public sealed record QuarantineListCommand(string TenantId, bool AsJson) : CliCommand;

public sealed record QuarantineReleaseCommand(string TenantId, string QueueId, string DecidedBy) : CliCommand;

public sealed record ProfilesInspectCommand(string TenantId, bool AsJson) : CliCommand;

/// <summary>
/// The CLI's commands.
/// </summary>
/// <remarks>
/// Exit codes are meaningful: <c>0</c> success, <c>2</c> bad input or missing file, <c>3</c> a
/// requested capability is unavailable. A CLI that reported failure as success would be trusted by
/// scripts, which is the worst place to be wrong.
/// </remarks>
public static class CliCommands
{
    private const int Ok = 0;
    private const int BadInput = 2;
    private const int CapabilityUnavailable = 3;

    /// <summary>
    /// Replay runs against a fixed instant so two runs over the same fixtures produce the same
    /// answer. Without that, the command could not be used to compare policy versions.
    /// </summary>
    public static readonly DateTimeOffset ReplayInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<int> AssessAsync(
        IServiceProvider services,
        AssessCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(command.Path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"error: could not read '{command.Path}'.");
            return BadInput;
        }

        var tenantId = command.TenantId ?? "cli";
        var analyzer = services.GetRequiredService<IMimeMessageAnalyzer>();
        var envelope = BuildEnvelope(tenantId, bytes);

        var result = analyzer.Analyze(new MimeAnalysisRequest
        {
            Envelope = envelope,
            RawMessage = bytes,
            TimeProvider = TimeProvider.System,
        });

        if (!result.IsAnalysable || result.Message is null)
        {
            await output.WriteLineAsync($"disposition: {result.Disposition.ToString().ToLowerInvariant()}");
            await output.WriteLineAsync($"reason: {result.Rejection?.Reason ?? "unspecified"}");
            return BadInput;
        }

        if (!command.UseSemanticProvider)
        {
            // Constraint: CLI assessment must not silently transmit private content externally.
            // The local path runs by default; the provider path is opted into, per invocation.
            await WriteLocalAsync(output, command, result, envelope);

            await output.WriteLineAsync(
                "semantic: skipped — no semantic provider requested. Provider usage is opt-in " +
                "(--semantic) and is not configured by default, so message content was not " +
                "transmitted anywhere.");
            return Ok;
        }

        var assessor = services.GetRequiredService<IMailAssessor>();
        if (assessor is UnavailableMailAssessor)
        {
            // Never fall back to the local result and call it an assessment.
            await output.WriteLineAsync(
                "error: no mail assessor is configured on this host, so a semantic assessment " +
                "cannot be produced. Refusing rather than reporting a local-only result.");
            return CapabilityUnavailable;
        }

        var assessment = await assessor.AssessAsync(
            result.Message,
            new AssessmentContext
            {
                TenantId = tenantId,
                ShadowMode = false,
                AssessmentOnly = true,
                CorrelationId = $"cli_{Guid.NewGuid():N}",
                TimeProvider = TimeProvider.System,
            },
            cancellationToken);

        await output.WriteLineAsync(
            JsonSerializer.Serialize(
                new
                {
                    file = Path.GetFileName(command.Path),
                    action = assessment.Action.ToString(),
                    riskIndex = assessment.RiskIndex,
                    reasons = assessment.Reasons.Select(r => r.Message).ToArray(),
                    versions = new
                    {
                        policy = assessment.Versions.PolicyVersion,
                        classifier = assessment.Versions.ClassifierModelVersion,
                    },
                },
                HostJson.Options));

        return Ok;
    }

    /// <summary>
    /// Assesses every fixture in a directory with a fixed clock and no provider.
    /// </summary>
    /// <remarks>
    /// No provider call is made on this path at all. A replay directory is a corpus of real
    /// messages, and running it repeatedly against a hosted classifier would be both a spend and a
    /// quiet exfiltration of whatever the fixtures contain.
    /// </remarks>
    public static async Task<int> ReplayAsync(
        IServiceProvider services,
        ReplayCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(command.Directory))
        {
            await output.WriteLineAsync($"error: '{command.Directory}' is not a directory.");
            return BadInput;
        }

        var analyzer = services.GetRequiredService<IMimeMessageAnalyzer>();
        var clock = new FixedTimeProvider(ReplayInstant);
        var tenantId = command.TenantId ?? "cli";

        var files = Directory.EnumerateFiles(command.Directory, "*.eml")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            var result = analyzer.Analyze(new MimeAnalysisRequest
            {
                Envelope = BuildEnvelope(tenantId, bytes),
                RawMessage = bytes,
                TimeProvider = clock,
            });

            var line = JsonSerializer.Serialize(
                new
                {
                    file = Path.GetFileName(file),
                    disposition = result.Disposition.ToString(),
                    analysable = result.IsAnalysable,
                    evidenceCount = result.Evidence.Count,
                    observedAt = ReplayInstant,
                },
                HostJson.Options);

            await output.WriteLineAsync(line);
        }

        await output.WriteLineAsync($"replayed {files.Count} fixture(s); no provider calls were made.");
        return Ok;
    }

    public static async Task<int> QuarantineListAsync(
        IServiceProvider services,
        QuarantineListCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var store = services.GetRequiredService<QueueStore>();

        try
        {
            // Through the queue's own listing rather than a hand-written query against its tables.
            // The earlier version read `queue_item`/`queue_recipient` directly, which made the host
            // depend on a schema it does not own and would have broken silently the first time that
            // schema moved. Listing is now a queue operation, so it belongs to the queue.
            var page = await store
                .ListAsync(
                    new QueueListingQuery
                    {
                        TenantId = command.TenantId,
                        Filter = QueueListingFilter.Quarantined,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var rows = page.Items
                .SelectMany(item => item.Recipients
                    .Where(recipient => recipient.State == DeliveryState.Quarantined)
                    .Select(recipient => new
                    {
                        queueId = item.QueueId,
                        mailFrom = item.Envelope.MailFrom,
                        recipient = recipient.Recipient,
                        createdAt = item.CreatedAt,
                    }))
                .ToList();

            await output.WriteLineAsync(JsonSerializer.Serialize(rows, HostJson.Options));

            if (page.HasMore)
            {
                // Never silently truncate: an operator reading a quarantine list has to know when
                // they are looking at a page rather than the whole set.
                await output.WriteLineAsync(
                    $"more quarantined items exist; pass --after {page.NextCursor} for the next page.");
            }

            return Ok;
        }
        catch (ArgumentException)
        {
            // A malformed cursor throws rather than silently restarting the listing.
            await output.WriteLineAsync("error: the queue rejected the listing request.");
            return BadInput;
        }
    }

    public static async Task<int> QuarantineReleaseAsync(
        IServiceProvider services,
        QuarantineReleaseCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var intake = services.GetRequiredService<ISubmissionIntake>();

        try
        {
            var released = await intake.ResolveQuarantineAsync(
                command.QueueId, command.TenantId, command.DecidedBy, cancellationToken);

            // A release is always attributed. Without --by there is no one to attribute it to, so
            // the caller must supply one rather than have the CLI invent an identity.
            await output.WriteLineAsync(
                released
                    ? $"released {command.QueueId} as {command.DecidedBy}."
                    : $"{command.QueueId} was not quarantined; nothing to release.");

            return released ? Ok : BadInput;
        }
        catch (StorageUnavailableException)
        {
            await output.WriteLineAsync("error: the release could not be durably recorded.");
            return BadInput;
        }
    }

    public static async Task<int> ProfilesInspectAsync(
        IServiceProvider services,
        ProfilesInspectCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var database = services.GetRequiredService<HostDatabase>();

        try
        {
            using var connection = database.Open();
            using var cmd = connection.CreateCommand();

            // Keys are tenant-scoped keyed hashes, never raw addresses — so this listing is safe to
            // print and an operator still cannot read an address out of it.
            cmd.CommandText =
                """
                SELECT profile_scope, profile_key, direction, trusted_support, baseline_version,
                       regime_id, baseline_frozen, observed_attempts, observed_recipients, last_observed_at
                FROM profiles
                WHERE tenant_id = $tenant
                ORDER BY profile_scope, profile_key;
                """;
            cmd.Parameters.AddWithValue("$tenant", command.TenantId);

            var rows = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new
                {
                    scope = reader.GetString(0),
                    key = reader.GetString(1),
                    direction = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                    trustedSupport = reader.GetInt32(3),
                    baselineVersion = reader.GetInt32(4),
                    regimeId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    baselineFrozen = reader.GetInt32(6) != 0,
                    observedAttempts = reader.GetInt64(7),
                    observedRecipients = reader.GetInt64(8),
                    lastObservedAt = reader.IsDBNull(9) ? null : reader.GetString(9),
                });
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(rows, HostJson.Options));
            return Ok;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            await output.WriteLineAsync("error: the profile store could not be read.");
            return BadInput;
        }
    }

    private static async Task WriteLocalAsync(
        TextWriter output,
        AssessCommand command,
        MimeAnalysisResult result,
        MailEnvelope envelope)
    {
        if (command.AsJson)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    file = Path.GetFileName(command.Path),
                    disposition = result.Disposition.ToString(),
                    mimeDigest = envelope.MimeDigest,
                    coverage = new
                    {
                        result.Coverage.BodyParsed,
                        result.Coverage.HtmlPresent,
                        result.Coverage.HasAttachments,
                        result.Coverage.HtmlTextDisagreement,
                        result.Coverage.ParserLimitExceeded,
                        result.Coverage.ContentEncrypted,
                        result.Coverage.Truncated,
                    },
                    evidence = result.Evidence.Select(e => new
                    {
                        signalId = e.SignalId,
                        availability = e.Availability.ToString(),
                        value = e.Value,
                        sourceVersion = e.SourceVersion,
                    }).ToArray(),
                },
                HostJson.Options));
            return;
        }

        await output.WriteLineAsync($"{Path.GetFileName(command.Path)}");
        await output.WriteLineAsync($"  disposition   {result.Disposition.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync($"  digest        {envelope.MimeDigest}");
        await output.WriteLineAsync(
            $"  coverage      body={result.Coverage.BodyParsed} html={result.Coverage.HtmlPresent} " +
            $"attachments={result.Coverage.HasAttachments} truncated={result.Coverage.Truncated}");
        await output.WriteLineAsync($"  evidence      {result.Evidence.Count} deterministic signal(s)");

        foreach (var evidence in result.Evidence)
        {
            await output.WriteLineAsync(
                $"    {evidence.SignalId}  {evidence.Availability}  {evidence.Value?.ToString("0.###") ?? "-"}");
        }
    }

    private static MailEnvelope BuildEnvelope(string tenantId, byte[] bytes) => new()
    {
        InternalMessageId = $"cli_{Guid.NewGuid():N}",
        TenantId = tenantId,
        Direction = MailDirection.Inbound,
        TrustedPrincipalId = "cli",
        MailFrom = string.Empty,
        RcptTo = [],
        ReceivedAt = TimeProvider.System.GetUtcNow(),
        MimeDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
        // The CLI analyses a file in place; nothing was spooled, and the reference says so.
        PayloadReference = "file://local",
    };
}

/// <summary>A clock fixed at one instant, so replay output cannot vary with the wall clock.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}

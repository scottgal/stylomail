using System.Text;
using StyloMail.Core;
using StyloMail.Persistence;
using StyloMail.Queue;

namespace StyloMail.Queue.Tests;

/// <summary>
/// A clock the test drives by hand.
/// </summary>
/// <remarks>
/// Every time-dependent behaviour in the queue — lease expiry, retry backoff, hold windows, message
/// lifetime — is tested by moving this clock, never by waiting. A suite that slept for real minutes
/// would be slow enough that nobody would write the tests that matter.
/// </remarks>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    public void AdvanceMinutes(double minutes) => Advance(TimeSpan.FromMinutes(minutes));
}

/// <summary>An isolated queue: its own database, its own spool, its own clock.</summary>
internal sealed class QueueHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public QueueHarness(Func<TestClock, QueueOptions>? configure = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "stylomail-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        Clock = new TestClock(Start);
        Options = configure?.Invoke(Clock) ?? new QueueOptions { TimeProvider = Clock };

        SpoolRoot = Path.Combine(_root, "spool");
        DatabasePath = Path.Combine(_root, "queue.db");

        Connections = new SqliteConnectionFactory(DatabasePath);
        Spool = new SpoolStore(SpoolRoot);
        Store = new QueueStore(Connections, Spool, Options);
    }

    public TestClock Clock { get; }

    public QueueOptions Options { get; }

    public string SpoolRoot { get; }

    public string DatabasePath { get; }

    public SqliteConnectionFactory Connections { get; }

    public SpoolStore Spool { get; }

    public QueueStore Store { get; }

    /// <summary>
    /// A second store over the same database, with different options.
    /// </summary>
    /// <remarks>
    /// Used to model an operator changing a bound, or a different worker process picking up the
    /// same queue — the state has to behave correctly for a reader that did not write it.
    /// </remarks>
    public QueueStore Reopen(Func<TestClock, QueueOptions> configure)
        => new(Connections, new SpoolStore(SpoolRoot), configure(Clock));

    public static QueueSubmission Submission(
        string tenantId = "acme",
        string[]? recipients = null,
        string? idempotencyKey = null,
        string? mimeDigest = null,
        int? hopCount = 0,
        int payloadBytes = 64,
        DeliveryState state = DeliveryState.Queued,
        DateTimeOffset? reEvaluateBy = null)
        => new()
        {
            TenantId = tenantId,
            InternalMessageId = "msg-" + Guid.NewGuid().ToString("N"),
            Direction = MailDirection.Outbound,
            TrustedPrincipalId = "principal-1",
            MailFrom = "sender@acme.test",
            MimeDigest = mimeDigest ?? "digest-" + Guid.NewGuid().ToString("N"),
            Payload = Encoding.UTF8.GetBytes(new string('x', payloadBytes)),
            Recipients = [.. (recipients ?? ["rcpt@example.test"]).Select(r => new RecipientAdmission
            {
                Recipient = r,
                State = state,
                ReEvaluateBy = reEvaluateBy,
            })],
            IdempotencyKey = idempotencyKey,
            HopCount = hopCount,
        };

    /// <summary>Accepts a submission and asserts it really was accepted.</summary>
    public async Task<string> AcceptAsync(QueueSubmission submission)
    {
        var result = await Store.AcceptAsync(submission);
        Assert.True(result.IsAccepted, $"Expected acceptance but got {result.Admission}: {result.Detail}");
        return result.QueueId!;
    }

    public static QueueRecipientState By(QueueItem item, string recipient)
        => item.Recipients.Single(r =>
            string.Equals(r.Recipient, recipient, StringComparison.OrdinalIgnoreCase));

    public static DeliveryReport Delivered(string workerId, params string[] recipients)
        => new()
        {
            WorkerId = workerId,
            Recipients = [.. recipients.Select(r => new RecipientDeliveryResult
            {
                Recipient = r,
                Outcome = DeliveryAttemptOutcome.Delivered,
            })],
        };

    public static DeliveryReport TemporaryFailure(string workerId, params string[] recipients)
        => new()
        {
            WorkerId = workerId,
            Recipients = [.. recipients.Select(r => new RecipientDeliveryResult
            {
                Recipient = r,
                Outcome = DeliveryAttemptOutcome.TemporaryFailure,
                Detail = "421 upstream busy",
            })],
        };

    public string PayloadPath(string queueId, string tenantId = "acme")
        => Path.Combine(SpoolRoot, tenantId, $"{queueId}.eml");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort. A leftover temp directory is not worth failing a test over.
        }
    }
}

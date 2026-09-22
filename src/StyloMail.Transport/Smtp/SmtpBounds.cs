namespace StyloMail.Transport.Smtp;

/// <summary>
/// Every hard limit the transport enforces while it is talking to a peer.
/// </summary>
/// <remarks>
/// <b>These are features, not tuning.</b> A transport that can be made to hold unbounded resources
/// is a denial-of-service vector against the mail it exists to protect: an SMTP peer that never
/// sends a line terminator, or that streams a million-line reply, can pin a worker and its memory
/// forever unless a bound stops it. Each limit below answers a specific way that happens — see the
/// individual remarks.
///
/// <para>
/// <b>Time is injected.</b> Nothing here reads the wall clock; the reader takes a
/// <see cref="TimeProvider"/> so a test can drive a timeout instead of waiting for one.
/// </para>
/// </remarks>
public sealed record SmtpBounds
{
    /// <summary>How long to wait for the TCP connection (and any TLS handshake on it) to establish.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to wait for the server's opening greeting.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CommandTimeout"/> because the greeting is server-initiated: a host
    /// that accepts the TCP connection and then says nothing has not stalled our command, it has
    /// stalled before we sent one, and the two are worth telling apart in an incident.
    /// </remarks>
    public TimeSpan GreetingTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for a reply to a command we sent.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait while a message body is being written and acknowledged.
    /// </summary>
    /// <remarks>
    /// Deliberately much longer than a command: this covers writing up to
    /// <see cref="MaxMessageBytes"/> to a possibly slow upstream <em>and</em> waiting for its final
    /// response, which may include a synchronous virus or content scan. It is still a bound.
    /// </remarks>
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum bytes in one reply line.
    /// </summary>
    /// <remarks>
    /// RFC 5321 caps a reply line at 1000 bytes including the CRLF. The default is deliberately
    /// more generous than the RFC because real servers exceed it, but it is still a cap: without
    /// one, a peer that never sends <c>LF</c> makes the reader allocate without limit.
    /// </remarks>
    public int MaxReplyLineBytes { get; init; } = 2048;

    /// <summary>Maximum continuation lines in one reply. Without this, EHLO is an unbounded vector.</summary>
    public int MaxReplyLines { get; init; } = 32;

    /// <summary>Maximum total bytes across all lines of one reply.</summary>
    public int MaxReplyBytes { get; init; } = 16 * 1024;

    /// <summary>Maximum bytes in a command we send, so a hostile banner cannot steer us into echoing back megabytes.</summary>
    public int MaxCommandBytes { get; init; } = 1024;

    /// <summary>
    /// Maximum size of a message we will hand to an upstream.
    /// </summary>
    /// <remarks>
    /// Checked <em>before</em> the transaction starts, never during. A message that is refused
    /// mid-DATA has already been partially transferred, and the upstream's own size limit is the
    /// thing that should have caught it — this bound exists so we do not open a transfer we
    /// already know cannot finish.
    /// </remarks>
    public long MaxMessageBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Maximum recipients in one delivery request.</summary>
    public int MaxRecipients { get; init; } = 100;

    /// <summary>
    /// Maximum SMTP connections this instance will hold open at once.
    /// </summary>
    /// <remarks>
    /// Bounds file descriptors and the upstream's per-client session limit. Exceeding it waits
    /// rather than failing, so a burst queues instead of erroring.
    /// </remarks>
    public int MaxConcurrentConnections { get; init; } = 4;

    /// <summary>Validates the bounds. Called by the types that consume them.</summary>
    internal void Validate()
    {
        Positive(ConnectTimeout, nameof(ConnectTimeout));
        Positive(GreetingTimeout, nameof(GreetingTimeout));
        Positive(CommandTimeout, nameof(CommandTimeout));
        Positive(DataTimeout, nameof(DataTimeout));

        AtLeastOne(MaxReplyLineBytes, nameof(MaxReplyLineBytes));
        AtLeastOne(MaxReplyLines, nameof(MaxReplyLines));
        AtLeastOne(MaxReplyBytes, nameof(MaxReplyBytes));
        AtLeastOne(MaxCommandBytes, nameof(MaxCommandBytes));
        AtLeastOne(MaxRecipients, nameof(MaxRecipients));
        AtLeastOne(MaxConcurrentConnections, nameof(MaxConcurrentConnections));

        if (MaxMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes), MaxMessageBytes, "Must be positive.");
        }

        // A single line must fit inside the whole-reply budget, or a one-line reply would be
        // rejected by the total cap and the error would name the wrong limit.
        if (MaxReplyLineBytes > MaxReplyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxReplyLineBytes),
                MaxReplyLineBytes,
                "A reply line must fit within MaxReplyBytes, otherwise the total cap rejects single-line replies.");
        }
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be positive.");
        }
    }

    private static void AtLeastOne(int value, string name)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be at least 1.");
        }
    }
}

using System.Text;
using Microsoft.Extensions.Time.Testing;
using StyloMail.Transport.Smtp;
using StyloMail.Transport.Tests.Support;

namespace StyloMail.Transport.Tests;

/// <summary>
/// The reply reader is where a hostile or broken peer turns into a hung worker, so every bound it
/// enforces is exercised directly rather than only through a happy-path delivery.
/// </summary>
public sealed class SmtpReplyReaderTests
{
    private static SmtpReplyReader Reader(
        string wire,
        SmtpBounds? bounds = null,
        TimeProvider? clock = null) =>
        new(
            new MemoryStream(Encoding.UTF8.GetBytes(wire)),
            bounds ?? new SmtpBounds(),
            clock ?? TimeProvider.System);

    private static SmtpBounds Tight(
        int? lineBytes = null,
        int? lines = null,
        int? totalBytes = null) => new()
        {
            MaxReplyLineBytes = lineBytes ?? 2048,
            MaxReplyLines = lines ?? 32,
            MaxReplyBytes = totalBytes ?? 16 * 1024,
        };

    [Fact]
    public async Task SingleLineReply_ParsesCodeAndText()
    {
        var reply = await Reader("250 OK\r\n").ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(250, reply.Code);
        Assert.True(reply.IsPositive);
        Assert.Equal("OK", reply.Text);
        Assert.Null(reply.EnhancedStatusCode);
    }

    [Fact]
    public async Task MultiLineReply_IsReassembledRatherThanTruncatedAtTheFirstLine()
    {
        // The bug this guards: taking the first line as the reply, which would silently drop every
        // capability after the first and is exactly how a STARTTLS advertisement gets missed.
        const string Wire = "250-mail.example.com at your service\r\n250-SIZE 10485760\r\n250-STARTTLS\r\n250 AUTH PLAIN LOGIN\r\n";

        var reply = await Reader(Wire).ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(250, reply.Code);
        Assert.Equal(4, reply.Lines.Count);
        Assert.Contains("STARTTLS", reply.Lines[2], StringComparison.Ordinal);
        Assert.Equal("AUTH PLAIN LOGIN", reply.Lines[3]);
    }

    [Fact]
    public async Task EnhancedStatusCode_IsExtractedFromTheReplyText()
    {
        var reply = await Reader("550 5.7.1 Relay access denied\r\n")
            .ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(550, reply.Code);
        Assert.Equal("5.7.1", reply.EnhancedStatusCode);
        Assert.True(reply.IsPermanentNegative);
    }

    [Fact]
    public async Task EnhancedStatusCode_FindsTheCodeOnALaterContinuationLine()
    {
        var reply = await Reader("550-some prose\r\n550 5.1.1 No such user\r\n")
            .ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("5.1.1", reply.EnhancedStatusCode);
    }

    [Fact]
    public async Task NonEnhancedText_IsNotMistakenForAnEnhancedCode()
    {
        var reply = await Reader("250 1.2.3.4 is not a status code\r\n")
            .ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // Four segments is a version string, not an RFC 3463 code. Reporting it as one would put a
        // fabricated value in the ledger.
        Assert.Null(reply.EnhancedStatusCode);
    }

    [Fact]
    public async Task BareLineFeed_IsAccepted()
    {
        var reply = await Reader("220 hello\n").ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(220, reply.Code);
        Assert.Equal("hello", reply.Text);
    }

    [Fact]
    public async Task TwoRepliesInOnePacket_AreReadSeparately()
    {
        // Buffering correctness: a single socket read can carry more than one reply, and a reader
        // that discarded its buffer between calls would lose the second.
        var reader = Reader("220 hello\r\n250 OK\r\n");

        var greeting = await reader.ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = await reader.ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(220, greeting.Code);
        Assert.Equal(250, second.Code);
    }

    [Fact]
    public async Task CodeChangeInsideOneReply_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Reader("250-First\r\n251 Second\r\n")
                .ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());

        Assert.Contains("changed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverlongLine_IsRefusedRatherThanBufferedWithoutLimit()
    {
        var payload = new string('x', 500);
        var ex = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Reader($"{payload}\r\n", Tight(lineBytes: 128, totalBytes: 8192))
                .ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());

        Assert.Contains("128", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooManyContinuationLines_IsRefused()
    {
        var wire = string.Concat(Enumerable.Repeat("250-continued\r\n", 10));
        var ex = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Reader(wire, Tight(lines: 3, totalBytes: 8192))
                .ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());

        Assert.Contains("3-line budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TotalByteBudget_IsRefusedAcrossManyLines()
    {
        // Each line fits the 64-byte per-line budget; together they do not fit the total budget.
        var wire = "250-" + new string('a', 40) + "\r\n"
            + "250 " + new string('b', 40) + "\r\n";
        var ex = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Reader(wire, Tight(lineBytes: 64, totalBytes: 64))
                .ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());

        Assert.Contains("64-byte budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnclassifiableReplyCode_IsRefusedRatherThanGuessedAt()
    {
        var ex = await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Reader("100 you may proceed\r\n")
                .ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());

        Assert.Contains("outside 200-599", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("250OK\r\n")]
    [InlineData("25 OK\r\n")]
    [InlineData("OK\r\n")]
    [InlineData("9999 weird\r\n")]
    public async Task MalformedReplyLine_IsRefused(string wire)
    {
        await Assert.ThrowsAsync<SmtpProtocolException>(
            () => Reader(wire).ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CleanEofBeforeAnyReply_ReportsConnectionLost()
    {
        await Assert.ThrowsAsync<SmtpConnectionLostException>(
            () => Reader(string.Empty).ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task EofPartWayThroughAReply_ReportsConnectionLost()
    {
        // A line with no terminator at all: the loss happened inside a line, which is reported as
        // such. The between-lines case is covered by the continuation test below.
        var ex = await Assert.ThrowsAsync<SmtpConnectionLostException>(
            () => Reader("250-partial").ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());

        Assert.Contains("part-way through", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EofImmediatelyAfterAContinuationLine_ReportsConnectionLost()
    {
        // The dangerous shape: we have already been told a capability list is coming and it stopped
        // half way. Reporting the partial list as a complete reply is how STARTTLS gets missed.
        await Assert.ThrowsAsync<SmtpConnectionLostException>(
            () => Reader("250-STARTTLS\r\n").ReadReplyAsync(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SilentPeer_TimesOutOnTheInjectedClock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var stream = new BlockingStream();
        var reader = new SmtpReplyReader(stream, new SmtpBounds(), clock);

        var pending = reader.ReadReplyAsync(TimeSpan.FromSeconds(30), CancellationToken.None).AsTask();

        await stream.EnteredRead;

        // No wall-clock waiting: the read is provably outstanding and the clock is moved by hand.
        clock.Advance(TimeSpan.FromSeconds(31));

        await Assert.ThrowsAsync<SmtpTimeoutException>(() => pending);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsATimeout()
    {
        // These are different facts. A timeout means the peer is silent; cancellation means we are
        // shutting down. Conflating them would make a graceful drain look like an upstream incident.
        var stream = new BlockingStream();
        var reader = new SmtpReplyReader(stream, new SmtpBounds(), TimeProvider.System);

        using var cts = new CancellationTokenSource();
        var pending = reader.ReadReplyAsync(TimeSpan.FromMinutes(5), cts.Token).AsTask();

        await stream.EnteredRead;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}

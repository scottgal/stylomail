using System.IO.Pipelines;
using System.Text;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Tests.Support;

/// <summary>
/// An in-memory duplex channel: one end is given to the code under test, the other to the test.
/// </summary>
/// <remarks>
/// <b>No socket appears anywhere in this test project.</b> The brief requires that, and it is also
/// the only way to test the parts that matter: the interesting cases are a backend that refuses a
/// credential, a provider that never replies, and a client that sends half a command, none of which
/// a real server will perform on request.
///
/// <para>
/// Built on <see cref="Pipe"/> rather than a memory stream because the relay's behaviour depends on
/// reads that return *later*, not on reads that return bytes. A memory stream at end-of-stream makes
/// every session end immediately, which would quietly turn every relay assertion into a race that
/// passes for the wrong reason.
/// </para>
/// </remarks>
internal sealed class PipeDuplex : IDuplexChannel
{
    private readonly Pipe _toSession = new();
    private readonly Pipe _fromSession = new();

    public PipeDuplex(string description = "test") => Description = description;

    public Stream Input => _toSession.Reader.AsStream();

    public Stream Output => _fromSession.Writer.AsStream();

    public string Description { get; }

    /// <summary>The test's read end, what the code under test wrote.</summary>
    internal Stream PeerInput => _fromSession.Reader.AsStream();

    /// <summary>The test's write end, what the code under test will read.</summary>
    internal Stream PeerOutput => _toSession.Writer.AsStream();

    /// <summary>Writes text to the session, as a peer would.</summary>
    internal async Task SendAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await PeerOutput.WriteAsync(bytes);
        await PeerOutput.FlushAsync();
    }

    /// <summary>Writes raw bytes to the session, bypassing any text encoding.</summary>
    internal async Task SendRawAsync(byte[] bytes)
    {
        await PeerOutput.WriteAsync(bytes);
        await PeerOutput.FlushAsync();
    }

    /// <summary>Closes the peer's write end, so the session sees end-of-stream.</summary>
    internal async Task ClosePeerWriteAsync() => await PeerOutput.DisposeAsync();

    /// <summary>Reads whatever the session has written so far.</summary>
    internal async Task<string> ReadAvailableAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        var builder = new StringBuilder();

        while (true)
        {
            int read;
            try
            {
                read = await PeerInput.ReadAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));

            // Return as soon as nothing more is immediately available, so a caller asserting on the
            // greeting is not blocked waiting for a reply that comes later.
            if (!PeerInput.CanRead)
            {
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Reads one CRLF-terminated line from the session.</summary>
    internal async Task<string> ReadLineAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var builder = new StringBuilder();
        var one = new byte[1];

        while (true)
        {
            var read = await PeerInput.ReadAsync(one, cts.Token);
            if (read == 0)
            {
                break;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            if (one[0] != (byte)'\r')
            {
                builder.Append((char)one[0]);
            }
        }

        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _toSession.Reader.CompleteAsync().ConfigureAwait(false);
        await _fromSession.Writer.CompleteAsync().ConfigureAwait(false);
        await _toSession.Writer.CompleteAsync().ConfigureAwait(false);
        await _fromSession.Reader.CompleteAsync().ConfigureAwait(false);
    }
}

using System.Text;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Tests.Support;

/// <summary>
/// A mail provider that exists only in memory.
/// </summary>
/// <remarks>
/// Plays the backend half of the dialogue: it greets, records whatever authentication command it is
/// sent, and answers according to how the test configured it. Nothing here reaches a network, and
/// no Google account is involved anywhere in this test project.
///
/// <para>
/// <see cref="OpenCount"/> is the load-bearing member. "A revoked credential fails closed rather
/// than retrying silently" is a claim about <em>how many times</em> we attempted, and it can only be
/// asserted if the fake counts. A fake that simply refused would let a retry loop pass.
/// </para>
/// </remarks>
internal sealed class FakeBackendTransport : IBackendTransport
{
    private int _openCount;

    /// <summary>How many times a connection was opened. The retry assertion.</summary>
    public int OpenCount => Volatile.Read(ref _openCount);

    /// <summary>Whether the backend accepts the credential it is presented.</summary>
    public bool AcceptCredential { get; set; } = true;

    /// <summary>The authentication command the backend received, verbatim. Never asserted on for secrets.</summary>
    public List<string> ReceivedCommands { get; } = [];

    /// <summary>The backend side of the most recent channel, for scripting relay-phase bytes.</summary>
    public PipeDuplex? Channel { get; private set; }

    /// <summary>The SASL payload the backend was sent, decoded. Used to assert framing, not to log.</summary>
    public string? LastAuthArgument { get; private set; }

    public ValueTask<IDuplexChannel> OpenAsync(
        BackendConnectionRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _openCount);

        var channel = new PipeDuplex($"fake-backend:{request.Protocol}");
        Channel = channel;

        // The backend speaks first, as every one of these protocols requires.
        _ = PlayBackendAsync(channel, request.Protocol, cancellationToken);

        return ValueTask.FromResult<IDuplexChannel>(channel);
    }

    private async Task PlayBackendAsync(PipeDuplex channel, BackendProtocol protocol, CancellationToken cancellationToken)
    {
        try
        {
            var greeting = protocol switch
            {
                BackendProtocol.Imap => "* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN AUTH=XOAUTH2] Gmail ready\r\n",
                BackendProtocol.Pop3 => "+OK Gmail POP3 ready\r\n",
                _ => "+OK ready\r\n",
            };

            await channel.SendAsync(greeting);

            if (protocol == BackendProtocol.Imap)
            {
                await PlayImapAsync(channel, cancellationToken);
            }
            else
            {
                await PlayPop3Async(channel, cancellationToken);
            }
        }
        catch (Exception)
        {
            // The test harness tears channels down mid-dialogue routinely; a backend that talks to
            // a closed pipe is the expected shape of that, not a failure worth surfacing.
        }
    }

    private async Task PlayImapAsync(PipeDuplex channel, CancellationToken cancellationToken)
    {
        while (true)
        {
            var command = await channel.ReadLineAsync(TimeSpan.FromSeconds(5));
            if (command.Length == 0)
            {
                return;
            }

            Record(command);

            var upper = command.ToUpperInvariant();

            if (upper.StartsWith("S1 AUTHENTICATE", StringComparison.Ordinal))
            {
                var parts = command.Split(' ', 4);
                LastAuthArgument = parts.Length > 3 ? parts[3] : null;

                if (!AcceptCredential)
                {
                    await channel.SendAsync("S1 NO [AUTHENTICATIONFAILED] Invalid credentials\r\n");
                    return;
                }

                if (parts.Length > 3)
                {
                    // SASL-IR: the response arrived with the command, so we are done.
                    await channel.SendAsync("S1 OK authenticated\r\n");
                    return;
                }

                // No initial response: the backend must issue a challenge first.
                await channel.SendAsync("+ \r\n");
                var response = await channel.ReadLineAsync(TimeSpan.FromSeconds(5));
                Record(response);
                await channel.SendAsync(AcceptCredential ? "S1 OK authenticated\r\n" : "S1 NO rejected\r\n");
                return;
            }

            if (upper.StartsWith("S1 LOGIN", StringComparison.Ordinal))
            {
                LastAuthArgument = command["S1 LOGIN ".Length..];
                await channel.SendAsync(AcceptCredential ? "S1 OK authenticated\r\n" : "S1 NO rejected\r\n");
                return;
            }

            // Anything else during the authentication phase is unexpected; answering closes it out.
            await channel.SendAsync("S1 BAD unexpected\r\n");
            return;
        }
    }

    private async Task PlayPop3Async(PipeDuplex channel, CancellationToken cancellationToken)
    {
        while (true)
        {
            var command = await channel.ReadLineAsync(TimeSpan.FromSeconds(5));
            if (command.Length == 0)
            {
                return;
            }

            Record(command);

            var upper = command.ToUpperInvariant();

            if (upper.StartsWith("USER ", StringComparison.Ordinal))
            {
                await channel.SendAsync("+OK user accepted\r\n");
                continue;
            }

            if (upper.StartsWith("PASS ", StringComparison.Ordinal))
            {
                await channel.SendAsync(AcceptCredential ? "+OK maildrop ready\r\n" : "-ERR invalid credential\r\n");
                return;
            }

            if (upper.StartsWith("AUTH ", StringComparison.Ordinal))
            {
                LastAuthArgument = command["AUTH ".Length..];
                await channel.SendAsync(AcceptCredential ? "+OK authenticated\r\n" : "-ERR invalid credential\r\n");
                return;
            }

            await channel.SendAsync("-ERR unexpected\r\n");
            return;
        }
    }

    private void Record(string command)
    {
        lock (ReceivedCommands)
        {
            ReceivedCommands.Add(command);
        }
    }
}

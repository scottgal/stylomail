# Protocol harness, tier one: real sockets and a real backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up an opt-in test harness in which a real mail client reaches a real mail server through StyloMail's own protocol implementations, instead of only through the in-memory pipes the suite uses today.

**Architecture:** One new test project. `GreenMail` runs in a container as the backend, `MailKit` acts as the client, and a small TCP listener in the harness puts a real socket in front of `ImapAccessProxySession.RunAsync`. Nothing in `src/` changes.

**Tech Stack:** .NET 10, xUnit, Testcontainers for .NET, MailKit.

**Spec:** `docs/chat-channels-design.md` is unrelated to this; the relevant ground is `.styloagent/spec.md` section 9 (the client access proxy) and the existing patterns in `tests/StyloMail.AccessProxy.Tests/Support/`.

**Why this exists.** `StyloMail.AccessProxy` and `StyloMail.Transport` have **no package references at all**: the IMAP, POP3 and SMTP dialects are hand-written on both sides. Today's tests drive those dialects through `PipeDuplex`, an in-memory pipe, against a fake backend. That proves the state machine and proves nothing about the wire. A hand-rolled protocol parser fails on real framing, real TLS, real client quirks and real server quirks, and none of those exist in an in-memory pipe.

## Global Constraints

- `dotnet` is **not on PATH**. Every command runs after:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- The solution is `StyloMail.slnx`. Add the new project to it, or it will not build in CI.
- Analyzers run as **errors**. A build with a warning is a failed build.
- **Never use an em-dash** in code, comments, documentation or commit messages. Use a colon or a full stop.
- Every commit message ends with the `Co-Authored-By: Claude <noreply@anthropic.com>` trailer.
- **Do not run `git add` or `git commit`.** Leave the work in the tree and report. Never `--amend`, never `reset`.
- The suite is **opt-in**. Every test here is skipped unless `STYLOMAIL_HARNESS=1`, following the `LiveHostFactAttribute` pattern already used for the live console tests. A suite that needs Docker must never be the reason a normal `dotnet test` fails.

---

### Task 1: The harness project, and a container that answers a real client

**Files:**
- Create: `tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj`
- Create: `tests/StyloMail.Integration.Tests/HarnessFactAttribute.cs`
- Create: `tests/StyloMail.Integration.Tests/GreenMailServer.cs`
- Create: `tests/StyloMail.Integration.Tests/GreenMailTests.cs`
- Modify: `StyloMail.slnx`

**Interfaces:**
- Consumes: `GreenMailServer` is new here. `ProxyHarness` already exists at `tests/StyloMail.AccessProxy.Tests/Support/ProxyHarness.cs`.
- Produces: `HarnessFactAttribute` (skips unless `STYLOMAIL_HARNESS=1`) and `GreenMailServer` with `Host`, `ImapPort`, `Pop3Port`, `SmtpPort`, and `StartAsync()`.

- [ ] **Step 1: Create the project and add the packages**

```bash
cd /Users/scottgalloway/RiderProjects/stylomail
dotnet new xunit -o tests/StyloMail.Integration.Tests
dotnet sln StyloMail.slnx add tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj
dotnet add tests/StyloMail.Integration.Tests package Testcontainers
dotnet add tests/StyloMail.Integration.Tests package MailKit
dotnet add tests/StyloMail.Integration.Tests reference tests/StyloMail.AccessProxy.Tests
```

`dotnet add package` writes the resolved version into the `.csproj`, so no version is guessed here. **Record the two versions you get in your report.**

The reference to `StyloMail.AccessProxy.Tests` is deliberate and temporary: it lets this project reuse `ProxyHarness` and its in-memory fakes rather than duplicating them. When a second consumer appears (the transport harness), the shared parts should move into a `StyloMail.TestHarness` library and both test projects should reference that instead. Do not do that now.

Delete the generated `UnitTest1.cs`.

- [ ] **Step 2: Write the opt-in gate**

Create `tests/StyloMail.Integration.Tests/HarnessFactAttribute.cs`:

```csharp
namespace StyloMail.Integration.Tests;

/// <summary>
/// A fact that needs Docker and a real socket, and therefore does not run by default.
/// </summary>
/// <remarks>
/// Skipped unless <c>STYLOMAIL_HARNESS=1</c>. The unit suites must stay runnable with nothing
/// installed: a normal <c>dotnet test</c> that fails because a container runtime is absent is a
/// suite people learn to ignore. This mirrors <c>LiveHostFactAttribute</c>, which is the same idea
/// for the console.
/// </remarks>
public sealed class HarnessFactAttribute : FactAttribute
{
    public const string EnableVariable = "STYLOMAIL_HARNESS";

    public HarnessFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnableVariable)))
        {
            Skip = $"Set {EnableVariable}=1, and have a container runtime running, to run the protocol harness.";
        }
    }
}
```

Add `using Xunit;` if the generated project does not already have implicit usings for it.

- [ ] **Step 3: Write the failing test**

Create `tests/StyloMail.Integration.Tests/GreenMailTests.cs`:

```csharp
using MailKit.Net.Imap;
using MailKit.Security;

namespace StyloMail.Integration.Tests;

/// <summary>
/// The harness proving itself before it is used to prove anything else.
/// </summary>
/// <remarks>
/// If this fails, every other test in the project fails for a reason that has nothing to do with
/// StyloMail, so it is worth having one test whose only job is that the container is a mail server.
/// </remarks>
public sealed class GreenMailTests
{
    [HarnessFact]
    public async Task A_real_imap_client_logs_into_the_harness_backend()
    {
        await using var backend = await GreenMailServer.StartAsync();

        using var client = new ImapClient();
        await client.ConnectAsync(backend.Host, backend.ImapPort, SecureSocketOptions.None);

        await client.AuthenticateAsync(GreenMailServer.Login, GreenMailServer.AppPassword);

        Assert.True(client.IsAuthenticated);

        var inbox = await client.GetFolderAsync("INBOX");
        Assert.NotNull(inbox);

        await client.DisconnectAsync(true);
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj`
Expected: FAIL, with `The name 'GreenMailServer' does not exist in the current context`.

Also run it without the variable and confirm it reports **skipped**, not failed.

- [ ] **Step 5: Write the fixture**

Create `tests/StyloMail.Integration.Tests/GreenMailServer.cs`:

```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace StyloMail.Integration.Tests;

/// <summary>
/// A throwaway mail server, real enough to speak IMAP, POP3 and SMTP over a socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authentication is deliberately left enabled.</b> The standalone image disables it by default
/// through <c>GREENMAIL_OPTS</c>, and a backend that accepts any password cannot tell the harness
/// whether StyloMail presented the credential it resolved, which is the entire point of the
/// credential seam. So the options string is replaced rather than appended to.
/// </para>
/// <para>
/// <b>The ports are random and mapped.</b> A fixed port makes two runs collide, and a container that
/// fails because something else holds 3025 is a test failure that describes the machine rather than
/// the code.
/// </para>
/// </remarks>
public sealed class GreenMailServer : IAsyncDisposable
{
    /// <summary>The login the harness backend knows, as an address at the example domain.</summary>
    public const string Login = "alice@example.com";

    /// <summary>The backend's own password, which StyloMail must resolve and present.</summary>
    public const string AppPassword = "backend-app-password-4b7c";

    private readonly IContainer _container;

    private GreenMailServer(IContainer container) => _container = container;

    /// <summary>A host that only ever means the container, so no test has to think about it.</summary>
    public string Host => "127.0.0.1";

    public ushort ImapPort => _container.GetMappedPublicPort(3143);

    public ushort Pop3Port => _container.GetMappedPublicPort(3110);

    public ushort SmtpPort => _container.GetMappedPublicPort(3025);

    public static async Task<GreenMailServer> StartAsync()
    {
        // -Dgreenmail.users=login:password@domain creates login@domain with that password, which is
        // how a backend acquires an account without a test signing up over the wire first.
        const string Options =
            "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 "
            + "-Dgreenmail.users=alice:backend-app-password-4b7c@example.com";

        var container = new ContainerBuilder()
            .WithImage("greenmail/standalone:2.1.14")
            .WithEnvironment("GREENMAIL_OPTS", Options)
            .WithPortBinding(3143, assignRandomHostPort: true)
            .WithPortBinding(3110, assignRandomHostPort: true)
            .WithPortBinding(3025, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(3143))
            .Build();

        await container.StartAsync();
        return new GreenMailServer(container);
    }

    public ValueTask DisposeAsync() => new(_container.DisposeAsync().AsTask());
}
```

**Image tag:** `2.1.14` is deliberate and was verified to exist on Docker Hub before this plan was written (the newest at the time; `2.1.9` was the first candidate and is stale). XOAUTH2 arrived for SMTP in 2.1.3 and for IMAP and POP3 in 2.1.5, so anything older than 2.1.5 silently rules out the OAuth test that plan three needs. If the tag has vanished by the time you run it, pick the newest 2.1.x that resolves and **say which you used in your report**.

- [ ] **Step 6: Run test to verify it passes**

Run: `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj`
Expected: PASS. If it fails, the container is the problem and nothing downstream is worth debugging until it is green.

- [ ] **Step 7: Commit**

```bash
git add tests/StyloMail.Integration.Tests StyloMail.slnx
git commit -m "Add the protocol harness, and prove the backend answers a real client

GreenMail in a container with authentication left enabled, because a backend
that accepts any password cannot tell the harness whether StyloMail presented
the credential it resolved. Opt-in behind STYLOMAIL_HARNESS=1 so a normal test
run never needs a container runtime.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: A real IMAP client reaches the backend through StyloMail

**Files:**
- Create: `tests/StyloMail.Integration.Tests/ProxyListener.cs`
- Create: `tests/StyloMail.Integration.Tests/ImapThroughProxyTests.cs`

**Interfaces:**
- Consumes: `GreenMailServer` and `HarnessFactAttribute` from Task 1; `ProxyHarness` and `StreamDuplexChannel` from existing code.
- Produces: `ProxyListener.Start(Func<IDuplexChannel, CancellationToken, Task<SessionOutcome>> run)` returning a listener with a `Port`, and `IAsyncDisposable`.

The seam already exists: `ImapAccessProxySession.RunAsync(IDuplexChannel client, CancellationToken)` takes the client side as a duplex channel, and `StreamDuplexChannel(Stream input, Stream output, string description, bool ownsStreams = true)` already adapts a pair of streams to it. What is missing is something that turns an accepted TCP connection into that pair.

- [ ] **Step 1: Write the failing test**

Create `tests/StyloMail.Integration.Tests/ImapThroughProxyTests.cs`:

```csharp
using MailKit.Net.Imap;
using MailKit.Security;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.Integration.Tests;

/// <summary>
/// The client access proxy with a real client on one side and a real server on the other.
/// </summary>
/// <remarks>
/// The in-memory suite proves the state machine. This proves the bytes: a client library this
/// project does not control, speaking the dialect over a socket, against a server this project does
/// not control, with our parser in the middle.
/// </remarks>
public sealed class ImapThroughProxyTests
{
    [HarnessFact]
    public async Task A_real_imap_client_reaches_the_backend_through_the_proxy()
    {
        // The backend account is the one whose password a client can no longer use directly.
        await using var backend = await GreenMailServer.StartAsync();

        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        await using var proxy = ProxyListener.Start((channel, token) =>
            harness.NewImapSession().RunAsync(channel, token));

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", proxy.Port, SecureSocketOptions.None);

        // The whole point of the feature: a client that can only speak LOGIN user pass gets a
        // working session against a backend that no longer accepts that.
        await client.AuthenticateAsync(ProxyHarness.Login, ProxyHarness.ClientPassword);

        Assert.True(client.IsAuthenticated);

        var inbox = await client.GetFolderAsync("INBOX");
        Assert.NotNull(inbox);

        await client.DisconnectAsync(true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj --filter ImapThroughProxyTests`
Expected: FAIL, with `The name 'ProxyListener' does not exist in the current context`.

- [ ] **Step 3: Write the listener**

Create `tests/StyloMail.Integration.Tests/ProxyListener.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Integration.Tests;

/// <summary>
/// Puts a real socket in front of a proxy session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connections are accepted one at a time and run to completion.</b> A test here drives one
/// client, so an accept loop that overlaps sessions would add concurrency the test is not exercising
/// and make a failure harder to read. When a test needs two clients, it starts two listeners.
/// </para>
/// <para>
/// <b>The port is chosen by the operating system.</b> Binding a fixed port makes two runs collide,
/// and a container or a stray process holding it would look like a defect in the proxy.
/// </para>
/// </remarks>
public sealed class ProxyListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<IDuplexChannel, CancellationToken, Task<SessionOutcome>> _run;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;

    private ProxyListener(
        TcpListener listener,
        Func<IDuplexChannel, CancellationToken, Task<SessionOutcome>> run)
    {
        _listener = listener;
        _run = run;
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static ProxyListener Start(Func<IDuplexChannel, CancellationToken, Task<SessionOutcome>> run)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ProxyListener(listener, run);
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient accepted;

            try
            {
                accepted = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // The listener was closed underneath us, which is how disposal ends this loop.
                return;
            }

            using (accepted)
            {
                var stream = accepted.GetStream();
                var description = $"client:127.0.0.1:{(accepted.Client.RemoteEndPoint as IPEndPoint)?.Port}";
                await using var channel = new StreamDuplexChannel(stream, stream, description);

                try
                {
                    await _run(channel, _stopping.Token);
                }
                catch (Exception) when (_stopping.IsCancellationRequested)
                {
                    // A session cut short by disposal is the normal end of a test, not a failure.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();

        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
            // Expected on the way out.
        }

        _stopping.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj`
Expected: PASS for both tests.

If it hangs rather than fails, the likely cause is the session waiting for a greeting that the harness never sent, and the fix is in the wiring rather than in the proxy.

- [ ] **Step 5: Commit**

```bash
git add tests/StyloMail.Integration.Tests
git commit -m "Drive the IMAP proxy with a real client over a real socket

The in-memory suite proves the state machine; this proves the bytes. A real
client library and a real server, with our hand-written parser in the middle.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: The same for POP3

**Files:**
- Create: `tests/StyloMail.Integration.Tests/Pop3ThroughProxyTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 and 2. `ProxyHarness.NewPop3Session()` already exists beside `NewImapSession()`.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

Create `tests/StyloMail.Integration.Tests/Pop3ThroughProxyTests.cs`:

```csharp
using MailKit.Net.Pop3;
using MailKit.Security;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.Integration.Tests;

public sealed class Pop3ThroughProxyTests
{
    [HarnessFact]
    public async Task A_real_pop3_client_reaches_the_backend_through_the_proxy()
    {
        await using var backend = await GreenMailServer.StartAsync();

        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        await using var proxy = ProxyListener.Start((channel, token) =>
            harness.NewPop3Session().RunAsync(channel, token));

        using var client = new Pop3Client();
        await client.ConnectAsync("127.0.0.1", proxy.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync(ProxyHarness.Login, ProxyHarness.ClientPassword);

        Assert.True(client.IsAuthenticated);

        await client.DisconnectAsync(true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj --filter Pop3ThroughProxyTests`
Expected: FAIL, with `'ProxyHarness' does not contain a definition for 'NewPop3Session'` if the name differs. **Read `ProxyHarness` and use the name it actually has**; the in-memory POP3 tests already call something and that is the name.

- [ ] **Step 3: Fix the call to the real name and run it**

Run: `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj`
Expected: PASS for all three tests.

- [ ] **Step 4: Run the whole solution and confirm nothing regressed**

```bash
dotnet build StyloMail.slnx
dotnet test StyloMail.slnx
```

Expected: build 0 errors and 0 warnings. The new tests report **skipped** here, because `STYLOMAIL_HARNESS` is not set, and every other test is green with the same totals as before.

- [ ] **Step 5: Commit**

```bash
git add tests/StyloMail.Integration.Tests
git commit -m "Drive the POP3 proxy with a real client over a real socket

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## What this plan deliberately does not do

- **SMTP submission and the upstream MTA handoff.** `Transport` needs the same treatment and it is
  plan two, once this shape is proven.
- **Cloudflare Email Routing and the OAuth token endpoint.** Both are HTTP, both need `WireMock.Net`,
  and neither is exercised by a mail-protocol harness. Plan three.
- **XOAUTH2 against GreenMail.** The image tag is pinned for it, but the test is not here: the
  credential seam deserves its own task rather than being an afterthought to a transport test.
- **A cross-implementation matrix.** Dovecot and Stalwart would each find a different class of
  parser defect, and that is worth having, but it multiplies the container count before the first
  container has proven it can hold a conversation.
- **The Gmail question.** Its IMAP adds `X-GM-LABELS`, `X-GM-MSGID` and `X-GM-THRID`, which no
  container image implements because they are Google's superset rather than the RFC. The harness
  covers RFC IMAP and **the untested Gmail behaviour must be written down** rather than implied away.

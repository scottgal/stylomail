using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using StyloMail.ProxyHarness.Reliability;

var json = new JsonSerializerOptions { WriteIndented = true };
var thresholdsPath = Path.Combine(AppContext.BaseDirectory, "thresholds.json");
var thresholds = JsonSerializer.Deserialize<Thresholds>(File.ReadAllText(thresholdsPath))
    ?? throw new InvalidOperationException("Missing thresholds");
if (thresholds.Concurrency < 2 || thresholds.Concurrency % 2 != 0 || thresholds.Rounds < 3
    || thresholds.RoundSeconds < 1 || thresholds.WarmupSessions < 2 || thresholds.SettleMilliseconds < 0
    || thresholds.SessionDeadlineSeconds < 1 || thresholds.MinSessionsPerRound < 1)
    throw new ArgumentException("Thresholds must provide even concurrency, both protocols, and at least three bounded rounds");
var artifactDirectory = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass one artifact-directory path");
Directory.CreateDirectory(artifactDirectory);
var failures = new List<string>();
var instrumentErrors = new List<string>();
var rounds = new List<object>();
var faults = new List<object>();
var report = new Dictionary<string, object?>
{
    ["thresholds"] = thresholds, ["thresholdsSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(thresholdsPath))),
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["rounds"] = rounds, ["faults"] = faults, ["findings"] = failures, ["instrumentErrors"] = instrumentErrors,
    ["scope"] = "Production TCP listener, IMAP/SMTP sessions, limiter, backend connectors/Host transport, client/backend TLS and in-memory stager. Ephemeral fixture accounts with PBKDF2 1000; allow/no-analyser stand-ins. No live providers, model, persistence or management lifecycle. Darwin FD/socket census; process census uses ps comm only. Memory and resource gates have versioned tolerances; GC counts are observations. Allocations include both clients and fixtures, not just product code.",
};
try
{
    var fixture = await ProxyFixture.Create(BackendMode.Healthy, thresholds.Concurrency);
    var ports = fixture.Ports;
    report["ports"] = ports;
    try
    {
        // Warm both TLS paths and crypto/runtime caches before measuring retained resources.
        for (var i = 0; i < thresholds.WarmupSessions; i++) await Client(fixture, i % 2 == 0, "healthy");
        await fixture.Settle(TimeSpan.FromSeconds(5));
        await Task.Delay(thresholds.SettleMilliseconds);
        var baseline = ResourceCensus.Capture(fixture);
        report["baseline"] = baseline;

        // A socket-census control: two connected endpoints must increase the native socket count.
        using (var control = new TcpClient())
        {
            await control.ConnectAsync(IPAddress.Loopback, fixture.ImapPort);
            await Task.Delay(50);
            var held = ResourceCensus.Capture(fixture);
            if (held.Sockets < baseline.Sockets + 2) throw new IOException("Socket census failed its positive control");
        }
        await fixture.Settle(TimeSpan.FromSeconds(5));
        // The probe intentionally aborts a pre-TLS connection; exclude its named/unnamed result
        // from the healthy-session gate rather than claiming that the probe was a normal session.
        report["censusControlOutcomes"] = fixture.Outcomes;
        var measuredStartResults = fixture.ResultCount;
        var managedSamples = new List<long>();
        var residentSamples = new List<long>();
        var peakSlots = 0;
        for (var round = 0; round < thresholds.Rounds; round++)
        {
            var completed = 0;
            long handshakeMicroseconds = 0;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var start = Stopwatch.GetTimestamp();
            var beforeResults = fixture.ResultCount;
            var beforeRelayed = fixture.RelayedCount;
            using var sampling = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        peakSlots = Math.Max(peakSlots, fixture.Limiter.ActiveSessions);
                        await Task.Delay(2, sampling.Token);
                    }
                }
                catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
            });
            try
            {
                await Task.WhenAll(Enumerable.Range(0, thresholds.Concurrency).Select(async worker =>
                {
                    while (Stopwatch.GetElapsedTime(start).TotalSeconds < thresholds.RoundSeconds)
                    {
                        var latency = await Client(fixture, worker % 2 == 0, "healthy");
                        Interlocked.Add(ref handshakeMicroseconds, (long)(latency * 1000));
                        Interlocked.Increment(ref completed);
                    }
                }));
            }
            finally { await sampling.CancelAsync(); await sampler; }
            await fixture.Settle(TimeSpan.FromSeconds(5));
            await Task.Delay(thresholds.SettleMilliseconds);
            var sample = ResourceCensus.Capture(fixture);
            managedSamples.Add(sample.ManagedBytes);
            residentSamples.Add(sample.WorkingSetBytes);
            var allocatedPerSession = (sample.TotalAllocatedBytes - allocatedBefore) / Math.Max(1, completed);
            rounds.Add(new { round = round + 1, completed, elapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds,
                authenticationMeanMilliseconds = handshakeMicroseconds / 1000.0 / Math.Max(1, completed),
                allocatedBytesPerSession = allocatedPerSession, resources = sample, outcomes = fixture.Outcomes });
            Console.WriteLine($"soak round={round + 1} sessions={completed} slots={sample.SessionSlots} sockets={sample.Sockets} handles={sample.Handles} managed={sample.ManagedBytes}");
            if (completed < thresholds.MinSessionsPerRound || fixture.ResultCount - beforeResults != completed || fixture.RelayedCount - beforeRelayed != completed) failures.Add($"round-{round + 1}: session count or named Relayed outcome mismatch");
            if (sample.Processes != baseline.Processes || sample.SessionSlots != 0 || sample.TrackedClients != 0 || sample.TrackedSessions != 0 || sample.BackendSessions != 0 || sample.StagingHandles != 0) failures.Add($"round-{round + 1}: resource owners did not return to baseline");
            if (sample.Sockets > baseline.Sockets + thresholds.MaxSocketDelta || sample.Handles > baseline.Handles + thresholds.MaxHandleDelta || sample.ManagedBytes > baseline.ManagedBytes + thresholds.MaxManagedDeltaBytes) failures.Add($"round-{round + 1}: retention budget exceeded");
            if (sample.WorkingSetBytes > baseline.WorkingSetBytes + thresholds.MaxWorkingSetDeltaBytes) failures.Add($"round-{round + 1}: working set budget exceeded");
        }
        // Three consistently rising samples are a trend only above the versioned noise tolerance.
        if (managedSamples.Count >= 3 && managedSamples.Zip(managedSamples.Skip(1), (left, right) => right - left)
            .All(delta => delta > thresholds.MaxManagedTrendBytesPerRound)) failures.Add("managed retention rises every round beyond tolerance");
        if (residentSamples.Count >= 3 && residentSamples.Zip(residentSamples.Skip(1), (left, right) => right - left)
            .All(delta => delta > thresholds.MaxWorkingSetTrendBytesPerRound)) failures.Add("working set rises every round beyond tolerance");
        report["soakResultCount"] = fixture.ResultCount - measuredStartResults;
        report["peakTrackedConnectionsIncludingTeardown"] = fixture.PeakSessions;
        report["peakSessionSlotsSampledEvery2ms"] = peakSlots;
        report["backendErrors"] = fixture.BackendErrors;
        if (fixture.BackendErrors.Length != 0) failures.Add("backend fixture errors invalidate soak");
        if (peakSlots != thresholds.Concurrency) failures.Add("concurrency control did not reach requested session slots");
    }
    finally { await fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
    report["afterFixtureDisposal"] = ResourceCensus.Capture(fixture);
    try { ProbeReleasedPorts(ports); report["portsReleased"] = true; }
    catch (SocketException error)
    {
        // TIME_WAIT can legitimately prevent a fresh exclusive bind after teardown. Preserve the
        // failed availability probe; do not label it a process/socket leak or skip the fault group.
        report["portsReleased"] = false;
        report["portBindFailure"] = error.SocketErrorCode.ToString();
        failures.Add("exclusive-port-rebind-unavailable (inspect netstat states; not proof of a leak)");
    }
    Console.WriteLine("soak complete; starting first fault group");

    foreach (var scenario in new[]
             {
                 (Name: "backend-refused", Mode: BackendMode.Refused, Smtp: false),
                 (Name: "backend-refused", Mode: BackendMode.Refused, Smtp: true),
                 (Name: "connect-stalled", Mode: BackendMode.ConnectStall, Smtp: false),
                 (Name: "connect-stalled", Mode: BackendMode.ConnectStall, Smtp: true),
                 (Name: "backend-stalled", Mode: BackendMode.NoGreeting, Smtp: false),
                 (Name: "backend-stalled", Mode: BackendMode.NoGreeting, Smtp: true),
                 (Name: "half-close", Mode: BackendMode.Healthy, Smtp: false),
                 (Name: "half-close", Mode: BackendMode.Healthy, Smtp: true),
                 (Name: "abort-data", Mode: BackendMode.Healthy, Smtp: true),
             })
    {
        await using var faultFixture = await ProxyFixture.Create(scenario.Mode, thresholds.Concurrency);
        if (scenario.Mode == BackendMode.Refused)
        {
            using var refusalControl = new TcpClient(AddressFamily.InterNetwork);
            using var controlDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var refused = false;
            try { await refusalControl.ConnectAsync(IPAddress.Loopback, faultFixture.Ports[scenario.Smtp ? 3 : 2], controlDeadline.Token); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused) { refused = true; }
            if (!refused) throw new IOException("Backend refusal injection did not produce ConnectionRefused");
        }
        var start = Stopwatch.GetTimestamp();
        await Client(faultFixture, scenario.Smtp, scenario.Name);
        var settled = true;
        try { await faultFixture.Settle(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { settled = false; }
        var protocol = scenario.Smtp ? "smtp" : "imap";
        var expectedOutcomes = scenario.Name switch
        {
            "backend-refused" => new[] { "BackendUnavailable" },
            "backend-stalled" => new[] { "Timeout", "BackendUnavailable" },
            "connect-stalled" => new[] { "Timeout" },
            "half-close" when !scenario.Smtp => new[] { "Relayed", "ClientDisconnected" },
            _ => new[] { "ClientDisconnected" },
        };
        var named = faultFixture.HasSingleOutcome(protocol, expectedOutcomes);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if ((scenario.Name.StartsWith("backend-", StringComparison.Ordinal) || scenario.Name == "connect-stalled")
            && elapsed > ProxyFixture.BackendDeadlineMilliseconds + thresholds.BackendDeadlineToleranceMilliseconds)
            failures.Add($"{scenario.Name}/{protocol}: configured backend deadline exceeded");
        if (!named) failures.Add($"{scenario.Name}/{(scenario.Smtp ? "smtp" : "imap")}: no named session outcome");
        if (!settled) failures.Add($"{scenario.Name}: resources did not settle");
        faults.Add(new { scenario.Name, protocol = scenario.Smtp ? "smtp" : "imap", namedOutcome = named,
            expectedOutcomes, settled, elapsedMilliseconds = elapsed,
            configuredBackendDeadlineMilliseconds = ProxyFixture.BackendDeadlineMilliseconds,
            outcomes = faultFixture.Outcomes, clientResults = faultFixture.ClientResults,
            slots = faultFixture.Limiter.ActiveSessions, stagingHandles = faultFixture.Stager.ActiveCount });
        Console.WriteLine($"fault name={scenario.Name} protocol={(scenario.Smtp ? "smtp" : "imap")} named={named} settled={settled}");
    }
}
catch (Exception exception)
{
    // Exception messages and protocol lines may contain untrusted data; only type names are saved.
    instrumentErrors.Add("instrument-aborted:" + exception.GetType().Name);
}
finally
{
    report["passed"] = failures.Count == 0 && instrumentErrors.Count == 0;
    File.WriteAllText(Path.Combine(artifactDirectory, "report.json"), JsonSerializer.Serialize(report, json));
}
Console.WriteLine($"reliability findings={failures.Count} instrumentErrors={instrumentErrors.Count} report={Path.Combine(artifactDirectory, "report.json")}");
return instrumentErrors.Count != 0 ? 2 : failures.Count == 0 ? 0 : 1;

async Task<double> Client(ProxyFixture fixture, bool smtp, string fault)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(thresholds.SessionDeadlineSeconds));
    try { var elapsed = await ProtocolClient.Run(fixture, smtp, fault, deadline.Token); fixture.ClientResult("completed"); return elapsed; }
    catch (Exception exception)
    {
        fixture.ClientResult(exception.GetType().Name);
        if (fault == "healthy") throw;
        return 0;
    }
}

static void ProbeReleasedPorts(IEnumerable<int> ports)
{
    var held = new List<Socket>();
    try
    {
        foreach (var port in ports.Distinct())
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            held.Add(socket);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
    }
    finally { foreach (var socket in held) socket.Dispose(); }
}

internal sealed record Thresholds(int Concurrency, int Rounds, int RoundSeconds, int WarmupSessions,
    int SettleMilliseconds, int SessionDeadlineSeconds, int MaxHandleDelta, int MaxSocketDelta,
    long MaxManagedDeltaBytes, long MaxManagedTrendBytesPerRound, long MaxWorkingSetDeltaBytes,
    long MaxWorkingSetTrendBytesPerRound, int BackendDeadlineToleranceMilliseconds, int MinSessionsPerRound);

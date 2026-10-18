using System.Net;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Tests;

public sealed class RelayOutputIdleTests
{
    [Fact]
    public async Task Dispose_that_does_not_settle_a_write_releases_the_slot_without_clearing_or_pooling_its_array()
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromMilliseconds(200), nonsettlingWrite: true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var heartbeat = Heartbeat(fixture, stop.Token);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var expected = new byte[] { 41 };
        try
        {
            await fixture.BackendPeer.GetStream().WriteAsync(expected, stop.Token);
            await fixture.Output.WriteEntered.Task.WaitAsync(stop.Token);
            Assert.Equal(ProxyBoundReason.IdleTimeout, await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
            await fixture.AssertReleased();
            Assert.True(fixture.Output.Disposed);
            Assert.Equal(1, fixture.Output.Pending);
            Assert.Equal(expected, fixture.Output.CapturedWrite.ToArray());
            Assert.Equal(1, fixture.AbandonmentLogs);
            Assert.Equal(2, fixture.Buffers!.Rented.Count);
            Assert.Single(fixture.Buffers.Returned);
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(fixture.Output.CapturedWrite, out var memory));
            Assert.DoesNotContain(memory.Array!, fixture.Buffers.Returned);
        }
        finally { fixture.Output.ReleaseWrite.TrySetResult(); stop.Cancel(); await Drain(heartbeat); }
        await Until(() => fixture.Output.Pending == 0);
        Assert.Single(fixture.Buffers!.Returned);
        Assert.Equal(expected, fixture.Output.CapturedWrite.ToArray());
    }

    [Fact]
    public async Task Nonreading_real_tcp_peer_times_out_a_pending_write_and_releases_socket_owners()
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromMilliseconds(700));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var heartbeat = Heartbeat(fixture, stop.Token);
        var send = SendUntilClosed(fixture.BackendPeer.GetStream(), stop.Token);
        try
        {
            await Until(() => fixture.Output.Pending > 0);
            await Task.Delay(100, stop.Token);
            Assert.True(fixture.Output.Pending > 0, "The real TCP send buffer never stayed full.");
            var finished = await Task.WhenAny(fixture.Finished.Task, Task.Delay(TimeSpan.FromSeconds(8), stop.Token));
            Assert.True(ReferenceEquals(fixture.Finished.Task, finished),
                $"Output idle did not reclaim sockets: writes={fixture.Output.Writes}, completed={fixture.Output.Completed}, canceled={fixture.Output.Canceled}, disposed={fixture.Output.Disposed}, pending={fixture.Output.Pending}, receive={fixture.Client.ReceiveBufferSize}, unread={fixture.Client.Available}, send={fixture.Output.SendBufferSize}.");
            Assert.Equal(ProxyBoundReason.IdleTimeout, await fixture.Finished.Task);
            Assert.True(fixture.Output.Disposed);
            Assert.Equal(0, fixture.Output.Pending);
            Assert.InRange(fixture.Output.IdleAtClose, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
            await fixture.AssertReleased();
        }
        finally { stop.Cancel(); await Drain(heartbeat, send); }
    }

    [Fact]
    public async Task Continuous_progress_can_exceed_one_idle_window_and_preserves_exact_bytes()
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromMilliseconds(700));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var heartbeat = Heartbeat(fixture, stop.Token);
        var expected = new byte[4096];
        new Random(42).NextBytes(expected);
        var actual = new byte[expected.Length];
        try
        {
            // Four completed writes span >700ms while making progress every250ms.
            for (var i = 0; i < 4; i++)
            {
                await fixture.BackendPeer.GetStream().WriteAsync(expected, stop.Token);
                await fixture.Client.GetStream().ReadExactlyAsync(actual, stop.Token);
                Assert.Equal(expected, actual);
                await Task.Delay(250, stop.Token);
                Assert.False(fixture.Finished.Task.IsCompleted);
            }
            fixture.Client.Client.Shutdown(SocketShutdown.Send);
            Assert.Equal(ProxyBoundReason.None, await fixture.Finished.Task.WaitAsync(stop.Token));
            await fixture.AssertReleased();
        }
        finally { stop.Cancel(); await Drain(heartbeat); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(nameof(OperationCanceledException))]
    [InlineData(nameof(IOException))]
    [InlineData(nameof(ObjectDisposedException))]
    [InlineData(nameof(InvalidOperationException))]
    [InlineData(nameof(ApplicationException))]
    public async Task Stalled_flush_is_closed_and_settled_before_relay_ownership_is_released(string? faultOnClose)
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromMilliseconds(700), stallFlush: true, faultOnClose: faultOnClose);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var heartbeat = Heartbeat(fixture, stop.Token);
        try
        {
            await fixture.BackendPeer.GetStream().WriteAsync(new byte[] { 41, 42, 43 }, stop.Token);
            await fixture.Output.FlushEntered.Task.WaitAsync(stop.Token);
            Assert.Same(fixture.Finished.Task, await Task.WhenAny(fixture.Finished.Task, Task.Delay(TimeSpan.FromSeconds(3))));
            Assert.Equal(ProxyBoundReason.IdleTimeout, await fixture.Finished.Task);
            Assert.True(fixture.Output.Disposed);
            Assert.True(fixture.Output.FlushSettled);
            Assert.Equal(0, fixture.Output.Pending);
            await fixture.AssertReleased();
            Assert.Equal(2, fixture.Buffers!.Returned.Count);
            Assert.All(fixture.Buffers.Returned, buffer => Assert.True(buffer.AsSpan().IndexOfAnyExcept((byte)0) < 0));
            Assert.InRange(fixture.SettledFaultLogs.Count, 0, 1);
            if (faultOnClose is not null)
                Assert.Contains(Assert.Single(fixture.SettledFaultLogs), new[] { faultOnClose, nameof(TaskCanceledException), nameof(OperationCanceledException) });
        }
        finally { stop.Cancel(); await Drain(heartbeat); }
    }

    private static async Task Heartbeat(Fixture fixture, CancellationToken token)
    {
        var drain = DrainBytes(fixture.BackendPeer.GetStream(), token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await fixture.Client.GetStream().WriteAsync(new byte[1], token);
                await Task.Delay(50, token);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        await Drain(drain);
    }
    private static async Task DrainBytes(Stream stream, CancellationToken token)
    {
        var bytes = new byte[4096];
        try { while (await stream.ReadAsync(bytes, token) != 0) { } }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private static async Task SendUntilClosed(Stream stream, CancellationToken token)
    {
        var bytes = new byte[65536];
        Random.Shared.NextBytes(bytes);
        try { while (!token.IsCancellationRequested) await stream.WriteAsync(bytes, token); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private static async Task Drain(params Task[] tasks) => await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TcpClient Client { get; } = new(AddressFamily.InterNetwork) { ReceiveBufferSize = 1024, NoDelay = true };
        public TcpClient BackendPeer { get; } = new(AddressFamily.InterNetwork) { NoDelay = true };
        public TaskCompletionSource<ProxyBoundReason> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TrackedOutput Output { get; private set; } = null!;
        public RecordingPool? Buffers { get; private set; }
        public int AbandonmentLogs;
        public ConcurrentQueue<string> SettledFaultLogs { get; } = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TcpAccessProxyListener _listener = null!;
        private SessionLimiter _limiter = null!;
        public static async Task<Fixture> Create(TimeSpan idle, bool stallFlush = false, bool nonsettlingWrite = false, string? faultOnClose = null)
        {
            var fixture = new Fixture();
            fixture.Buffers = new RecordingPool();
            var bounds = new AccessProxyBounds { IdleTimeout = idle, MaxSessionDuration = TimeSpan.FromSeconds(20), MaxConcurrentSessions = 1, MaxSessionsPerAccount = 1, RelayBufferBytes = 4096 };
            fixture._limiter = new SessionLimiter(bounds);
            var backendListener = new TcpListener(IPAddress.Loopback, 0);
            backendListener.Start();
            TcpClient backend;
            try
            {
                await fixture.BackendPeer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)backendListener.LocalEndpoint).Port);
                backend = await backendListener.AcceptTcpClientAsync();
            }
            finally { backendListener.Stop(); }
            fixture._listener = new TcpAccessProxyListener(new IPEndPoint(IPAddress.Loopback, 0), "test-tenant", async (_, raw, token) =>
            {
                ((NetworkStream)raw.Input).Socket.SendBufferSize = 64 * 1024;
                ((NetworkStream)raw.Input).Socket.NoDelay = true;
                fixture.Output = new TrackedOutput(raw.Output, stallFlush, nonsettlingWrite, faultOnClose);
                await using var clientChannel = new StreamDuplexChannel(raw.Input, fixture.Output, "test-client");
                await using var backendChannel = new StreamDuplexChannel(backend.GetStream(), backend.GetStream(), "test-backend");
                fixture._started.TrySetResult();
                try
                {
                    var reason = await ByteRelay.RunAsync(clientChannel, backendChannel, bounds, TimeProvider.System, null, token,
                        () => Interlocked.Increment(ref fixture.AbandonmentLogs), fixture.Buffers, fixture.SettledFaultLogs.Enqueue);
                    fixture.Finished.TrySetResult(reason);
                    return reason == ProxyBoundReason.None ? SessionOutcome.Relayed : SessionOutcome.Timeout;
                }
                catch (Exception exception) { fixture.Finished.TrySetException(exception); throw; }
                finally { backend.Dispose(); }
            }, fixture._limiter);
            // Configure the listening socket before the TCP handshake so accepted sockets inherit
            // the small buffer rather than retaining an established connection's autotuning.
            var tcp = (TcpListener)typeof(TcpAccessProxyListener).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture._listener)!;
            tcp.Server.SendBufferSize = 64 * 1024;
            await fixture._listener.StartAsync(CancellationToken.None);
            await fixture.Client.ConnectAsync(IPAddress.Loopback, fixture._listener.BoundPort);
            // Apply on the connected socket too. macOS can immediately autotune its readback;
            // pending real writes and the resulting idle close are the control, not that value.
            fixture.Client.ReceiveBufferSize = 1024;
            await fixture._started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, fixture._limiter.ActiveSessions);
            return fixture;
        }
        public async Task AssertReleased()
        {
            await Until(() => _limiter.ActiveSessions == 0);
            foreach (var name in new[] { "_clients", "_sessions" })
            {
                var dictionary = typeof(TcpAccessProxyListener).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_listener)!;
                Assert.Equal(0, (int)dictionary.GetType().GetProperty("Count")!.GetValue(dictionary)!);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); BackendPeer.Dispose();
            if (_listener is not null) await _listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            _limiter?.Dispose();
        }
    }

    private sealed class RecordingPool : ArrayPool<byte>
    {
        public ConcurrentQueue<byte[]> Rented { get; } = new();
        public ConcurrentQueue<byte[]> Returned { get; } = new();
        public override byte[] Rent(int minimumLength) { var buffer = new byte[minimumLength]; Rented.Enqueue(buffer); return buffer; }
        public override void Return(byte[] array, bool clearArray = false) { if (clearArray) Array.Clear(array); Returned.Enqueue(array); }
    }

    private sealed class TrackedOutput(Stream inner, bool stallFlush, bool nonsettlingWrite, string? faultOnClose) : Stream
    {
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReadOnlyMemory<byte> CapturedWrite { get; private set; }
        private int _pending, _disposed, _writes, _completed, _canceled;
        public int Writes => Volatile.Read(ref _writes);
        public int Completed => Volatile.Read(ref _completed);
        public int Canceled => Volatile.Read(ref _canceled);
        public int SendBufferSize { get; } = ((NetworkStream)inner).Socket.SendBufferSize;
        private long _lastCompleted = System.Diagnostics.Stopwatch.GetTimestamp();
        private long _closedAt;
        public TimeSpan IdleAtClose => System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCompleted), Volatile.Read(ref _closedAt));
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pending => Volatile.Read(ref _pending);
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public bool FlushSettled { get; private set; }
        public TaskCompletionSource FlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Increment(ref _writes);
            using var registration = cancellationToken.Register(() => Interlocked.Increment(ref _canceled));
            try
            {
                if (nonsettlingWrite)
                {
                    CapturedWrite = buffer;
                    WriteEntered.TrySetResult();
                    await ReleaseWrite.Task; // Neither cancellation nor Dispose settles this public seam.
                }
                else await inner.WriteAsync(buffer, cancellationToken);
                Interlocked.Exchange(ref _lastCompleted, System.Diagnostics.Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _completed);
            }
            finally { Interlocked.Decrement(ref _pending); }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!stallFlush) { await inner.FlushAsync(cancellationToken); return; }
            FlushEntered.TrySetResult();
            // Deliberately ignores cancellation and only settles on physical stream disposal.
            try
            {
                await _closed.Task;
                if (faultOnClose is not null) throw faultOnClose switch
                {
                    nameof(OperationCanceledException) => new OperationCanceledException("Closed I/O."),
                    nameof(IOException) => new IOException("Closed I/O."),
                    nameof(ObjectDisposedException) => new ObjectDisposedException("Closed I/O."),
                    nameof(InvalidOperationException) => new InvalidOperationException("Closed transport has completed its reader."),
                    _ => new ApplicationException("An arbitrary closed-transport fault."),
                };
            }
            finally { FlushSettled = true; }
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await inner.DisposeAsync();
            Interlocked.Exchange(ref _closedAt, System.Diagnostics.Stopwatch.GetTimestamp());
            _closed.TrySetResult();
            GC.SuppressFinalize(this);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

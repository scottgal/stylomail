using System.Collections.Concurrent;

namespace StyloMail.Assessment.Semantic;

/// <summary>
/// Shares work per key with independent waiter cancellation and a finite lifetime. One expired
/// execution can drain beside its replacement. Two expired executions install a tombstone,
/// bounding actual unsettled executions to two per key even when providers ignore cancellation.
/// </summary>
public sealed class SingleFlight<TKey, TResult> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, State> _states = new();
    private readonly TimeSpan _maximum;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _diagnostic;
    private long _expired, _lateCompletions, _tombstoneRefusals;
    public long ExpiredFlights => Interlocked.Read(ref _expired);
    public long DetachedCompletions => Interlocked.Read(ref _lateCompletions);
    public long TombstoneRefusals => Interlocked.Read(ref _tombstoneRefusals);
    /// <summary>Tracked keys, including draining executions; not a waiter count.</summary>
    public int InFlightCount => _states.Count;

    private sealed class State
    {
        public readonly object Gate = new();
        public readonly HashSet<Flight> Running = [];
        public Flight? Current;
        public bool Tombstone, RefusalLogged;
    }
    private sealed class Flight(Func<CancellationToken, Task<TResult>> factory, Func<TResult> unavailable, Action<TResult>? publish)
    {
        public readonly CancellationTokenSource Lifetime = new();
        public readonly TaskCompletionSource<TResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Func<CancellationToken, Task<TResult>> Factory = factory;
        public readonly Func<TResult> Unavailable = unavailable;
        public readonly Action<TResult>? Publish = publish;
        public ITimer? Timer;
        public bool Expired, Settled, CancellationCompleted;
    }

    public SingleFlight(TimeSpan? maximumDuration = null, TimeProvider? clock = null, Action<string>? diagnostic = null)
    {
        _maximum = maximumDuration ?? TimeSpan.FromMinutes(5);
        if (_maximum <= TimeSpan.Zero || _maximum.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(maximumDuration), "A finite positive flight lifetime is required.");
        _clock = clock ?? TimeProvider.System;
        _diagnostic = diagnostic;
    }

    /// <summary>Compatibility overload for a factory without a cancellation parameter.</summary>
    public Task<TResult> RunAsync(TKey key, Func<Task<TResult>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return RunAsync(key, _ => factory(), static () => throw new TimeoutException("Shared operation deadline elapsed."), CancellationToken.None);
    }

    public async Task<TResult> RunAsync(TKey key, Func<CancellationToken, Task<TResult>> factory,
        Func<TResult> unavailable, CancellationToken cancellationToken, Action<TResult>? publish = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(unavailable);
        cancellationToken.ThrowIfCancellationRequested();
        Flight flight;
        State state;
        var start = false;
        var refused = false;
        var logRefusal = false;
        while (true)
        {
            state = _states.GetOrAdd(key, static _ => new State());
            lock (state.Gate)
            {
                if (!_states.TryGetValue(key, out var registered) || !ReferenceEquals(state, registered)) continue;
                if (state.Tombstone)
                {
                    Interlocked.Increment(ref _tombstoneRefusals);
                    logRefusal = !state.RefusalLogged; state.RefusalLogged = true;
                    refused = true; flight = null!;
                }
                else if (state.Current is { } current) flight = current;
                else
                {
                    flight = new Flight(factory, unavailable, publish);
                    state.Current = flight;
                    state.Running.Add(flight);
                    var ownedState = state;
                    var ownedFlight = flight;
                    flight.Timer = _clock.CreateTimer(_ => Expire(ownedState, ownedFlight), null, _maximum, Timeout.InfiniteTimeSpan);
                    _ = flight.Result.Task.ContinueWith(static task => { _ = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    start = true;
                }
                break;
            }
        }
        if (refused)
        {
            if (logRefusal) Diagnose("tombstone_refused");
            cancellationToken.ThrowIfCancellationRequested();
            return unavailable();
        }
        if (start) _ = Task.Run(() => ExecuteAsync(key, state, flight));
        return await flight.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Expire(State state, Flight flight)
    {
        lock (state.Gate)
        {
            if (flight.Settled || flight.Expired) return;
            flight.Expired = true;
            flight.Timer?.Dispose();
            if (ReferenceEquals(state.Current, flight)) state.Current = null;
            state.Tombstone = state.Running.Count >= 2;
            Interlocked.Increment(ref _expired);
            try { flight.Result.TrySetResult(flight.Unavailable()); }
            catch (Exception exception) { flight.Result.TrySetException(exception); }
        }
        Diagnose("deadline_expired");
        // Provider cancellation callbacks may block. Never invoke them under the key lock,
        // on the timer callback, or on a caller's wait path.
        _ = Task.Run(async () =>
        {
            try { await flight.Lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception) { Diagnose("cancellation_callback_failed"); }
            finally
            {
                lock (state.Gate)
                {
                    flight.CancellationCompleted = true;
                    if (flight.Settled) flight.Lifetime.Dispose();
                }
            }
        });
    }

    private async Task ExecuteAsync(TKey key, State state, Flight flight)
    {
        TResult? value = default;
        Exception? error = null;
        try
        {
            flight.Lifetime.Token.ThrowIfCancellationRequested();
            value = await flight.Factory(flight.Lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { error = exception; }
        bool detached;
        lock (state.Gate)
        {
            flight.Settled = true;
            flight.Timer?.Dispose();
            detached = flight.Expired;
            if (!detached)
            {
                if (error is OperationCanceledException canceled) flight.Result.TrySetCanceled(canceled.CancellationToken);
                else if (error is not null) flight.Result.TrySetException(error);
                else
                {
                    // Publish once before releasing this generation, closing the cache-miss gap
                    // between provider settlement and waiter continuations writing the cache.
                    try { flight.Publish?.Invoke(value!); flight.Result.TrySetResult(value!); }
                    catch (Exception exception) { flight.Result.TrySetException(exception); }
                }
            }
            // Identity is the generation guard: a detached execution cannot clear a newer flight.
            if (ReferenceEquals(state.Current, flight)) state.Current = null;
            state.Running.Remove(flight);
            if (state.Running.Count < 2) { state.Tombstone = false; state.RefusalLogged = false; }
            if (state.Running.Count == 0)
                ((ICollection<KeyValuePair<TKey, State>>)_states).Remove(new(key, state));
            if (!detached || flight.CancellationCompleted) flight.Lifetime.Dispose();
        }
        if (detached) { Interlocked.Increment(ref _lateCompletions); Diagnose("detached_settled"); }
    }

    private void Diagnose(string kind)
    {
        try { _diagnostic?.Invoke(kind); }
        catch (Exception) { /* Diagnostics never control shared work or key eviction. */ }
    }
}

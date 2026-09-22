using System.Collections.Concurrent;

namespace StyloMail.Assessment.Semantic;

/// <summary>
/// Collapses concurrent identical calls into one execution of the underlying operation.
/// </summary>
/// <remarks>
/// <b>Why this is not the same thing as the cache.</b> A cache removes a provider call that has
/// already happened; single-flight removes one that is happening now. Without it, a campaign
/// arriving as fifty simultaneous copies — which is precisely what a campaign is — spends fifty
/// provider calls to learn one answer, and the duplicates arrive faster than any cache can be
/// populated. The two mechanisms compose: single-flight bounds concurrent work, the cache bounds
/// repeated work.
///
/// <para>
/// The first caller to claim a key performs the work; everyone else awaits that same task and
/// receives the same result. Failures are shared too — a group of identical requests failing
/// together is the honest outcome of one failing provider call, and retrying each one separately
/// would multiply load on a provider that is already struggling.
/// </para>
/// </remarks>
public sealed class SingleFlight<TKey, TResult>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Task<TResult>> _inFlight = new();

    /// <summary>Callers currently awaiting a shared result, including the one doing the work.</summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>
    /// Runs <paramref name="factory"/> once for <paramref name="key"/>, however many callers ask
    /// for it concurrently.
    /// </summary>
    /// <param name="key">Identifies the work. Identical keys share one execution.</param>
    /// <param name="factory">The work. Invoked at most once per flight.</param>
    public async Task<TResult> RunAsync(TKey key, Func<Task<TResult>> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        while (true)
        {
            if (_inFlight.TryGetValue(key, out var joined))
            {
                return await joined.ConfigureAwait(false);
            }

            // RunContinuationsAsynchronously: the winner completes other callers' continuations
            // while holding no lock, so a slow continuation cannot stall a queued follower.
            var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_inFlight.TryAdd(key, completion.Task))
            {
                // Another caller claimed the key between the lookup and the insert. Loop and join
                // theirs rather than running the work twice.
                continue;
            }

            try
            {
                var result = await factory().ConfigureAwait(false);
                completion.SetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
                throw;
            }
            finally
            {
                // Removed after the task is completed, so a follower that read the task during the
                // flight still observes the result. Only a caller arriving after this point starts
                // a new flight, which is the correct reading of "the previous one is over".
                _inFlight.TryRemove(key, out _);
            }
        }
    }
}

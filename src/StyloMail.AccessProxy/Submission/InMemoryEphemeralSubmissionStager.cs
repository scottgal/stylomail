namespace StyloMail.AccessProxy.Submission;

/// <summary>In-memory bounded stager for tests and local harnesses, never durable storage.</summary>
public sealed class InMemoryEphemeralSubmissionStager : IEphemeralSubmissionStager
{
    private int _active;

    /// <summary>Number of undisposed handles, exposed so deletion-path tests can assert it reaches zero.</summary>
    public int ActiveCount => Volatile.Read(ref _active);

    public ValueTask<IStagedProxySubmission> CreateAsync(long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _active);
        return ValueTask.FromResult<IStagedProxySubmission>(new Handle(this, maximumBytes));
    }

    public ValueTask DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private sealed class Handle : IStagedProxySubmission
    {
        private readonly InMemoryEphemeralSubmissionStager _owner;
        private readonly MemoryStream _content = new();
        private int _disposed;

        public Handle(InMemoryEphemeralSubmissionStager owner, long maximumBytes)
        {
            _owner = owner;
            MaximumBytes = maximumBytes;
        }

        public long MaximumBytes { get; }

        public Stream Content => _content;

        public long Length => _content.Length;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _content.GetBuffer().AsSpan(0, checked((int)_content.Length)).Clear();
                _content.Dispose();
                Interlocked.Decrement(ref _owner._active);
            }
            return ValueTask.CompletedTask;
        }
    }
}

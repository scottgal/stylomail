namespace StyloMail.AccessProxy.Sessions;

/// <summary>Owns one accepted socket's slot until its entire connection has been disposed.</summary>
internal sealed class SessionAdmission(SessionLimiter limiter, SessionLease lease) : IDisposable
{
    private int _claimed;
    private int _disposed;

    internal SessionLease? Borrow(SessionLimiter consumer)
        => ReferenceEquals(consumer, limiter) && Volatile.Read(ref _disposed) == 0
            && Interlocked.CompareExchange(ref _claimed, 1, 0) == 0
            ? new SessionLease(static () => { }) : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) lease.Dispose();
    }
}

/// <summary>A transparent channel view carrying admission without ambient execution context.</summary>
internal class AdmittedDuplexChannel(IDuplexChannel inner, SessionAdmission admission) : IDuplexChannel
{
    internal SessionAdmission Admission => admission;
    public Stream Input => inner.Input;
    public Stream Output => inner.Output;
    public string Description => inner.Description;
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    internal static IDuplexChannel Wrap(IDuplexChannel channel, SessionAdmission admission)
        => channel switch
        {
            IStartTlsCapableChannel startTls => new AdmittedStartTlsChannel(startTls, admission),
            ISecureDuplexChannel secure => new AdmittedSecureChannel(secure, admission),
            _ => new AdmittedDuplexChannel(channel, admission),
        };

    internal static IDuplexChannel Inherit(IDuplexChannel parent, IDuplexChannel channel)
        => parent is AdmittedDuplexChannel admitted ? Wrap(channel, admitted.Admission) : channel;

    private sealed class AdmittedSecureChannel : AdmittedDuplexChannel, ISecureDuplexChannel
    {
        private readonly ISecureDuplexChannel _secure;
        internal AdmittedSecureChannel(ISecureDuplexChannel inner, SessionAdmission admission) : base(inner, admission)
            => _secure = inner;
        public bool IsSecure => _secure.IsSecure;
    }

    private sealed class AdmittedStartTlsChannel : AdmittedDuplexChannel, IStartTlsCapableChannel
    {
        private readonly IStartTlsCapableChannel _startTls;
        internal AdmittedStartTlsChannel(IStartTlsCapableChannel inner, SessionAdmission admission) : base(inner, admission)
            => _startTls = inner;
        public async ValueTask<ISecureDuplexChannel> UpgradeToTlsAsync(CancellationToken cancellationToken)
            => (ISecureDuplexChannel)Wrap(await _startTls.UpgradeToTlsAsync(cancellationToken).ConfigureAwait(false), Admission);
    }
}

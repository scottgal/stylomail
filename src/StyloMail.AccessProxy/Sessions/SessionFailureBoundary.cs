using System.Net.Sockets;
using StyloMail.AccessProxy.Hosting;

namespace StyloMail.AccessProxy.Sessions;

internal static class SessionFailureBoundary
{
    internal static SessionOutcome? BackendOutcome(Exception exception, CancellationToken caller)
        => exception switch
        {
            SocketException => SessionOutcome.BackendUnavailable,
            AccessProxyTimeoutException or TimeoutException => SessionOutcome.Timeout,
            OperationCanceledException when !caller.IsCancellationRequested => SessionOutcome.Timeout,
            _ => null,
        };

    internal static void Report(IProxySessionContextSink? sink, string stage, Exception exception, SessionOutcome? outcome)
    {
        if (sink is not IProxySessionFailureSink diagnostics) return;
        if (exception is AccessProxyTimeoutException { DuringBackendAuthentication: true })
        {
            stage = "backend_authentication";
        }
        for (var depth = 0; depth < 8 && exception is AccessProxyTimeoutException && exception.InnerException is not null; depth++)
            exception = exception.InnerException;
        var type = exception.GetType().Name;
        var socket = exception as SocketException ?? exception.InnerException as SocketException;
        try { diagnostics.OnFailure(stage, type[..Math.Min(type.Length, 64)], socket?.SocketErrorCode.ToString(), outcome); }
        catch (Exception) { /* Diagnostics cannot change a protocol outcome or cleanup. */ }
    }
}

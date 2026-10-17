using System.Net;
using System.Security.Cryptography.X509Certificates;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Hosting;

public enum ProxyListenerProtocol { Imap, Pop3, Smtp }
public enum ProxyListenerEventKind { Started, Stopped, Faulted }
public enum ProxyListenerFaultReason { None, CertificateUnavailable, CertificateInvalid, BindFailed, AcceptLoopFaulted, StartupConfigurationInvalid }
public enum ProxySessionEventKind { Opened, Closed }
public enum ProxySessionDirection { ClientToProxy, ProxyToBackend }
public enum ProxyBoundReason { None, AuthenticationTimeout, IdleTimeout, MaximumDuration, SubmissionLimit }
public enum ProxyTlsPolicy { Required, LoopbackHarnessPlaintext }

/// <summary>Host configuration for exactly one tenant-bound protocol listener.</summary>
public sealed record ProxyListenerOptions
{
    public required string TenantId { get; init; }
    public required IPAddress BindAddress { get; init; }
    public required int Port { get; init; }
    public required ProxyListenerProtocol Protocol { get; init; }
    public string? CertificateReference { get; init; }
    /// <summary>SMTP requires TLS; plaintext is available only for an explicit loopback harness.</summary>
    public required ProxyTlsPolicy TlsPolicy { get; init; }
    public bool Enabled { get; init; }
}

public sealed record ProxyListenerEvent
{
    public required ProxyListenerEventKind Kind { get; init; }
    public required string TenantId { get; init; }
    public required ProxyListenerProtocol Protocol { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public int? BoundPort { get; init; }
    public ProxyListenerFaultReason FaultReason { get; init; }
    /// <summary>Short bounded safe detail, never exception text, endpoint, path, login or credential.</summary>
    public string? SafeDetail { get; init; }
}

/// <summary>Bounded, header-only thread correlation metadata for one proxied message.</summary>
public sealed record ProxyThreadingEvent
{
    public required string TenantId { get; init; }
    public required string AccountId { get; init; }
    public required ProxyListenerProtocol Protocol { get; init; }
    public required string MessageToken { get; init; }
    public required string ThreadToken { get; init; }
    public required int ReferenceDepth { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public sealed record ProxySessionEvent
{
    public required ProxySessionEventKind Kind { get; init; }
    public required string SessionId { get; init; }
    public required string TenantId { get; init; }
    public string? AccountId { get; init; }
    public required ProxyListenerProtocol Protocol { get; init; }
    public required ProxySessionDirection Direction { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public SessionOutcome? Outcome { get; init; }
    public long ClientBytes { get; init; }
    public long BackendBytes { get; init; }
    public ProxyBoundReason BoundReason { get; init; }
}

public interface IProxyListenerEventSink
{
    ValueTask PublishAsync(ProxyListenerEvent listenerEvent, CancellationToken cancellationToken);
    ValueTask PublishAsync(ProxySessionEvent sessionEvent, CancellationToken cancellationToken);
}

public interface IProxySessionContextSink
{
    ValueTask OnAuthenticatedAsync(string accountId, DateTimeOffset authenticatedAt, CancellationToken cancellationToken);
}

/// <summary>Message traffic category, distinct from <see cref="ProxySessionDirection"/> transport flow.</summary>
public enum MessageTrafficKind { Retrieval, Submission }
public enum ProxyWarningMarkerKind { AuthenticationFailed, LinkDomainMismatch, DisplayNameMismatch, AttachmentTypeMismatch }
public enum ProxyWarningAvailability { Available, NotProduced }
public enum ProxyWarningAvailabilityReason { None, NoAnalyserConfigured, UnreadableMessage, UnsupportedContent }

public sealed record ProxyDeterministicWarningEvent
{
    public required string TenantId { get; init; }
    public required string AccountId { get; init; }
    public required MessageTrafficKind TrafficKind { get; init; }
    public required string MessageToken { get; init; }
    public required string ThreadToken { get; init; }
    public required ProxyWarningMarkerKind MarkerKind { get; init; }
    public required string FindingId { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required ProxyWarningAvailability Availability { get; init; }
    public required ProxyWarningAvailabilityReason AvailabilityReason { get; init; }
}

public sealed record ProxyWarningAnalysisOutcome
{
    public required string TenantId { get; init; }
    public required string AccountId { get; init; }
    public required MessageTrafficKind TrafficKind { get; init; }
    public required string MessageToken { get; init; }
    public required string ThreadToken { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required ProxyWarningAvailability Availability { get; init; }
    public required ProxyWarningAvailabilityReason AvailabilityReason { get; init; }
    public required int MarkerCount { get; init; }
}

/// <summary>
/// Receives derived deterministic-warning facts only. Implementations must not retain source
/// message content, headers or credentials.
/// </summary>
public interface IProxyWarningEventSink
{
    ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken cancellationToken);

    ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken cancellationToken);
}

public sealed record ProxyWarningAnalysisInput
{
    public required ReadOnlyMemory<byte> Message { get; init; }
    public required string TenantId { get; init; }
    public required string AccountId { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
}

public sealed record ProxyWarningFinding
{
    public required ProxyWarningMarkerKind MarkerKind { get; init; }
    public required string FindingId { get; init; }
}

/// <summary>Host-composed bounded local analysis. It derives tokens and never retains input bytes.</summary>
public interface IProxyWarningAnalyzer
{
    ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(
        ProxyWarningAnalysisInput input,
        CancellationToken cancellationToken);
}

public sealed record ProxyWarningAnalysisResult
{
    public required string MessageToken { get; init; }
    public required string ThreadToken { get; init; }
    public required ProxyWarningAvailability Availability { get; init; }
    public required ProxyWarningAvailabilityReason AvailabilityReason { get; init; }
    public required IReadOnlyList<ProxyWarningFinding> Findings { get; init; }
}

public interface IProxyCertificateResolver
{
    ValueTask<X509Certificate2?> ResolveAsync(string certificateReference, CancellationToken cancellationToken);
}

/// <summary>Hosting-shaped lifecycle surface; ingress wires this to its host runtime.</summary>
public interface IProxyListenerHostedService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

using System.Net;

namespace StyloMail.Host.Hosting;

/// <summary>
/// Whether the semantic provider has rejected this deployment's credential.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because a rotated key is a failure that looks like a healthy deployment.</b> The Jev
/// adapter throws loudly on a `401` — deliberately, since a revoked key must never present as a calm
/// inbox — but nothing connected that loudness to the thing an operator actually watches. The result
/// was the worst shape available: `/health/ready` answering `200 ready` while every assessment failed,
/// so a load balancer kept routing mail to a host that could not assess any of it.
/// </para>
/// <para>
/// <b>It latches, and only a success clears it.</b> A `401` from the provider is not transient — it
/// means the credential is wrong — so treating it as a blip that might resolve would put the host back
/// to advertising itself ready while still failing every message. The state therefore stays rejected
/// until a classification actually succeeds, which cannot happen while the key is bad. Failing closed
/// is the only direction that cannot produce the false-healthy state this was written to remove.
/// </para>
/// <para>
/// Nothing here is message content: a status code and an instant. The instant is kept so an operator
/// can tell "this broke just now" from "this has been broken since the deploy", which is the question
/// the readiness answer alone cannot distinguish.
/// </para>
/// </remarks>
public sealed class ProviderCredentialHealth
{
    private readonly Lock _gate = new();

    private bool _rejected;
    private HttpStatusCode _status;
    private DateTimeOffset _since;

    /// <summary>True once the provider has rejected this deployment's credential.</summary>
    public bool IsRejected
    {
        get
        {
            lock (_gate)
            {
                return _rejected;
            }
        }
    }

    /// <summary>When the most recent rejection happened, or null when the credential is fine.</summary>
    public DateTimeOffset? RejectedSince
    {
        get
        {
            lock (_gate)
            {
                return _rejected ? _since : null;
            }
        }
    }

    /// <summary>The status the provider answered with, for the operator — never served to a caller.</summary>
    public HttpStatusCode? RejectedWith
    {
        get
        {
            lock (_gate)
            {
                return _rejected ? _status : null;
            }
        }
    }

    /// <summary>Records that the provider rejected the credential.</summary>
    public void RecordRejected(HttpStatusCode status, DateTimeOffset now)
    {
        lock (_gate)
        {
            _rejected = true;
            _status = status;
            _since = now;
        }
    }

    /// <summary>
    /// Records that a classification succeeded, which is the only thing that clears a rejection.
    /// </summary>
    /// <remarks>
    /// Called on every success rather than only on a transition, so the state cannot get stuck
    /// rejected after a credential is fixed by a restart or a corrected configuration. The cost is one
    /// uncontended lock acquisition on a path that has just made an HTTP call.
    /// </remarks>
    public void RecordAccepted()
    {
        lock (_gate)
        {
            _rejected = false;
        }
    }
}

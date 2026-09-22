namespace StyloMail.Desktop.Models;

/// <summary>Which intervention is being asked for.</summary>
public enum ActionKind
{
    /// <summary>Stop an authenticated principal's outbound delivery.</summary>
    PauseSender,

    /// <summary>Lift a pause. The audited mirror, and the same privilege.</summary>
    ResumeSender,

    /// <summary>Release a quarantined message so it is delivered.</summary>
    ReleaseQuarantine,
}

/// <summary>
/// An action the operator has asked for but not yet confirmed.
/// </summary>
/// <remarks>
/// <b>Asking and doing are separate steps, and this type is the gap between
/// them.</b> Every action the console offers is consequential: a pause stops an
/// account's mail, and a release delivers mail that was deliberately held. A
/// console that performed them on a single click would be one mis-click away
/// from either, and the mistake would be invisible afterwards because the
/// ledger records only that it happened.
///
/// <para>
/// <see cref="Consequence"/> exists so the confirmation says what will happen
/// rather than naming the button again. "Pause compromised@example.test" is the
/// label; "outbound mail from this account will stop being delivered" is the
/// thing the operator is agreeing to.
/// </para>
/// </remarks>
public sealed record ActionRequest
{
    public required ActionKind Kind { get; init; }

    /// <summary>The short label for the action, naming its target.</summary>
    public required string Title { get; init; }

    /// <summary>What will actually happen, in plain words.</summary>
    public required string Consequence { get; init; }

    /// <summary>
    /// The audit reason. Required for every action here.
    /// </summary>
    /// <remarks>
    /// The Host accepts an empty one on the pause and resume routes. The
    /// console does not: the reason is the audit record, and an intervention
    /// applied without one is, months later, indistinguishable from one nobody
    /// explained. A scripted caller can still send none.
    /// </remarks>
    public required string Reason { get; init; }

    /// <summary>The queue id, for a quarantine release.</summary>
    public string? QueueId { get; init; }

    /// <summary>The principal id, for a sender control.</summary>
    public string? PrincipalId { get; init; }
}

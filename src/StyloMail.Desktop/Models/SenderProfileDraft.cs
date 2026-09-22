using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Models;

/// <summary>
/// An operator's description of a sender, being edited.
/// </summary>
/// <remarks>
/// <b>The whole reason this is a type rather than a form full of bindings.</b>
/// A settings write is a <em>full replace</em>: every field the request omits is
/// cleared. So a form that sent only the fields it displays would silently erase
/// everything else the moment someone edited a note — the company, the external
/// reference, the notification target, the posture, all gone, with nothing on
/// screen having said so.
///
/// <para>
/// Building the request here, from a draft that was itself built from a full
/// response, makes that impossible rather than merely discouraged: the fields
/// the form does not show are still carried, because they were never dropped in
/// the first place.
/// </para>
/// </remarks>
public sealed class SenderProfileDraft : ObservableObject
{
    private string? _label;
    private string? _companyId;
    private string? _notes;
    private string? _externalRef;
    private string? _notificationTarget;
    private string? _posture;

    private SenderProfileDraft(string principalId) => PrincipalId = principalId;

    public string PrincipalId { get; }

    /// <summary>Whether anyone had described this sender before this edit began.</summary>
    public bool WasDescribed { get; private init; }

    /// <summary>
    /// The stance as it was stored, which is not always one this build offers.
    /// </summary>
    /// <remarks>
    /// Kept so the difference between "stored and editable" and "stored and not
    /// understood" can be reported. See <see cref="UnrecognisedPosture"/>.
    /// </remarks>
    private string? StoredPosture { get; init; }

    public string? Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public string? CompanyId
    {
        get => _companyId;
        set => Set(ref _companyId, value);
    }

    public string? Notes
    {
        get => _notes;
        set => Set(ref _notes, value);
    }

    public string? ExternalRef
    {
        get => _externalRef;
        set => Set(ref _externalRef, value);
    }

    public string? NotificationTarget
    {
        get => _notificationTarget;
        set => Set(ref _notificationTarget, value);
    }

    /// <summary>
    /// The stance, from the closed set the Host accepts.
    /// </summary>
    /// <remarks>
    /// Null clears it. Anything else must be one of
    /// <see cref="SenderPosture"/>'s names, because the Host refuses the rest by
    /// name — and a control that could produce a refused value would fail in
    /// front of an operator for something knowable at the call site.
    /// </remarks>
    public string? Posture
    {
        get => _posture;
        set
        {
            if (value is not null && !SenderPosture.IsKnown(value))
            {
                throw new ArgumentException(
                    $"'{value}' is not a posture this console can set.", nameof(value));
            }

            Set(ref _posture, value);
        }
    }

    /// <summary>
    /// A stance that was stored but is not one this build recognises.
    /// </summary>
    /// <remarks>
    /// <b>The case that makes a round trip dangerous.</b> A newer Host can store
    /// a stance this build has never heard of. Reading it is fine. Writing the
    /// profile back with every other field edited would send that value, the
    /// Host would refuse the whole write with a 400, and the operator would see
    /// an unexplained failure while editing a note.
    ///
    /// <para>
    /// So the draft reports it and refuses to submit, rather than dropping it
    /// (which would silently discard somebody's decision) or sending it (which
    /// would fail for a reason the screen cannot explain).
    /// </para>
    /// </remarks>
    public string? UnrecognisedPosture => StoredPosture is not null && !SenderPosture.IsKnown(StoredPosture)
        ? StoredPosture
        : null;

    public bool HasUnrecognisedPosture => UnrecognisedPosture is not null;

    /// <summary>
    /// Whether this profile can be saved.
    /// </summary>
    /// <remarks>
    /// False while a stored value this build cannot write is present, because
    /// saving would clear it. That is not a nicety: clearing it would be the
    /// console deciding, silently, that a stance somebody recorded does not
    /// exist.
    /// </remarks>
    public bool CanSave => !HasUnrecognisedPosture;

    /// <summary>Why it cannot be saved, when it cannot. Null when it can.</summary>
    public string? SaveRefusal => HasUnrecognisedPosture
        ? $"This sender's posture is '{UnrecognisedPosture}', which this build does not recognise. "
            + "Saving would clear it, so the console will not. Update the console, or clear the "
            + "posture from a client that understands it."
        : null;

    /// <summary>Builds a draft from what the Host holds.</summary>
    public static SenderProfileDraft From(SenderSettingsResponse settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var draft = new SenderProfileDraft(settings.PrincipalId)
        {
            WasDescribed = settings.IsDescribed,
            StoredPosture = settings.Posture,
            _label = settings.Label,
            _companyId = settings.CompanyId,
            _notes = settings.Notes,
            _externalRef = settings.ExternalRef,
            _notificationTarget = settings.NotificationTarget,
        };

        // A posture this build knows is offered for editing; one it does not is
        // carried but never placed in the editable field, so a stray keystroke
        // cannot turn an unrecognised value into a different one.
        if (SenderPosture.IsKnown(settings.Posture))
        {
            draft._posture = settings.Posture;
        }

        return draft;
    }

    /// <summary>
    /// Builds the request. <b>Every field, always.</b>
    /// </summary>
    /// <remarks>
    /// The request is a full replace, so this sends the complete profile
    /// including the fields a particular form does not show. A caller that
    /// assembled its own request from the visible fields would clear the rest,
    /// which is the bug this type exists to prevent.
    /// </remarks>
    public SenderSettingsRequest ToRequest() => new()
    {
        Label = Clean(Label),
        CompanyId = Clean(CompanyId),
        Notes = Clean(Notes),
        ExternalRef = Clean(ExternalRef),
        NotificationTarget = Clean(NotificationTarget),
        Posture = Posture,
    };

    /// <summary>Blank is absent, not a value.</summary>
    /// <remarks>
    /// A field an operator emptied means "clear this", which null says. Sending
    /// an empty string instead would store a value that reads, later, like
    /// somebody meant to type something.
    /// </remarks>
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

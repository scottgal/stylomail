using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// Editing a sender's profile.
/// </summary>
/// <remarks>
/// The whole of this file is about one hazard. A settings write is a full
/// replace, so a form that sends only the fields it displays erases everything
/// else: silently, and without anything on screen having mentioned it. Most of
/// these tests exist to make that impossible rather than merely unlikely.
/// </remarks>
public sealed class SenderProfileDraftTests
{
    private static SenderSettingsResponse Described => Json.Read<SenderSettingsResponse>(Wire.SenderSettings);

    private static SenderSettingsResponse Undescribed => Json.Read<SenderSettingsResponse>(Wire.SenderSettingsUndescribed);

    /// <summary>
    /// The one that matters. Editing one field sends all of them.
    /// </summary>
    /// <remarks>
    /// A form showing only a label would produce a request whose other five
    /// fields are absent, and absent means cleared. This asserts the round trip
    /// carries everything, so no form built on this type can erase a company.
    /// </remarks>
    [Fact]
    public void Editing_one_field_carries_every_other_one()
    {
        var draft = SenderProfileDraft.From(Described);

        draft.Label = "Acme marketing";

        var request = draft.ToRequest();

        Assert.Equal("Acme marketing", request.Label);
        Assert.Equal("co_7f3a", request.CompanyId);
        Assert.Equal("Primary marketing account", request.Notes);
        Assert.Equal("crm-99213", request.ExternalRef);
        Assert.Equal("ops@acme.test", request.NotificationTarget);
        Assert.Equal(SenderPosture.Watch, request.Posture);
    }

    /// <summary>Nothing edited means nothing changed, and the request still carries the profile.</summary>
    [Fact]
    public void Saving_without_editing_is_a_faithful_round_trip()
    {
        var request = SenderProfileDraft.From(Described).ToRequest();

        Assert.Equal("Acme outbound", request.Label);
        Assert.Equal("co_7f3a", request.CompanyId);
        Assert.Equal("crm-99213", request.ExternalRef);
    }

    /// <summary>An undescribed sender round-trips to a request that clears nothing it did not have.</summary>
    [Fact]
    public void An_undescribed_sender_round_trips_to_an_empty_profile()
    {
        var draft = SenderProfileDraft.From(Undescribed);

        Assert.False(draft.WasDescribed);
        Assert.False(draft.HasUnrecognisedPosture);

        var request = draft.ToRequest();

        Assert.Null(request.Label);
        Assert.Null(request.CompanyId);
        Assert.Null(request.Posture);
    }

    /// <summary>Clearing a field means clearing it, and blank is the same as absent.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_emptied_field_is_sent_as_absent(string blank)
    {
        var draft = SenderProfileDraft.From(Described);

        draft.Notes = blank;

        Assert.Null(draft.ToRequest().Notes);
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var draft = SenderProfileDraft.From(Described);

        draft.ExternalRef = "  crm-99213  ";

        Assert.Equal("crm-99213", draft.ToRequest().ExternalRef);
    }

    // ===================== the posture, which is a closed set =====================

    /// <summary>A value the Host would refuse is refused here, at the call site.</summary>
    [Fact]
    public void A_posture_the_host_does_not_know_cannot_be_typed_in()
    {
        var draft = SenderProfileDraft.From(Described);

        Assert.Throws<ArgumentException>(() => draft.Posture = "gold");
        Assert.Throws<ArgumentException>(() => draft.Posture = "Trusted");
    }

    [Theory]
    [InlineData(SenderPosture.Trusted)]
    [InlineData(SenderPosture.Normal)]
    [InlineData(SenderPosture.Watch)]
    public void Every_posture_the_host_accepts_can_be_set(string posture)
    {
        var draft = SenderProfileDraft.From(Described);

        draft.Posture = posture;

        Assert.Equal(posture, draft.ToRequest().Posture);
    }

    [Fact]
    public void A_posture_can_be_cleared()
    {
        var draft = SenderProfileDraft.From(Described);

        draft.Posture = null;

        Assert.Null(draft.ToRequest().Posture);
    }

    /// <summary>
    /// A stance stored by a newer Host is kept, reported, and refuses to save.
    /// </summary>
    /// <remarks>
    /// This is the round trip that would otherwise be dangerous. The value reads
    /// fine; writing the profile back with a note edited would send it, the Host
    /// would refuse the whole write by name, and the operator would see an
    /// unexplained failure while editing something unrelated.
    ///
    /// <para>
    /// The alternatives are both worse. Dropping it silently discards a decision
    /// somebody recorded. Sending it fails for a reason the screen cannot
    /// explain. So it is carried, the screen says so, and saving is refused with
    /// the reason attached.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_posture_from_a_newer_host_is_reported_and_blocks_saving()
    {
        var settings = Described with { Posture = "federated" };

        var draft = SenderProfileDraft.From(settings);

        Assert.True(draft.HasUnrecognisedPosture);
        Assert.Equal("federated", draft.UnrecognisedPosture);
        Assert.False(draft.CanSave);

        Assert.NotNull(draft.SaveRefusal);
        Assert.Contains("federated", draft.SaveRefusal, StringComparison.Ordinal);

        // And it is not in the editable field, so a keystroke cannot turn one
        // unrecognised value into a different one.
        Assert.Null(draft.Posture);

        // The rest of the profile still round-trips, so the reason for refusing
        // is the posture alone rather than the whole profile being unusable.
        Assert.Equal("Acme outbound", draft.ToRequest().Label);
        Assert.Equal("co_7f3a", draft.ToRequest().CompanyId);
    }

    [Fact]
    public void A_recognised_posture_blocks_nothing()
    {
        var draft = SenderProfileDraft.From(Described);

        Assert.True(draft.CanSave);
        Assert.Null(draft.SaveRefusal);
    }

    /// <summary>A posture the Host sent as absent is not an unrecognised one.</summary>
    [Fact]
    public void No_posture_is_not_an_unrecognised_posture()
    {
        var draft = SenderProfileDraft.From(Described with { Posture = null });

        Assert.False(draft.HasUnrecognisedPosture);
        Assert.True(draft.CanSave);
    }

    /// <summary>The draft is a draft: the source response is not mutated.</summary>
    [Fact]
    public void Editing_does_not_touch_what_the_host_sent()
    {
        var settings = Described;
        var draft = SenderProfileDraft.From(settings);

        draft.Label = "changed";

        Assert.Equal("Acme outbound", settings.Label);
    }
}

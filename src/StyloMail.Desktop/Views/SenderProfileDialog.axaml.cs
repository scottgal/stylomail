using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Views;

/// <summary>One entry in the company picker.</summary>
/// <remarks>
/// A record rather than binding the combo straight at the company list, because
/// "no company" is a real choice and an empty selection is not: an operator has
/// to be able to take a sender out of a company, and a combo whose only empty
/// state is "nothing selected" cannot express that.
/// </remarks>
public sealed record CompanyChoice(string? CompanyId, string Name)
{
    /// <summary>The name, not the record's shape.</summary>
    /// <remarks>
    /// A ComboBox renders its items with ToString, and a record's default is
    /// its whole member list: the picker read
    /// "CompanyChoice { CompanyId = co_d2c8..., Name = Acme }". Overriding
    /// this is the smallest fix and keeps the type a plain value.
    /// </remarks>
    public override string ToString() => Name;
}

/// <summary>One entry in the posture picker.</summary>
public sealed record PostureChoice(string? Posture, string Name)
{
    /// <summary>The name, for the same reason as <see cref="CompanyChoice"/>.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Edits an operator's description of a sender.
/// </summary>
/// <remarks>
/// The form is a thin shell over <see cref="SenderProfileDraft"/>, which is
/// where the rules live: the whole profile round-trips, blank means cleared, and
/// a stored value this build cannot write blocks saving rather than being
/// silently dropped. None of that is re-implemented here.
/// </remarks>
public partial class SenderProfileDialog : Window
{
    /// <summary>What was saved, so the caller can reload.</summary>
    public SenderSettingsResponse? Saved { get; private set; }

    public SenderProfileDialog()
        : this(SenderProfileDraft.From(new SenderSettingsResponse { PrincipalId = "preview" }), [])
    {
    }

    public SenderProfileDialog(SenderProfileDraft draft, IReadOnlyList<CompanyResponse> companies)
    {
        Draft = draft;

        InitializeComponent();

        PrincipalText.Text = draft.PrincipalId;

        LabelBox.Text = draft.Label ?? string.Empty;
        NotesBox.Text = draft.Notes ?? string.Empty;
        ExternalRefBox.Text = draft.ExternalRef ?? string.Empty;
        NotificationTargetBox.Text = draft.NotificationTarget ?? string.Empty;

        CompanyBox.ItemsSource = Choices(companies);
        CompanyBox.SelectedItem = ((IReadOnlyList<CompanyChoice>)CompanyBox.ItemsSource)
            .FirstOrDefault(choice => choice.CompanyId == draft.CompanyId)
            ?? ((IReadOnlyList<CompanyChoice>)CompanyBox.ItemsSource)[^1];

        PostureBox.ItemsSource = Postures();
        PostureBox.SelectedItem = ((IReadOnlyList<PostureChoice>)PostureBox.ItemsSource)
            .FirstOrDefault(choice => choice.Posture == draft.Posture)
            ?? ((IReadOnlyList<PostureChoice>)PostureBox.ItemsSource)[0];

        ShowRefusal(draft.SaveRefusal);
        SaveProfileButton.IsEnabled = draft.CanSave;
    }

    /// <summary>The draft being edited, exposed so the caller can read it back.</summary>
    public SenderProfileDraft Draft { get; }

    /// <summary>
    /// The companies to offer, with "no company" last.
    /// </summary>
    /// <remarks>
    /// Last rather than first because it is the minority choice, and because a
    /// list that opens on "no company" invites filing a sender nowhere by
    /// accepting the default.
    /// </remarks>
    private static IReadOnlyList<CompanyChoice> Choices(IReadOnlyList<CompanyResponse> companies) =>
    [
        .. companies
            .OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase)
            .Select(company => new CompanyChoice(company.CompanyId, company.Name)),
        new CompanyChoice(null, "No company"),
    ];

    /// <summary>The postures to offer, with "not set" first.</summary>
    private static IReadOnlyList<PostureChoice> Postures() =>
    [
        new PostureChoice(null, "Not set"),
        .. SenderPosture.All.Select(posture => new PostureChoice(posture, posture)),
    ];

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Draft.Label = LabelBox.Text;
        Draft.Notes = NotesBox.Text;
        Draft.ExternalRef = ExternalRefBox.Text;
        Draft.NotificationTarget = NotificationTargetBox.Text;
        Draft.CompanyId = (CompanyBox.SelectedItem as CompanyChoice)?.CompanyId;

        var chosen = (PostureBox.SelectedItem as PostureChoice)?.Posture;

        // The picker can only produce values from the closed set, so this cannot
        // throw. Setting it anyway rather than trusting that: if the picker is
        // ever populated from somewhere else, the refusal should be here.
        try
        {
            Draft.Posture = chosen;
        }
        catch (ArgumentException ex)
        {
            ShowRefusal(ex.Message);
            return;
        }

        Saved = new SenderSettingsResponse
        {
            PrincipalId = Draft.PrincipalId,
            Label = Draft.ToRequest().Label,
            CompanyId = Draft.ToRequest().CompanyId,
        };

        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void ShowRefusal(string? refusal)
    {
        SaveRefusalText.Text = refusal ?? string.Empty;
        SaveRefusalText.IsVisible = refusal is not null;
    }
}

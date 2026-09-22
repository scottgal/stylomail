using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Views;

/// <summary>One row in the company list.</summary>
/// <remarks>
/// Carries whether it is a real company or the "new" placeholder. A ListBox
/// whose only empty state is "nothing selected" cannot express "start a new
/// one", and clearing the form on a button instead loses whatever was typed in
/// it.
/// </remarks>
public sealed record CompanyRow(CompanyResponse? Company, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Manages the companies senders are filed under.
/// </summary>
/// <remarks>
/// <b>Operator-side only, and the screen says so.</b> Nothing in the assessment
/// pipeline reads a company, so this groups the console's own view rather than
/// changing how mail is judged. A screen that let an operator believe they had
/// changed a security posture by creating a group would be the same mistake as
/// an unlabelled posture field.
///
/// <para>
/// It talks to the client directly rather than through a draft type. The
/// full-replace hazard that made <see cref="Models.SenderProfileDraft"/>
/// necessary does not apply here: a company request carries only a name and
/// notes, and both are on screen at once.
/// </para>
/// </remarks>
public partial class CompaniesDialog : Window
{
    private readonly ICompanyStore _store;
    private IReadOnlyList<CompanyRow> _rows = [];

    public CompaniesDialog()
        : this(new EmptyCompanyStore(), [])
    {
    }

    public CompaniesDialog(ICompanyStore store, IReadOnlyList<CompanyResponse> companies)
    {
        _store = store;

        InitializeComponent();

        Apply(companies);
    }

    /// <summary>Whether anything was created or changed, so the caller can reload.</summary>
    public bool Changed { get; private set; }

    private void Apply(IReadOnlyList<CompanyResponse> companies)
    {
        // A field rather than read back out of the control: the placeholder row
        // is what "New" selects, and reaching into ItemsSource for it is the
        // kind of cast that works until somebody changes the control.
        List<CompanyRow> rows =
        [
            .. companies
                .OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase)
                .Select(company => new CompanyRow(company, company.Name)),
            new CompanyRow(null, "New company..."),
        ];

        _rows = rows;

        CompanyList.ItemsSource = rows;
        CompanyList.SelectedItem = rows[0];
        Show(rows[0]);
    }

    private void Show(CompanyRow row)
    {
        CompanyNameBox.Text = row.Company?.Name ?? string.Empty;
        CompanyNotesBox.Text = row.Company?.Notes ?? string.Empty;

        CompanyFormTitle.Text = row.Company is null ? "New company" : "Rename or re-note";
        // The id is shown because it is what a sender's profile records, and
        // because two companies can share a name.
        CompanyResultText.Text = row.Company is null
            ? "The Host mints the identifier, so there is nothing to choose here."
            : row.Company.CompanyId;
    }

    private void OnCompanySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (CompanyList.SelectedItem is CompanyRow row) Show(row);
    }

    private void OnNewClick(object? sender, RoutedEventArgs e)
    {
        // Selecting the placeholder rather than clearing the form, so the list
        // and the form cannot disagree about what is being edited.
        CompanyList.SelectedItem = _rows[^1];
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var selected = CompanyList.SelectedItem as CompanyRow;
        var name = CompanyNameBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            CompanyResultText.Text = "A company needs a name.";
            return;
        }

        try
        {
            if (selected?.Company is { } existing)
            {
                await _store.SaveAsync(existing.CompanyId, new CompanyRequest
                {
                    Name = name,
                    Notes = Blank(CompanyNotesBox.Text),
                }).ConfigureAwait(true);

                CompanyResultText.Text = $"Saved {name}.";
            }
            else
            {
                var created = await _store.CreateAsync(new CompanyRequest
                {
                    Name = name,
                    Notes = Blank(CompanyNotesBox.Text),
                }).ConfigureAwait(true);

                CompanyResultText.Text = $"Created {created.Name}.";
            }

            Changed = true;
            Apply(await _store.ListAsync().ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is Api.StyloMailApiException or ArgumentException)
        {
            // Reported rather than swallowed: an operator who pressed Save and
            // saw nothing change cannot tell a refusal from a no-op.
            CompanyResultText.Text = ex is Api.StyloMailApiException api
                ? $"Not saved. {api.Detail ?? api.Code ?? "The Host refused the request."}"
                : ex.Message;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Blank notes are absent, not an empty string.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The company operations the dialog needs.
/// </summary>
/// <remarks>
/// A seam rather than the client directly, so the dialog can be constructed by
/// the XAML loader and so its behaviour is testable without a Host. It exposes
/// exactly the three calls this screen makes and nothing else.
/// </remarks>
public interface ICompanyStore
{
    Task<IReadOnlyList<CompanyResponse>> ListAsync(CancellationToken cancellationToken = default);

    Task<CompanyResponse> CreateAsync(CompanyRequest request, CancellationToken cancellationToken = default);

    Task<CompanyResponse> SaveAsync(string companyId, CompanyRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A company store that holds nothing. For the XAML loader only.</summary>
internal sealed class EmptyCompanyStore : ICompanyStore
{
    public Task<IReadOnlyList<CompanyResponse>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CompanyResponse>>([]);

    public Task<CompanyResponse> CreateAsync(CompanyRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No company store is configured.");

    public Task<CompanyResponse> SaveAsync(string companyId, CompanyRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No company store is configured.");
}

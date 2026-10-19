using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The people directory. It owns no transport and no profile dialog; the window does.
/// </summary>
public partial class RecipientsView : UserControl
{
    public RecipientsView() => InitializeComponent();

    private RecipientsViewModel? Model => DataContext as RecipientsViewModel;

    private async void OnInboundClick(object? sender, RoutedEventArgs e) => await LoadKindAsync("recipient", "Inbound");

    private async void OnOutboundClick(object? sender, RoutedEventArgs e) => await LoadKindAsync("recipient", "Outbound");

    private async void OnSendersClick(object? sender, RoutedEventArgs e) => await LoadKindAsync("sender");

    private async void OnCompaniesClick(object? sender, RoutedEventArgs e) => await LoadKindAsync("company");

    private async Task LoadKindAsync(string kind, string? direction = null)
    {
        if (Model is not { } model) return;

        try
        {
            await model.LoadKindAsync(kind, direction);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Recipients] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnSearchClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        try
        {
            await model.SearchAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Recipients] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        try
        {
            await model.ClearSearchAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Recipients] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        try
        {
            await model.LoadAsync(append: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Recipients] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnProfileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecipientRowView row } && Model is { } model)
        {
            model.RequestProfile(row);
        }
    }
}

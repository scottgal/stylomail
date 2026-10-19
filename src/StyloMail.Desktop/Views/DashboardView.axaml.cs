using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The dashboard pane. It owns no transport and no policy: every value comes from
/// <see cref="DashboardViewModel"/>, which is filled by the window.
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private DashboardViewModel? Model => DataContext as DashboardViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        try
        {
            await model.LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // A refresh that was cancelled produced no answer and needs no sentence.
        }
        catch (Exception ex)
        {
            // The model turns expected failures into card states. Anything else is a programming
            // error, and taking the window down over it would be worse than logging it.
            Console.Error.WriteLine($"[Dashboard] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnWarningActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DashboardWarning warning } && Model is { } model)
        {
            model.ActivateWarning(warning);
        }
    }

    private void OnDismissWarningClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DashboardWarning warning } && Model is { } model)
        {
            model.Dismiss(warning);
        }
    }

    private void OnOpenPeopleClick(object? sender, RoutedEventArgs e) => Model?.RequestNavigation("Recipients");

    private void OnOpenConversationsClick(object? sender, RoutedEventArgs e) => Model?.RequestNavigation("Conversations");
}

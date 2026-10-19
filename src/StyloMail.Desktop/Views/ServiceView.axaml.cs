using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The service page. It owns no transport; the window supplies the model and the model owns the calls.
/// </summary>
public partial class ServiceView : UserControl
{
    public ServiceView() => InitializeComponent();

    private ServiceViewModel? Model => DataContext as ServiceViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.LoadAsync());
    }

    private void OnEnableAccountClick(object? sender, RoutedEventArgs e)
        => AskAccount(sender, static (model, row) => model.AskEnableAccount(row));

    private void OnDisableAccountClick(object? sender, RoutedEventArgs e)
        => AskAccount(sender, static (model, row) => model.AskPauseAccount(row));

    private void OnRevokeAccountClick(object? sender, RoutedEventArgs e)
        => AskAccount(sender, static (model, row) => model.AskRevokeAccount(row));

    private void OnEnableListenerClick(object? sender, RoutedEventArgs e)
        => AskListener(sender, static (model, row) => model.AskEnableListener(row));

    private void OnDrainListenerClick(object? sender, RoutedEventArgs e)
        => AskListener(sender, static (model, row) => model.AskDrainListener(row));

    private void AskAccount(object? sender, Action<ServiceViewModel, ServiceAccountRow> ask)
    {
        if (sender is Control { DataContext: ServiceAccountRow row } && Model is { } model)
        {
            ask(model, row);
        }
    }

    private void AskListener(object? sender, Action<ServiceViewModel, ServiceListenerRow> ask)
    {
        if (sender is Control { DataContext: ServiceListenerRow row } && Model is { } model)
        {
            ask(model, row);
        }
    }

    private async void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.ConfirmAsync());
    }

    private void OnCancelPendingClick(object? sender, RoutedEventArgs e) => Model?.CancelPending();

    private void OnDismissResultClick(object? sender, RoutedEventArgs e) => Model?.DismissResult();

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Service] {ex.GetType().Name}: {ex.Message}");
        }
    }
}

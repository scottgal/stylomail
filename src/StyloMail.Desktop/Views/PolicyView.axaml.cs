using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>The policy configuration and approval page.</summary>
public partial class PolicyView : UserControl
{
    public PolicyView() => InitializeComponent();

    private PolicyViewModel? Model => DataContext as PolicyViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.LoadAsync());
    }

    private async void OnNewDraftClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.OpenNewDraftAsync());
    }

    private async void OnEditDraftClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || PackFrom(sender) is not { } item) return;
        await RunAsync(() => model.EditDraftAsync(item));
    }

    private void OnApplyTemplateClick(object? sender, RoutedEventArgs e) => Model?.ApplyTemplate();

    private async void OnSaveDraftClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.SaveDraftAsync());
    }

    private void OnCancelDraftClick(object? sender, RoutedEventArgs e) => Model?.CancelDraft();

    private void OnApproveClick(object? sender, RoutedEventArgs e) => BeginPackAction(sender, "approve");

    private void OnActivateClick(object? sender, RoutedEventArgs e) => BeginPackAction(sender, "activate");

    private void OnWithdrawClick(object? sender, RoutedEventArgs e) => BeginPackAction(sender, "withdraw");

    private void OnDeactivateClick(object? sender, RoutedEventArgs e) => BeginPackAction(sender, "deactivate");

    private async void OnConfirmPackActionClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.ConfirmPackActionAsync());
    }

    private void OnCancelPackActionClick(object? sender, RoutedEventArgs e) => Model?.CancelPackAction();

    private void OnNewBindingClick(object? sender, RoutedEventArgs e) => Model?.OpenNewBinding();

    private async void OnSaveBindingClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.SaveBindingAsync());
    }

    private void OnCancelBindingClick(object? sender, RoutedEventArgs e) => Model?.CancelBindingEditor();

    private void OnRevokeBindingClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || BindingFrom(sender) is not { } item) return;
        model.BeginRevokeBinding(item);
    }

    private async void OnConfirmRevokeBindingClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.ConfirmRevokeBindingAsync());
    }

    private void OnCancelRevokeBindingClick(object? sender, RoutedEventArgs e) => Model?.CancelRevokeBinding();

    private void BeginPackAction(object? sender, string action)
    {
        if (Model is not { } model || PackFrom(sender) is not { } item) return;
        model.BeginPackAction(item, action);
    }

    private static PolicyPackListItem? PackFrom(object? sender) =>
        (sender as Button)?.Tag as PolicyPackListItem;

    private static PolicyBindingItem? BindingFrom(object? sender) =>
        (sender as Button)?.Tag as PolicyBindingItem;

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
            Console.Error.WriteLine($"[Policy] {ex.GetType().Name}: {ex.Message}");
        }
    }
}

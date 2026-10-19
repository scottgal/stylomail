using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The human review queue. It owns no transport and no policy; the model owns both.
/// </summary>
public partial class ReviewView : UserControl
{
    public ReviewView() => InitializeComponent();

    private ReviewViewModel? Model => DataContext as ReviewViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.LoadAsync());
    }

    private void OnReleaseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ReviewCardView card } && Model is { } model)
        {
            model.AskRelease(card);
        }
    }

    private void OnKeepStoppedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ReviewCardView card } && Model is { } model)
        {
            model.AskKeepStopped(card);
        }
    }

    private void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ReviewCardView card } && Model is { } model)
        {
            model.AskReject(card);
        }
    }

    private async void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.ConfirmAsync());
    }

    private void OnCancelPendingClick(object? sender, RoutedEventArgs e) => Model?.CancelPending();

    private void OnDismissResultClick(object? sender, RoutedEventArgs e) => Model?.DismissResult();

    private void OnOpenHeldClick(object? sender, RoutedEventArgs e)
        => Model?.RequestNavigation("Being re-checked before delivery");

    private void OnOpenStoppedClick(object? sender, RoutedEventArgs e)
        => Model?.RequestNavigation("Stopped before delivery");

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
            Console.Error.WriteLine($"[Review] {ex.GetType().Name}: {ex.Message}");
        }
    }
}

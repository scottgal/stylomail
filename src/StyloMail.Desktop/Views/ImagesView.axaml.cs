using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>The image observation page. It never renders image bytes.</summary>
public partial class ImagesView : UserControl
{
    public ImagesView() => InitializeComponent();

    private ImagesViewModel? Model => DataContext as ImagesViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        await RunAsync(() => model.LoadAsync());
    }

    private async void OnConversationSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Model is not { } model || model.SelectedConversation is not { } row) return;
        await RunAsync(() => model.SelectAsync(row));
    }

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
            Console.Error.WriteLine($"[Images] {ex.GetType().Name}: {ex.Message}");
        }
    }
}

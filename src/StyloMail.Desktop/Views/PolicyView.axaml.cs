using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>The read-only policy page.</summary>
public partial class PolicyView : UserControl
{
    public PolicyView() => InitializeComponent();

    private PolicyViewModel? Model => DataContext as PolicyViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        try
        {
            await model.LoadAsync();
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

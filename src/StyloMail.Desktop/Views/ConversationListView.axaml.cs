using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The conversation list pane. Embed this, set its <see cref="Control.DataContext"/> to a
/// <see cref="ProxyConversationListViewModel"/>, and the pane loads its first page when attached.
/// </summary>
public partial class ConversationListView : UserControl
{
    /// <summary>Required by the XAML compiler; the hosting page supplies the view model.</summary>
    public ConversationListView() => InitializeComponent();

    private ProxyConversationListViewModel? Model => DataContext as ProxyConversationListViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            await model.LoadAsync();
        }
    }

    private async void OnLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            await model.LoadAsync(append: true);
        }
    }

    private void OnOpenSignalsClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is Control { DataContext: ProxyConversationRowView row })
        {
            model.SelectedConversation = row;
        }
    }
}

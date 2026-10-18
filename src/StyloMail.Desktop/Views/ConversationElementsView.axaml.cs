using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

public partial class ConversationElementsView : UserControl
{
    public ConversationElementsView() => InitializeComponent();
    private ProxyConversationElementsViewModel? Model => DataContext as ProxyConversationElementsViewModel;
    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model) await model.LoadAsync(model.ThreadToken);
    }
}

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StyloMail.Desktop.Api;

namespace StyloMail.Desktop.Views;

/// <summary>Named shell for the conversation page, so automated evidence can address its window.</summary>
public partial class ConversationsDialog : Window
{
    public ConversationsDialog() => AvaloniaXamlLoader.Load(this);
    public ConversationsDialog(ProxyConversationClient? client) : this() => Content = new ConversationPage(client);
}

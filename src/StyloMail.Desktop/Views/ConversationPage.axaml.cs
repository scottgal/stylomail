using Avalonia;
using Avalonia.Controls;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The proxy conversation page: the list on the left and the selected thread's signals on the
/// right, wired together.
/// </summary>
/// <remarks>
/// This is the type the shell embeds. Constructing it with a <see cref="ProxyConversationClient"/>
/// is all the wiring the shell needs: selecting a row loads that thread's signals, and the first
/// page is read when the page attaches. The shell owns the navigation entry and nothing else here.
/// </remarks>
public partial class ConversationPage : UserControl
{
    private ProxyConversationListViewModel? _list;
    private ProxyConversationSignalsViewModel? _signals;
    private ProxyConversationElementsViewModel? _elements;

    /// <summary>Required by the XAML compiler.</summary>
    public ConversationPage() => InitializeComponent();

    /// <summary>Wires both panes to one client. A null client renders both reads as unavailable.</summary>
    public ConversationPage(ProxyConversationClient? client)
        : this()
        => Wire(new ProxyConversationListViewModel(client), new ProxyConversationSignalsViewModel(client), new ProxyConversationElementsViewModel(client));

    /// <summary>Wires both panes to view models the caller already holds.</summary>
    public ConversationPage(ProxyConversationListViewModel list, ProxyConversationSignalsViewModel signals)
        : this()
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(signals);
        Wire(list, signals, new ProxyConversationElementsViewModel());
    }

    /// <summary>Wires all three thread panes to caller-owned view models.</summary>
    public ConversationPage(
        ProxyConversationListViewModel list,
        ProxyConversationSignalsViewModel signals,
        ProxyConversationElementsViewModel elements)
        : this()
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(elements);
        Wire(list, signals, elements);
    }

    private void Wire(ProxyConversationListViewModel list, ProxyConversationSignalsViewModel signals, ProxyConversationElementsViewModel elements)
    {
        _list = list;
        _signals = signals;
        _elements = elements;
        ListView.DataContext = list;
        SignalsView.DataContext = signals;
        ElementsView.DataContext = elements;
        list.ConversationSelected += OnConversationSelected;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_list is { HasLoaded: false } list)
        {
            Start(list.LoadAsync());
        }
    }

    private async void OnConversationSelected(object? sender, ProxyConversationRowView? row)
    {
        if (row is null || _signals is null || _elements is null) return;

        try
        {
            await Task.WhenAll(_signals.LoadAsync(row.ThreadToken), _elements.LoadAsync(row.ThreadToken));
        }
        catch (OperationCanceledException)
        {
            // The operator moved on before the read answered; the view model was never asked to
            // report a cancellation as a failure.
        }
    }

    private static async void Start(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Attach-time load with no cancellation source; nothing to report.
        }
    }
}

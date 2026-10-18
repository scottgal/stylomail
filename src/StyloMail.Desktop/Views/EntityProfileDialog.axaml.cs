using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Views;
public partial class EntityProfileDialog : Window
{
    public EntityProfileDialog() { InitializeComponent(); }
    public EntityProfileDialog(EntityProfileView profile) : this() { DataContext = profile; }
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}

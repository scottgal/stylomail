using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Views;
public partial class ProxyAccountCreateDialog : Window
{
    private readonly StyloMailApiClient? _client;
    public ProxyAccountCreateDialog() { InitializeComponent(); }
    public ProxyAccountCreateDialog(StyloMailApiClient client) : this() => _client = client;
    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        var request = new ProxyAccountCreateRequest(LoginBox.Text ?? "", ClientPasswordBox.Text ?? "", BackendAccountIdBox.Text ?? "", ProviderBox.Text ?? "", DiscriminatorBox.Text ?? "", BackendUsernameBox.Text ?? "", ScopesBox.Text, BackendSecretBox.Text ?? "");
        try
        {
            if (_client is null) { ResultText.Text = "Account creation is unavailable until connected to a Host."; return; }
            var created = await _client.CreateProxyAccountAsync(request);
            ResultText.Text = $"Account created: {created.AccountId}. State: {created.EnrollmentState}.";
        }
        catch (StyloMailApiException error) when (error.Status == System.Net.HttpStatusCode.Conflict)
        { ResultText.Text = "That login is already enrolled. Choose a different login or manage the existing account."; }
        catch (StyloMailApiException error) when (error.Status == System.Net.HttpStatusCode.NotFound && error.Code == "credential_unavailable")
        { ResultText.Text = "The backend credential reference is unavailable; no account was created."; }
        catch (StyloMailApiException error) { ResultText.Text = $"Could not create account: {error.Detail ?? error.Code ?? error.Failure.ToString()}"; }
        finally { ClientPasswordBox.Text = string.Empty; BackendSecretBox.Text = string.Empty; }
    }
    private void OnCancelClick(object? sender, RoutedEventArgs e) { ClientPasswordBox.Text = string.Empty; BackendSecretBox.Text = string.Empty; Close(); }
}

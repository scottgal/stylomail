using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Views;

public partial class ProxyManagementDialog : Window
{
    private readonly StyloMailApiClient? _client;
    private IReadOnlyList<ProxyAccountRow> _accounts = [];
    /// <summary>Required by compiled AXAML; production opens the injected constructor below.</summary>
    public ProxyManagementDialog() { InitializeComponent(); }
    public ProxyManagementDialog(StyloMailApiClient client) : this() { _client = client; Opened += async (_, _) => await RefreshAsync(); }
    private async Task RefreshAsync()
    {
        StatusText.Text = "Reading tenant-scoped proxy management data…";
        try
        {
            if (_client is null) { StatusText.Text = "Proxy management is unavailable until connected to a Host."; return; }
            var accounts = await _client.GetProxyAccountsAsync();
            var sessions = await _client.GetLiveProxySessionsAsync();
            var listeners = await _client.GetProxyListenersAsync();
            var operations = await _client.GetProxyOperationsAsync("last_24_hours");
            _accounts = accounts.Accounts.Select(ProxyAccountRow.From).ToArray(); AccountsList.ItemsSource = _accounts;
            ListenersList.ItemsSource = listeners.Listeners.Select(ProxyListenerRow.From).ToArray();
            SessionsList.ItemsSource = sessions.Sessions.Select(ProxySessionRow.From).ToArray();
            SessionsText.Text = sessions.Availability == "not_produced" ? "Live-session telemetry was not produced for this window; an empty list is not a zero-session claim." : $"Live scope only. {sessions.Sessions.Count} session(s) in this page.";
            OperationsText.Text = DescribeOperations(operations);
            StatusText.Text = "Review data is pseudonymous and excludes raw logins, endpoints, credentials, and message content.";
        }
        catch (StyloMailApiException error) { StatusText.Text = $"Could not read proxy management data: {error.Detail ?? error.Code ?? error.Failure.ToString()}"; }
    }
    public static string DescribeOperations(ProxyOperationsResponse value)
    {
        if (value.Availability == "not_produced") return $"{value.Window} ({value.WindowStart:u}–{value.WindowEnd:u}): operations telemetry was not produced; counters are unknown, not zero.";
        var listeners = string.Join("; ", value.Listeners.Select(x => $"{x.Protocol}: {(x.Enabled ? x.State : "disabled")}, TLS {x.TlsPolicy}{(x.Draining ? "; draining: stops accepting new sessions and lets connected sessions finish" : string.Empty)}{(x.FaultReason is null ? string.Empty : ", fault " + x.FaultReason)}{(x.SafeDetail is null ? string.Empty : "; " + x.SafeDetail)}"));
        return $"{value.Window} ({value.WindowStart:u}–{value.WindowEnd:u}), source {value.Provenance}. Opened {Count(value.Counters.Opened)}, closed {Count(value.Counters.Closed)}, faults {Count(value.Counters.Faulted)}, live {Count(value.Counters.LiveSessions)}. Listeners: {listeners}";
    }
    private static string Count(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not produced";
    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private async void OnEnableClick(object? sender, RoutedEventArgs e) => await SetEnabled(sender, true);
    private async void OnDisableClick(object? sender, RoutedEventArgs e) => await SetEnabled(sender, false);
    private async Task SetEnabled(object? sender, bool enabled) { if (_client is null || (sender as Control)?.DataContext is not ProxyAccountRow row) return; await _client.SetProxyAccountEnabledAsync(row.AccountId, enabled); await RefreshAsync(); }
    private async void OnRevokeClick(object? sender, RoutedEventArgs e) { if (_client is null || (sender as Control)?.DataContext is not ProxyAccountRow row) return; await _client.RevokeProxyCredentialAsync(row.AccountId); await RefreshAsync(); }
    private async void OnCreateAccountClick(object? sender, RoutedEventArgs e) { if (_client is null) return; await new ProxyAccountCreateDialog(_client).ShowDialog(this); await RefreshAsync(); }
    private async void OnEnableListenerClick(object? sender, RoutedEventArgs e) => await SetListenerEnabled(sender, true);
    private async void OnDisableListenerClick(object? sender, RoutedEventArgs e) => await SetListenerEnabled(sender, false);
    private async Task SetListenerEnabled(object? sender, bool enabled)
    {
        if (_client is null || (sender as Control)?.DataContext is not ProxyListenerRow row) return;
        try
        {
            await _client.SetProxyListenerEnabledAsync(row.ListenerId, enabled);
            StatusText.Text = enabled
                ? "Listener enablement was requested; refreshing its observed state."
                : "Listener drain was requested: it stops accepting new sessions while connected sessions finish.";
            await RefreshAsync();
        }
        catch (StyloMailApiException error) when (error.Code == "listener_runtime_unavailable")
        {
            StatusText.Text = "Listener runtime is unavailable, so this control cannot be applied. Deployment configuration remains read-only here.";
        }
        catch (StyloMailApiException error)
        {
            StatusText.Text = $"Could not change listener state: {error.Detail ?? error.Code ?? error.Failure.ToString()}";
        }
    }
}
public sealed record ProxyAccountRow(string AccountId, string Summary, string Detail, bool CanEnable, bool CanDisable)
{ public static ProxyAccountRow From(ProxyAccountResponse x) => new(x.AccountId, $"{x.AccountId} · {x.ProviderId}", $"Pseudonymous login key: {x.LoginKey}. Operator state: {(x.Enabled ? "enabled" : "disabled")}; enrolment: {x.EnrollmentState}; credential class: {x.CredentialDiscriminator}.", !x.Enabled, x.Enabled); }
public sealed record ProxySessionRow(string Summary, string Detail)
{ public static ProxySessionRow From(ProxySessionResponse x) => new($"{x.Protocol} {x.Direction} · {x.Outcome ?? "active"}", $"Account: {x.AccountId ?? "not produced"}; started {x.StartedAt:u}; authenticated {(x.AuthenticatedAt is null ? "not produced" : x.AuthenticatedAt.Value.ToString("u"))}; client bytes {Count(x.ClientBytes)}; backend bytes {Count(x.BackendBytes)}.{(x.BoundReason is null ? string.Empty : " Safe reason: " + x.BoundReason)}"); private static string Count(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not measured"; }
public sealed record ProxyListenerRow(string ListenerId, string Summary, string Detail, bool CanEnable, bool CanDisable)
{
    public static ProxyListenerRow From(ProxyListenerResponse x) => new(
        x.ListenerId,
        $"{x.Protocol} · port {x.BoundPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not produced"} · TLS {x.TlsPolicy}",
        $"State: {(x.Enabled ? x.State : "disabled")}.{(x.Draining ? " Draining: stops accepting new sessions while connected sessions finish." : string.Empty)}{(x.FaultReason is null ? string.Empty : " Safe fault: " + x.FaultReason)}{(x.SafeDetail is null ? string.Empty : " " + x.SafeDetail)}",
        !x.Enabled,
        x.Enabled && !x.Draining);
}

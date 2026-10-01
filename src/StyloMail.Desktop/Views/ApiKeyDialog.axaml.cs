using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Interactivity;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.Services;

namespace StyloMail.Desktop.Views;

/// <summary>
/// Enters, replaces and clears the console's API key.
/// </summary>
/// <remarks>
/// <b>This is the screen that makes a first run possible.</b> The keychain seam
/// was built and tested before this existed, and there was no way for an
/// operator to put a key into it: a first run could reach "No API key set" and
/// stop there, with the only remedy being an environment variable that spec
/// 10.3 rules out.
///
/// <para>
/// <b>Nothing here can show a key.</b> The field is masked, there is no reveal
/// control, and after this dialog closes the value exists only in the keychain.
/// That is not for looks: it is what makes the screen useless to anyone reading
/// a screenshot, a screen recording or a shared window, which is the threat
/// spec 10.3 is about.
/// </para>
/// </remarks>
public partial class ApiKeyDialog : Window
{
    private readonly IApiKeyProvider _keys;
    private readonly IConsoleSettings _settings;

    /// <summary>
    /// The address the console was pointing at when this dialog opened.
    /// </summary>
    /// <remarks>
    /// Captured here rather than read from the settings on save, because
    /// <c>TryAcceptAddress</c> has already written the new one by the time the
    /// caller of that method decides anything: comparing against the settings
    /// at that point would compare the address with itself and report that
    /// nothing moved, every time.
    /// </remarks>
    private readonly string? _originalAddress;

    /// <summary>
    /// What this dialog changed, and therefore what the caller owes.
    /// </summary>
    /// <remarks>
    /// One value rather than separate flags, so that "the caller should
    /// reconnect" is answered in one place. See <see cref="ConnectionEdit"/>
    /// for why the address half of it exists at all.
    /// </remarks>
    public ConnectionEdit Edit { get; private set; } = ConnectionEdit.None;

    public ApiKeyDialog()
        : this(new InMemoryConsoleSettings(), new InMemoryKeychainProvider())
    {
    }

    public ApiKeyDialog(IConsoleSettings settings, IApiKeyProvider keys)
    {
        _settings = settings;
        _keys = keys;
        _originalAddress = settings.HostAddress;

        // The generated InitializeComponent, not AvaloniaXamlLoader.Load.
        // Both load the XAML; only the generated one also assigns the fields
        // the name generator declares for each x:Name, and reaching a control
        // through a field it never populated is a NullReferenceException.
        InitializeComponent();

        HostAddressBox.Text = settings.HostAddress ?? string.Empty;

        // Says whether a key is held and never what it is. The UI needs that to
        // choose its wording; it does not need the value, and a code path that
        // held it would be one more place it could reach a screen.
        UpdateKeyState();
    }

    // HostAddressBox, ApiKeyBox, HostRefusalText, KeyStateText and ResultText are
    // generated as fields from the x:Name attributes by Avalonia's name
    // generator. Declaring them here as well is a duplicate-definition error,
    // which is a pleasant surprise rather than an annoyance: it means the
    // compiler is checking that this code and the XAML agree.

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var entered = ApiKeyBox.Text;

        // The address is checked before the key is stored. Saving a key against
        // an address the console will not use would leave the operator holding
        // a credential they cannot tell is unused.
        if (!TryAcceptAddress(out var address))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entered))
        {
            // Saving with an empty field means "keep the stored one", which is
            // the ordinary case of changing only the address. It is still a
            // connection change when the address moved, which the caller reads
            // off Edit rather than off the wording below: the wording used to
            // say "Address saved." for a save that reconnected nothing.
            Edit = ConnectionEdit.KeptKey(_originalAddress, address);

            ResultText.Text = _keys is KeychainApiKeyProvider { } keychain && keychain.HasKey()
                ? Edit.AddressChanged
                    ? "Address saved. The stored key was kept."
                    : "Nothing changed. The stored key was kept."
                : "Enter the key the Host issued.";
            return;
        }

        StoreKey(entered);

        // Cleared from the field as soon as it is stored, so it is not sitting
        // in a control when the dialog is photographed or screen-shared.
        ApiKeyBox.Text = string.Empty;

        Edit = ConnectionEdit.SavedKey(_originalAddress, address);

        UpdateKeyState();
        ResultText.Text = "Saved to your keychain.";
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (_keys is not KeychainApiKeyProvider keychain)
        {
            ResultText.Text = "No stored key to remove on this build.";
            return;
        }

        keychain.Clear();
        Edit = ConnectionEdit.RemovedKey();

        UpdateKeyState();
        ResultText.Text = "Stored key removed.";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Validates and records the address, or explains why not.
    /// </summary>
    /// <remarks>
    /// The non-null attribution is what lets the save path hand this straight to
    /// <see cref="ConnectionEdit"/>: returning true is exactly the case where an
    /// address was accepted, so an address is exactly what the caller has.
    /// </remarks>
    private bool TryAcceptAddress([NotNullWhen(true)] out string? address)
    {
        address = null;

        var candidate = HostAddressBox.Text?.Trim();

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
        {
            ShowRefusal("That is not a complete address. It needs a scheme and a host.");
            return false;
        }

        if (!HostAddressPolicy.IsAcceptable(parsed, out var refusal))
        {
            ShowRefusal(refusal!);
            return false;
        }

        HostRefusalText.IsVisible = false;
        _settings.SetHostAddress(parsed.ToString());
        address = parsed.ToString();

        return true;
    }

    private void ShowRefusal(string refusal)
    {
        HostRefusalText.Text = refusal;
        HostRefusalText.IsVisible = true;
    }

    private void StoreKey(string key)
    {
        if (_keys is KeychainApiKeyProvider keychain)
        {
            keychain.Store(key);
            return;
        }

        ResultText.Text = "This build has no writable keychain.";
    }

    private void UpdateKeyState()
    {
        KeyStateText.Text = _keys is KeychainApiKeyProvider keychain && keychain.HasKey()
            ? "A key is stored. Its value cannot be shown, here or anywhere else."
            : "No key is stored, so the console can reach only the Host's unauthenticated health routes.";
    }
}

/// <summary>
/// A key provider that holds nothing, for the XAML loader and a design-time
/// preview. Never selected by the application.
/// </summary>
internal sealed class InMemoryKeychainProvider : IApiKeyProvider
{
    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);
}

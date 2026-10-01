namespace StyloMail.Desktop.Models;

/// <summary>
/// What saving on the connection screen changed, and therefore what the caller owes.
/// </summary>
/// <remarks>
/// <para>
/// A plain value rather than logic inside the dialog's click handler, on the
/// same reasoning as <see cref="HostStatus"/> and <see cref="LiveFeedStatus"/>:
/// the rule is worth more than the pixels it is enforced with, and this can be
/// tested exhaustively with no display and no dispatcher.
/// </para>
/// <para>
/// <b>It exists because the dialog announced a change the caller never acted
/// on.</b> The empty-key branch of save writes the address and reports "Address
/// saved. The stored key was kept.", while the only caller of
/// <c>MainWindow.ReconnectAsync</c> returned early unless the *key* had changed
/// - so an operator who changed only the address was told the address was saved
/// and kept talking to the old Host. That is decision 25's shape one level
/// down: state announced that was not computed. The dialog's own primary button
/// reads "Save and connect", which is the promise the branch was breaking.
/// </para>
/// <para>
/// So a save reports both halves - did the key change, did the address change -
/// and <see cref="RequiresReconnect"/> is the answer to the only question the
/// caller has. The two are reported separately rather than collapsed because
/// the dialog's wording depends on which one moved: "Address saved." over an
/// unchanged address is the same defect in miniature.
/// </para>
/// </remarks>
public sealed record ConnectionEdit
{
    private ConnectionEdit(bool keyChanged, bool addressChanged, string? address)
    {
        KeyChanged = keyChanged;
        AddressChanged = addressChanged;
        Address = address;
    }

    /// <summary>Nothing was saved, so nothing is owed.</summary>
    public static ConnectionEdit None { get; } = new(false, false, null);

    /// <summary>A new key was stored.</summary>
    public bool KeyChanged { get; }

    /// <summary>The address is not the one this console was pointing at.</summary>
    public bool AddressChanged { get; }

    /// <summary>The address that was validated, whether or not it moved.</summary>
    public string? Address { get; }

    /// <summary>
    /// Whether the caller has to rebuild its client to honour this save.
    /// </summary>
    /// <remarks>
    /// Either half is enough. A key change is read through the provider the old
    /// client holds and an address change is the old client's base address, so
    /// neither can be patched into a live connection.
    /// </remarks>
    public bool RequiresReconnect => KeyChanged || AddressChanged;

    /// <summary>A save that stored a key the operator typed.</summary>
    public static ConnectionEdit SavedKey(string? originalAddress, string address)
        => new(true, Moved(originalAddress, address), address);

    /// <summary>A save that kept the stored key, which is the address-only case.</summary>
    public static ConnectionEdit KeptKey(string? originalAddress, string address)
        => new(false, Moved(originalAddress, address), address);

    /// <summary>The stored key was removed, which is a connection change on its own.</summary>
    public static ConnectionEdit RemovedKey() => new(true, false, null);

    private static bool Moved(string? originalAddress, string address)
        => !string.Equals(originalAddress, address, StringComparison.Ordinal);
}

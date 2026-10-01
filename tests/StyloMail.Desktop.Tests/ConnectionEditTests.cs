using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// What a save on the connection screen owes the console.
/// </summary>
/// <remarks>
/// The defect this type was written for is narrow and worth stating as a test
/// rather than a comment: the dialog wrote the address, said "Address saved.",
/// and the caller reconnected nothing, because it was gated on the key having
/// changed. An operator who moved the console to a different Host kept talking
/// to the old one and was told the move had happened. So the case that matters
/// most here is the one where the key did not change and the address did.
/// </remarks>
public sealed class ConnectionEditTests
{
    private const string Old = "http://127.0.0.1:5271";
    private const string New = "http://127.0.0.1:5391";

    [Fact]
    public void Nothing_was_saved_so_nothing_is_owed()
    {
        var edit = ConnectionEdit.None;

        Assert.False(edit.KeyChanged);
        Assert.False(edit.AddressChanged);
        Assert.False(edit.RequiresReconnect);
        Assert.Null(edit.Address);
    }

    /// <summary>
    /// The case the dialog used to get wrong, and the reason the type exists.
    /// </summary>
    [Fact]
    public void An_address_that_moved_with_the_kept_key_still_owes_a_reconnect()
    {
        var edit = ConnectionEdit.KeptKey(Old, New);

        Assert.False(edit.KeyChanged);
        Assert.True(edit.AddressChanged);
        Assert.True(edit.RequiresReconnect);
        Assert.Equal(New, edit.Address);
    }

    /// <summary>
    /// Saving without changing anything is not a connection change, and the
    /// caller must not rebuild a working client for it. This is the other half
    /// of the same rule: the dialog should not announce what it did not do, and
    /// it should not act as though it did.
    /// </summary>
    [Fact]
    public void An_unchanged_address_with_the_kept_key_owes_nothing()
    {
        var edit = ConnectionEdit.KeptKey(Old, Old);

        Assert.False(edit.KeyChanged);
        Assert.False(edit.AddressChanged);
        Assert.False(edit.RequiresReconnect);

        // The validated address is still recorded, because the caller may want
        // to show it. Recording it is not the same as owing a reconnect.
        Assert.Equal(Old, edit.Address);
    }

    [Fact]
    public void A_stored_key_owes_a_reconnect_even_when_the_address_did_not_move()
    {
        var edit = ConnectionEdit.SavedKey(Old, Old);

        Assert.True(edit.KeyChanged);
        Assert.False(edit.AddressChanged);
        Assert.True(edit.RequiresReconnect);
    }

    [Fact]
    public void A_stored_key_and_a_moved_address_report_both()
    {
        var edit = ConnectionEdit.SavedKey(Old, New);

        Assert.True(edit.KeyChanged);
        Assert.True(edit.AddressChanged);
        Assert.True(edit.RequiresReconnect);
    }

    /// <summary>
    /// Clearing the key is a connection change on its own: the client holds the
    /// provider the key was read through, so it has to be rebuilt whether or not
    /// the address moved.
    /// </summary>
    [Fact]
    public void Removing_the_stored_key_owes_a_reconnect()
    {
        var edit = ConnectionEdit.RemovedKey();

        Assert.True(edit.KeyChanged);
        Assert.False(edit.AddressChanged);
        Assert.True(edit.RequiresReconnect);
    }

    /// <summary>
    /// A first run has no stored address, so anything the operator enters is a
    /// move. Treating null as "equal to whatever was typed" would leave the
    /// first save of an address reconnecting nothing.
    /// </summary>
    [Fact]
    public void A_first_address_after_no_stored_one_is_a_move()
    {
        Assert.True(ConnectionEdit.KeptKey(null, New).AddressChanged);
        Assert.True(ConnectionEdit.KeptKey(null, New).RequiresReconnect);
    }

    /// <summary>
    /// Case and trailing differences are moves: the settings were written with
    /// the string the operator gave, and comparing loosely could report "no
    /// change" for an address the console is now stored to point at.
    /// </summary>
    [Fact]
    public void The_comparison_is_ordinal()
    {
        Assert.True(ConnectionEdit.KeptKey(Old, Old.ToUpperInvariant()).AddressChanged);
    }
}

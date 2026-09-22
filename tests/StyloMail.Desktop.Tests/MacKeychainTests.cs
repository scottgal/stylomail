using StyloMail.Desktop.Services;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// A fact that touches the real keychain, and only when asked to.
/// </summary>
/// <remarks>
/// Opt-in, and the reason is not tidiness. This writes into the login keychain,
/// so running it unasked would put an item in a developer's keychain on every
/// test run, and macOS may raise a keychain-access prompt that a test runner
/// cannot answer. Neither belongs in a suite that is supposed to run anywhere.
/// </remarks>
public sealed class KeychainFactAttribute : FactAttribute
{
    public const string Variable = "STYLOMAIL_KEYCHAIN_SMOKE";

    public KeychainFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Set {Variable}=1 to run this against the real keychain.";
        }
    }
}

/// <summary>
/// The macOS keychain, against the actual keychain.
/// </summary>
/// <remarks>
/// <b>Why this exists at all.</b> The interop here is a pile of P/Invokes into
/// two system frameworks, and every failure path in it returns null quietly
/// rather than throwing, which is right for a keychain read and means a broken
/// implementation is indistinguishable from an empty one. Without this test the
/// console would report "no API key set" forever and there would be nothing
/// anywhere to say whether the store worked.
///
/// <para>
/// It uses a service name of its own and deletes what it writes, so it cannot
/// collide with, read, or overwrite a real operator key.
/// </para>
/// </remarks>
public sealed class MacKeychainTests
{
    private const string Service = "StyloMail Console Keychain Self Test";
    private const string Account = "round-trip";
    private const string Value = "value-to-round-trip-0123456789";

    [KeychainFact]
    public void A_key_round_trips_and_is_removed_again()
    {
        var keychain = new MacKeychain();

        Assert.True(keychain.IsAvailable, "The keychain reported itself unavailable on macOS.");

        // Start from nothing, so a leftover from an earlier aborted run cannot
        // make the first read pass for the wrong reason.
        keychain.Delete(Service, Account);
        Assert.Null(keychain.Read(Service, Account));

        keychain.Write(Service, Account, Value);
        Assert.Equal(Value, keychain.Read(Service, Account));

        keychain.Delete(Service, Account);
        Assert.Null(keychain.Read(Service, Account));
    }

    /// <summary>
    /// Writing twice replaces rather than failing, because rotating a key is
    /// the ordinary reason to call it a second time.
    /// </summary>
    [KeychainFact]
    public void Writing_twice_replaces_the_stored_key()
    {
        var keychain = new MacKeychain();

        try
        {
            keychain.Write(Service, Account, "first-value");
            keychain.Write(Service, Account, "second-value");

            Assert.Equal("second-value", keychain.Read(Service, Account));
        }
        finally
        {
            keychain.Delete(Service, Account);
        }
    }

    /// <summary>Removing something that is not there is not an error.</summary>
    [KeychainFact]
    public void Deleting_a_missing_key_is_harmless()
    {
        var keychain = new MacKeychain();

        keychain.Delete(Service, "never-written");
        keychain.Delete(Service, "never-written");

        Assert.Null(keychain.Read(Service, "never-written"));
    }

    /// <summary>
    /// On a platform that is not macOS the store answers nothing at all rather
    /// than pretending, which the caller reads as "no key configured".
    /// </summary>
    [Fact]
    public void An_unavailable_keychain_reads_nothing_and_stores_nothing()
    {
        var keychain = new MacKeychain();

        if (keychain.IsAvailable)
        {
            // On macOS the behaviour under test is the other test's.
            return;
        }

        keychain.Write(Service, Account, Value);
        Assert.Null(keychain.Read(Service, Account));
    }
}

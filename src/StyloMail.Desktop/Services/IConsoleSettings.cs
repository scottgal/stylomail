namespace StyloMail.Desktop.Services;

/// <summary>
/// The console's own settings, which are not secrets.
/// </summary>
/// <remarks>
/// <b>The API key is deliberately not here.</b> It lives in the platform
/// keychain and nothing else; this interface exists for the values that are
/// ordinary configuration, and the split is enforced by this type having no
/// member that could hold a credential.
///
/// <para>
/// A file is a reasonable home for a host address and was the wrong home for a
/// key, which is the distinction spec 10.3 draws. The test for this asserts
/// that the serialised form never contains the key, so the two cannot drift
/// into one store by a later convenience.
/// </para>
/// </remarks>
public interface IConsoleSettings
{
    /// <summary>Where this console points. Null means "nothing chosen yet".</summary>
    string? HostAddress { get; }

    /// <summary>Records where to point. Does not validate: that is the caller's job.</summary>
    void SetHostAddress(string hostAddress);
}

/// <summary>
/// Settings that are not written down.
/// </summary>
/// <remarks>
/// What the console uses before anyone has configured anything, and what the
/// UI harness uses so a run cannot inherit a developer's chosen Host.
/// </remarks>
public sealed class InMemoryConsoleSettings : IConsoleSettings
{
    public string? HostAddress { get; private set; }

    public void SetHostAddress(string hostAddress) => HostAddress = hostAddress;
}

/// <summary>
/// Settings in a small JSON file under the platform's application data.
/// </summary>
/// <remarks>
/// Deliberately a single field rather than a settings bag. Every value that has
/// wanted to live here since has been a credential, and a bag invites the next
/// one in. Adding a field should require deciding, in this file, that it is not
/// a secret.
/// </remarks>
public sealed class JsonConsoleSettings : IConsoleSettings
{
    /// <summary>The one value this file holds.</summary>
    private sealed record Stored(string? HostAddress);

    private readonly string _path;

    public JsonConsoleSettings(string path) => _path = path;

    public string? HostAddress { get; private set; }

    public static JsonConsoleSettings Load(string path)
    {
        var settings = new JsonConsoleSettings(path);

        try
        {
            if (File.Exists(path))
            {
                settings.HostAddress = System.Text.Json.JsonSerializer
                    .Deserialize<Stored>(File.ReadAllText(path))?.HostAddress;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            // A settings file that cannot be read is not a reason the console
            // cannot open. It falls back to "nothing chosen", which the
            // connection screen already handles.
            Console.Error.WriteLine($"[Settings] {ex.GetType().Name}: {ex.Message}");
        }

        return settings;
    }

    public void SetHostAddress(string hostAddress)
    {
        HostAddress = hostAddress;

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(
                _path,
                System.Text.Json.JsonSerializer.Serialize(new Stored(hostAddress)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recorded in memory and reported, because a console that silently
            // forgot the address would look like a console that had not been
            // asked.
            Console.Error.WriteLine($"[Settings] Could not save: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

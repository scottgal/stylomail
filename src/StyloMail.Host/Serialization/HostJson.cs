using System.Text.Json;
using System.Text.Json.Serialization;

namespace StyloMail.Host.Serialization;

/// <summary>
/// The serializer used for anything the host persists or serves.
/// </summary>
/// <remarks>
/// Enums are written as names, never as ordinals. A ledger entry reading <c>"Quarantine"</c> stays
/// readable years later and survives someone reordering the enum, whereas a stored <c>2</c> would
/// silently change meaning the day a member is inserted above it.
/// </remarks>
public static class HostJson
{
    /// <summary>Used to write anything the host persists, and to read anything it was handed.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>
    /// Used to read an assessment back out of storage, and for nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="Options"/> so that tolerating a member an older build did not write
    /// is confined to the persistence boundary. Putting it in <see cref="Options"/> would make every
    /// assessment everywhere tolerant of missing required members, which is the guarantee
    /// <c>required</c> was chosen for.
    /// </para>
    /// <para>
    /// Built as <see cref="Options"/> plus the converter, which is also what keeps the converter's
    /// own read from recursing into itself.
    /// </para>
    /// </remarks>
    public static readonly JsonSerializerOptions PersistedRead = CreatePersistedRead();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreatePersistedRead()
    {
        var options = Create();
        options.Converters.Add(new PersistedAssessmentConverter());
        return options;
    }
}

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
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

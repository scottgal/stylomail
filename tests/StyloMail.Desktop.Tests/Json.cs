using System.Text.Json;
using System.Text.Json.Serialization;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// Reads a <see cref="Wire"/> fixture into a contract type.
/// </summary>
/// <remarks>
/// Mirrors the client's own serializer settings, which is what makes these
/// fixtures readable through the same lens the client reads the wire with: web
/// defaults for camelCase, and enums as names.
///
/// <para>
/// It is deliberately not the client's options object. Reaching into that would
/// make every model test a test of the client's serializer, and the binding
/// itself is already covered by <c>ListingTests</c> and
/// <c>DecisionContractTests</c> against raw JSON. Here the fixture is only a
/// convenient way to get a populated contract object.
/// </para>
/// </remarks>
internal static class Json
{
    private static readonly JsonSerializerOptions Options = Create();

    public static T Read<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException($"Fixture did not bind as {typeof(T).Name}.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StyloMail.Core;

namespace StyloMail.Host.Serialization;

/// <summary>
/// Reads an assessment that an earlier build persisted, supplying members that did not exist then.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is needed at all.</b> The ledger stores the assessment as a document so that adding a
/// field to the contract is not a migration. Members declared <c>required</c> break that promise from
/// the other side: System.Text.Json enforces them on deserialisation, so a row written before the
/// member existed cannot be read back at all. The failure then belongs to the build that added the
/// member and to every build after it, against data nobody touched, and no test catches it because
/// every test writes and reads inside one build where the member is always present. Confining the
/// tolerance to this converter keeps <c>required</c> honest at every construction site while still
/// letting history be read.
/// </para>
/// <para>
/// <b>These are back-fills, not defaults.</b> Each entry states what the rows lacking that member
/// actually were. <c>MailAssessor</c> is the only production construction site and it is the email
/// path, so every assessment already on disk was an email assessment made while still in the
/// delivery path, which is <see cref="DeliveryTiming.PreAcceptance"/>. That derivation is the entire
/// justification. The same code written for a member whose legacy value could not be derived would
/// be inventing history rather than restoring it, and should not be added here.
/// </para>
/// <para>
/// <b>Read at the persistence boundary only.</b> This is deliberately not part of
/// <see cref="HostJson.Options"/>, which would make every assessment everywhere tolerant of missing
/// required members and quietly undo the guarantee <c>required</c> exists to provide.
/// </para>
/// </remarks>
public sealed class PersistedAssessmentConverter : JsonConverter<MailAssessment>
{
    public override MailAssessment? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // Held for the whole method because the nodes below borrow from it, and it must outlive the
        // deserialise that consumes them.
        using var document = JsonDocument.ParseValue(ref reader);

        var assessment = AsJsonObject(document.RootElement);

        if (assessment is null)
        {
            // Not an object, so not an assessment in any shape. Handed to the ordinary reader so it
            // fails the way it always would rather than being reported as a legacy document.
            return document.RootElement.Deserialize<MailAssessment>(HostJson.Options);
        }

        // Every entry here says what the rows lacking that member actually were, which is what makes
        // this a back-fill rather than a default. MailAssessor is the only production construction
        // site and it is the email path, so a row written before chat existed was an email
        // assessment made while still in the delivery path: Email and PreAcceptance are what those
        // rows were, not a guess about them.
        Backfill(assessment, nameof(MailAssessment.DeliveryTiming), DeliveryTiming.PreAcceptance, options);
        Backfill(assessment, nameof(MailAssessment.Channel), ChannelContext.Email, options);

        // HostJson.Options does NOT contain this converter, which is what stops this call recursing
        // into the method it is called from. Adding this converter to Options would be an infinite
        // loop, and PersistedRead is built as Options plus this converter for exactly that reason.
        return assessment.Deserialize<MailAssessment>(HostJson.Options);
    }

    public override void Write(
        Utf8JsonWriter writer,
        MailAssessment value,
        JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, HostJson.Options);

    private static JsonObject? AsJsonObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? JsonObject.Create(element) : null;

    /// <summary>
    /// Adds the member when the stored document predates it, leaving documents that already carry it
    /// untouched.
    /// </summary>
    private static void Backfill<T>(
        JsonObject assessment,
        string member,
        T legacyValue,
        JsonSerializerOptions options)
    {
        // Named by the serializer's own policy rather than a literal, so a change to the naming
        // policy cannot silently turn this into a no-op that adds a property nothing reads.
        var name = options.PropertyNamingPolicy?.ConvertName(member) ?? member;

        if (!assessment.ContainsKey(name))
        {
            assessment[name] = JsonSerializer.SerializeToNode(legacyValue, options);
        }
    }
}

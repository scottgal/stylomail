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
/// <b>Members added after rows already existed may carry null by design.</b> The index arithmetic
/// (decision 37) is the case: a decision stored before it existed serves <c>null</c> for its weight,
/// counted flag and denominator. That is the row saying its build did not record them, not an omission
/// this converter failed to fill, and it must stay distinct from the two back-fills above, which
/// restore what a row actually was.
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

        // The index arithmetic (decision 37) is a different kind of member from the two above, and it
        // is handled differently on purpose. A back-fill restores a value that the row's own contents
        // determine; these three cannot be recovered at all, because the weights are policy
        // configuration that was never persisted alongside the decision. So the row is not given a
        // value, it is given the answer "the build that made this decision did not record it": null.
        //
        // Stated rather than left absent because `required` is enforced on *presence*, so a merely
        // missing member still fails the read, and a null member and an absent one are different
        // things to System.Text.Json. This is why the members are nullable: the alternative, a
        // zero, would serve a plausible number on a row that actually carried weight.
        StateUnrecorded(assessment, nameof(MailAssessment.RiskIndexDenominator), options);
        StateUnrecordedInDimensions(
            assessment,
            [nameof(RiskDimension.Weight), nameof(RiskDimension.Counted)],
            options);

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

    /// <summary>
    /// States null for a member whose value an earlier build did not record.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <see cref="Backfill{T}"/>: that method restores what a row was, and this one
    /// records that the row does not say. Written as its own method so the two are not read as the same
    /// operation by the next person, since conflating them is how a null would become a zero.
    /// </remarks>
    private static void StateUnrecorded(
        JsonObject assessment,
        string member,
        JsonSerializerOptions options)
    {
        var name = options.PropertyNamingPolicy?.ConvertName(member) ?? member;

        if (!assessment.ContainsKey(name))
        {
            // The JSON null literal rather than a null reference, so the property is present and null
            // rather than dropped, which is the distinction `required` is enforced on.
            assessment[name] = JsonNode.Parse("null");
        }
    }

    /// <summary>
    /// States null for each member on every entry of a collection of objects, for the members that
    /// travel inside a row rather than beside it.
    /// </summary>
    private static void StateUnrecordedInDimensions(
        JsonObject assessment,
        IReadOnlyList<string> members,
        JsonSerializerOptions options)
    {
        var collection = options.PropertyNamingPolicy?.ConvertName(nameof(MailAssessment.RiskDimensions))
            ?? nameof(MailAssessment.RiskDimensions);

        if (assessment[collection] is not JsonArray dimensions)
        {
            // No dimensions at all is a shape this converter has nothing to add to: the collection
            // member is itself required, so a document lacking it fails the read for a reason that is
            // not this decision's, and inventing an empty list here would be a default rather than a
            // restoration.
            return;
        }

        foreach (var dimension in dimensions)
        {
            if (dimension is not JsonObject row)
            {
                continue;
            }

            foreach (var member in members)
            {
                StateUnrecorded(row, member, options);
            }
        }
    }
}

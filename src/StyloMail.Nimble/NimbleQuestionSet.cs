using StyloMail.Core;

namespace StyloMail.Nimble;

/// <summary>
/// Turns StyloMail's semantic dimensions into the questions this provider is asked with, and turns an
/// answer back into an evidence value.
/// </summary>
/// <remarks>
/// <para>
/// <b>The request shape is part of the semantics and is therefore fixed here, versioned, and included
/// in the cache key.</b> That is not caution, it is a measurement. Asked alone on the corpus's
/// credential-request sample, <c>semantic.credential_request</c> answered B on three of three runs;
/// asked alongside the other eleven dimensions, it answered A on three of three. Both shapes are
/// stable at temperature zero and they disagree with each other. Across the twelve dimensions the two
/// shapes disagreed on three. A caller free to choose the shape would be free to choose the answer.
/// </para>
/// <para>
/// <b>That measurement was taken under <c>nimble-request-shape/1</c> and has NOT been re-taken under
/// the current shape.</b> It is kept rather than deleted because it is the reason the shape is
/// versioned at all, and because deleting a measurement leaves a reader unable to tell a claim that
/// was retired from one that was never made. What it does not do is describe the current request: the
/// three-of-twelve figure is a fact about the A/B letter rendering, and the shape below is not that
/// rendering. The shape that ships now is declared per question, as data, and answered with a graded
/// probability. Re-measuring the batching effect under this shape is open work, and the sentence stays
/// standing as a carry-over until a run moves it.
/// </para>
/// <para>
/// The shape that ships is the one that was measured: every askable dimension in <b>one</b> request,
/// each declared with its type, its instruction and both of its criteria, with the message in the
/// <c>state</c> member. There is no fan-out option, because the survey showed fan-out is not required
/// (twelve of twelve answered in one call) and because offering both would offer two different answers.
/// </para>
/// </remarks>
internal static class NimbleQuestionSet
{
    /// <summary>
    /// Version of the request shape. Bump it when the rendering, the batching or the answer alphabet
    /// changes, because any of those can change an answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="SemanticDimensions.QuestionSchemaVersion"/>, which versions the
    /// questions' text. This versions how they are put to <em>this</em> provider. Both belong in the
    /// cache key: editing a criterion changes an answer, and so does changing the shape it is asked in.
    /// </para>
    /// <para>
    /// <b><c>/2</c> is the migration from <c>/api/generate</c> to <c>POST /v1/systemone</c> on
    /// 1 Oct 2026, and it is a real shape change rather than a bookkeeping bump.</b> Under <c>/1</c>
    /// the questions were prose in a <c>system</c> message and the answer was constrained by a JSON
    /// schema to one of two letters. Under <c>/2</c> each question is data with its own type and
    /// criteria, the answer is a probability, and there is no schema and no system message. The
    /// alphabet changed, the rendering changed and the batching story is unmeasured, which is three of
    /// the three things this constant exists to cover.
    /// </para>
    /// <para>
    /// Because the classifier's cache key digests this value, the bump invalidates every memoised
    /// semantic assessment by construction. That is the intended effect and not a side effect: a
    /// cached answer produced by the letter shape must not be served to a caller asking the probability
    /// shape, and no separate invalidation step is needed or wanted.
    /// </para>
    /// </remarks>
    internal const string Version = "nimble-request-shape/2";

    /// <summary>
    /// The value that goes on every row this provider answers, naming both the model and the request
    /// shape that produced the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A row has to say what kind of answer it is, and this is where it says so.</b> A hosted row and
    /// a local row for the same dimension and the same message are not the same object: one is a graded
    /// belief, the other is a decision, and the request shape that produced either is part of the
    /// answer's identity (architecture decision 20). Stamping only the model reference would leave a
    /// reader unable to tell a row produced by this shape from a row produced by the shape the survey
    /// measured disagreeing with it.
    /// </para>
    /// <para>
    /// <b>The shape of the stamp follows the repo's existing convention</b> (<c>stylomail-mime/1</c>,
    /// <c>stylomail-chat/1</c>): a readable identity, not a hash. The <c>+</c> keeps the model reference
    /// intact as a leading token so it can still be read off a row, and the shape version follows.
    /// </para>
    /// <para>
    /// <b>The "one is a graded belief, the other is a decision" contrast above is now stale for this
    /// provider and is kept only as the reason the stamp exists.</b> It was written when this adapter
    /// answered with a letter and therefore could not express a graded belief at all. The SystemOne
    /// answer is a probability, so the two rows are closer in kind than that sentence says. The stamp
    /// still earns its place, because the shapes that produced them still differ and were still
    /// measured to disagree. Whether the two rows now <em>agree</em> is open work, and this comment is
    /// a statement of history rather than a current claim about either provider.
    /// </para>
    /// </remarks>
    internal static string SourceVersion(string model)
        => $"{model}+{Version}";

    /// <summary>The key the question at a given position is asked under.</summary>
    /// <remarks>
    /// Positional rather than the dimension id. The ids carry dots and are long, and a key that reads
    /// like an id invites the model to answer with an id as a value. The mapping back to ids is held in
    /// code, where it cannot drift. The server echoes this key back in its <c>answers</c> object, so it
    /// is also how an answer is matched to the dimension that asked for it.
    /// </remarks>
    internal static string KeyFor(int index) => "q" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Declares one question per dimension, each with its type, its instruction and both criteria.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each dimension keeps the shapes StyloMail authored: its <c>instructions</c>, and its
    /// <c>criteria</c> split into the case where the condition holds and the case where it does not.
    /// The instructions are the pipeline's, never the message's. The order of the returned entries is
    /// the order of <paramref name="dimensions"/>, and it is preserved because
    /// <see cref="KeyFor"/> is positional.
    /// </para>
    /// <para>
    /// <b>Every dimension is declared as a Noul question, and that was checked against the source
    /// rather than assumed.</b> All twelve entries in <see cref="SemanticDimensions"/> carry both a
    /// <c>CriteriaTrue</c> and a <c>CriteriaFalse</c>, and the file states at its head that they are
    /// deliberately not a choice across labels because several may hold at once. So the twelve are Noul
    /// questions and the type is not a per-dimension decision: if a dimension is ever added that is
    /// genuinely a choice, this method has to grow a type per entry rather than a constant.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<string, NimbleQuestion> BuildQuestions(
        IReadOnlyList<SemanticDimension> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);

        var questions = new Dictionary<string, NimbleQuestion>(dimensions.Count, StringComparer.Ordinal);

        for (var index = 0; index < dimensions.Count; index++)
        {
            var dimension = dimensions[index];

            questions[KeyFor(index)] = new NimbleQuestion
            {
                Type = NimbleQuestionTypes.Noul,
                Instructions = dimension.Instructions,
                Criteria = new NimbleNoulCriteria
                {
                    True = dimension.CriteriaTrue,
                    False = dimension.CriteriaFalse,
                },
            };
        }

        return questions;
    }

    /// <summary>
    /// Maps a Noul answer to the evidence value it represents, or null when it is not one we can use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The value is the probability itself, and that is a change in what this provider can say.</b>
    /// The port's value is a Noul probability, where a number near 0.5 means genuinely balanced rather
    /// than moderate (<c>Evidence.cs:32-34</c>). The shape this replaced answered with one of two
    /// letters and mapped them to the extremes 1.0 and 0.0, so the graded reading was unavailable in
    /// principle.
    /// </para>
    /// <para>
    /// <b>The sentence that used to stand here is kept, marked as a carry-over, because it is not yet
    /// re-measured.</b> It read: "1.0 or 0.0, with nothing in between, and that is a real reduction in
    /// what this provider can say. This model answers with a decision, not with a graded belief, so it
    /// cannot express balance and never lands near 0.5. Downstream policy that relies on near-0.5
    /// meaning 'unresolved' gets nothing from a local provider and must not be tuned as though it did."
    /// That was measured under <c>nimble-request-shape/1</c>, and it was a property of the A/B letter
    /// alphabet rather than an observation about the model: a two-letter answer cannot land near 0.5
    /// because 0.5 is not one of the two values. The mechanism that made the sentence true is gone with
    /// the letters, so the sentence is retired with them.
    /// </para>
    /// <para>
    /// <b>What is NOT claimed in its place, because it has not been measured.</b> The probe observed
    /// one Noul answer, <c>0.9995100663573931</c> on a synthetic credential-request state. That shows
    /// the shape can produce a value near 1, and it says nothing at all about whether answers land near
    /// 0.5, how the values are distributed, or whether they are stable across runs. The honest position
    /// is therefore neither the old claim nor its opposite: the alphabet no longer forbids the middle,
    /// and whether the model uses it is open work for the measurement phase. Until a run measures the
    /// distribution, policy must not be tuned either as though balance were available or as though it
    /// were impossible.
    /// </para>
    /// <para>
    /// <b>Null means "not a Noul answer we can use", which the caller reports as unavailable.</b> That
    /// covers a missing entry, a <c>type</c> the question did not ask for, and a value outside [0, 1].
    /// All three are refusals rather than repairs: a value that is not a probability is evidence that
    /// the request and the answer are not about the same question, and coercing it into range or
    /// defaulting it would put an invented number into the evidence chain. The old rule was the same
    /// rule about the letters, and it survives the change of alphabet.
    /// </para>
    /// </remarks>
    internal static double? MapNoul(NimbleSystemOneAnswer? answer)
    {
        if (answer is null)
        {
            return null;
        }

        if (!string.Equals(answer.Type, NimbleQuestionTypes.Noul, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (answer.Noul is not { } noul || double.IsNaN(noul) || noul < 0.0 || noul > 1.0)
        {
            return null;
        }

        return noul;
    }
}

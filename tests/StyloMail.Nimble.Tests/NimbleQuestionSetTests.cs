using System.Text;
using System.Text.Json;
using StyloMail.Core;

namespace StyloMail.Nimble.Tests;

/// <summary>
/// Pins the request shape. Every assertion here guards something that was measured to change an
/// answer, so a change that looks cosmetic is a change to what the provider says.
/// </summary>
/// <remarks>
/// <b>This file was rewritten for <c>nimble-request-shape/2</c>, and the alphabet is what changed.</b>
/// Its predecessor pinned a rendered prose system message, a JSON schema whose every property was
/// constrained to one of two letters, and a decoder that mapped those letters to 1.0 and 0.0. All
/// three are gone: a question is now a declared object with its own type and criteria, and its answer
/// is a probability. The assertions below are the same assertions about the same properties, moved
/// onto the shape that ships.
/// </remarks>
public sealed class NimbleQuestionSetTests
{
    // Hoisted to a static readonly field because a constant array argument is CA1861 in this
    // repo's analyzer set, where its severity is an error. The member lists are still pinned
    // where a reviewer sees them.
    private static readonly string[] QuestionMembers = ["type", "instructions", "criteria"];
    private static readonly string[] CriteriaMembers = ["true", "false"];
    [Fact]
    public void Declares_every_dimensions_instruction_and_both_of_its_criteria()
    {
        var questions = NimbleQuestionSet.BuildQuestions(SemanticDimensions.All);

        // The population first, because everything below indexes into the dictionary: a builder that
        // dropped a dimension would make the loop shorter and the assertions fewer without failing.
        Assert.Equal(SemanticDimensions.All.Count, questions.Count);

        for (var index = 0; index < SemanticDimensions.All.Count; index++)
        {
            var dimension = SemanticDimensions.All[index];
            var question = questions[NimbleQuestionSet.KeyFor(index)];

            // The text is StyloMail's, carried by identity rather than by containment. The previous
            // version of this test searched the rendered system message for each fragment, which
            // cannot distinguish "the criterion is this dimension's" from "the criterion appears
            // somewhere in a document that holds all twelve".
            Assert.Equal(dimension.Instructions, question.Instructions);
            Assert.Equal(dimension.CriteriaTrue, question.Criteria.True);
            Assert.Equal(dimension.CriteriaFalse, question.Criteria.False);
        }
    }

    [Fact]
    public void Numbers_its_questions_from_q0_without_gaps()
    {
        var keys = Enumerable
            .Range(0, SemanticDimensions.All.Count)
            .Select(NimbleQuestionSet.KeyFor)
            .ToList();

        Assert.Equal("q0", keys[0]);
        Assert.Equal("q11", keys[^1]);
        Assert.Equal(SemanticDimensions.All.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Declares_every_question_as_the_noul_type()
    {
        // One type for all twelve, checked against the source rather than assumed: every entry in
        // SemanticDimension.cs carries both a CriteriaTrue and a CriteriaFalse, and that file says at
        // its head that they are deliberately not a choice across labels because several may hold at
        // once. A dimension that is genuinely a choice has to grow a type per entry rather than
        // inherit this constant, and this assertion is what would make that a red.
        var questions = NimbleQuestionSet.BuildQuestions(SemanticDimensions.All);

        Assert.All(questions.Values, question => Assert.Equal(NimbleQuestionTypes.Noul, question.Type));

        // The wire word itself, and not only the constant it is read through. `noul` is the server's
        // vocabulary rather than this project's: renaming it produces a different request, and every
        // other assertion in this file compares against the constant and would follow a rename
        // silently.
        Assert.Equal("noul", NimbleQuestionTypes.Noul);
    }

    [Fact]
    public void Sends_exactly_the_members_the_measured_question_carries()
    {
        // The probe's request was three members per question and nothing else. A field added here
        // "because the server probably ignores it" is the shape of change that costs a run to
        // discover, so the member list is pinned where a reviewer sees it.
        var questions = NimbleQuestionSet.BuildQuestions(SemanticDimensions.All);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(questions));

        var first = document.RootElement.GetProperty(NimbleQuestionSet.KeyFor(0));
        Assert.Equal(
            QuestionMembers,
            first.EnumerateObject().Select(property => property.Name));

        Assert.Equal(
            CriteriaMembers,
            first.GetProperty("criteria").EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.9995100663573931, 0.9995100663573931)]
    public void Carries_a_noul_probability_straight_through(double noul, double expected)
    {
        // 0.5 is in this theory deliberately. The shape this replaced answered with one of two letters
        // and could not produce 0.5 at all, because 0.5 was not one of the two values.
        //
        // WHAT THIS ASSERTS: that the ADAPTER does not collapse a mid-range answer. WHAT IT DOES NOT
        // ASSERT, and must not be read as: that the model returns mid-range values. Whether real
        // answers land near 0.5 is unmeasured, and MapNoul's own remarks carry that correction. The
        // 0.9995100663573931 row is the one value the probe actually observed, and it is one point.
        Assert.Equal(expected, NimbleQuestionSet.MapNoul(new NimbleSystemOneAnswer
        {
            Type = NimbleQuestionTypes.Noul,
            Noul = noul,
        }));
    }

    [Fact]
    public void Accepts_the_type_in_any_case()
    {
        // The type is matched case-insensitively, which is the same rule the letter decoder used. It
        // is asserted rather than left implied because the type is a guard: an answer whose type does
        // not match the question asked is refused below, so how that match is decided is the
        // difference between a guard and a wall.
        Assert.Equal(0.75, NimbleQuestionSet.MapNoul(new NimbleSystemOneAnswer
        {
            Type = "NOUL",
            Noul = 0.75,
        }));
    }

    [Fact]
    public void Refuses_a_missing_answer()
    {
        // The key was asked and the server did not answer it. Null, never a default: an invented 0.0
        // would be indistinguishable downstream from an answered absence.
        Assert.Null(NimbleQuestionSet.MapNoul(null));
    }

    [Theory]
    [InlineData("choice", 0.9)]
    [InlineData("score", 0.9)]
    [InlineData(null, 0.9)]
    [InlineData("noul", null)]
    [InlineData("noul", 1.5)]
    [InlineData("noul", -0.1)]
    [InlineData("noul", double.NaN)]
    public void Refuses_anything_that_is_not_a_noul_probability_in_range(string? type, double? noul)
    {
        // Each row is a separate refusal and each is a real body a server can produce: a `choice` or
        // `score` answer for a question asked as `noul`, a type the answer omitted, a `noul` member
        // that is absent, and a value outside the unit interval. The value rows are the ones that
        // matter most: coercing 1.5 into range or defaulting a NaN would put an invented number into
        // the evidence chain, and the old refusal rule about the letters survives the change of
        // alphabet unchanged.
        Assert.Null(NimbleQuestionSet.MapNoul(new NimbleSystemOneAnswer { Type = type, Noul = noul }));
    }

    [Fact]
    public void Leaves_the_room_the_default_body_budget_needs()
    {
        var bytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(NimbleQuestionSet.BuildQuestions(SemanticDimensions.All)));

        var options = new NimbleOptions();

        // A GROWTH GUARD, not the adapter's budget arithmetic. The fit in the classifier measures the
        // whole request, envelope and state included, and shortens the body until it fits; it does not
        // reason about these three numbers at all. What this catches is the question set alone growing
        // until a default body of MaxBodyCharacters no longer leaves anything, which is the point at
        // which the default budget is dead code.
        //
        // 3,825 bytes at the time of writing, INFERRED from SemanticDimension.cs and the default
        // encoder's escaping rather than measured from a build, and it is marked inferred because a
        // derivation sitting beside a measured figure reads as measured. The derivation is in
        // .styloagent/scratch/nimble/measure-questionset-size.py. It replaces a 4,264-byte figure that
        // was measured from the A/B letter renderer, which no longer exists.
        Assert.True(
            bytes + options.MaxBodyCharacters <= options.NumCtx,
            $"the question set is {bytes} bytes, so a body budget of {options.MaxBodyCharacters} "
            + $"leaves nothing of the {options.NumCtx}-byte window");
    }

    [Fact]
    public void The_shape_that_ships_is_the_one_that_was_measured()
    {
        // The version is in the cache key, so it cannot be bumped without invalidating memoised
        // assessments. That is the point of pinning it in a test as well as in the contract.
        //
        // `/2` is the migration to POST /v1/systemone: the question is declared data rather than
        // rendered prose, the answer is a probability rather than one of two letters, and the system
        // message and the answer schema are both gone. The version covers all three, which is what it
        // exists to cover.
        Assert.Equal("nimble-request-shape/2", NimbleQuestionSet.Version);
    }
}

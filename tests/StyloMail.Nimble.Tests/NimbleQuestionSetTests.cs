using System.Text;
using StyloMail.Core;

namespace StyloMail.Nimble.Tests;

/// <summary>
/// Pins the request shape. Every assertion here guards something the survey measured to change an
/// answer, so a change that looks cosmetic is a change to what the provider says.
/// </summary>
public sealed class NimbleQuestionSetTests
{
    [Fact]
    public void Renders_every_dimensions_instructions_and_both_of_its_criteria()
    {
        var rendered = NimbleQuestionSet.RenderSystem(SemanticDimensions.All);

        for (var index = 0; index < SemanticDimensions.All.Count; index++)
        {
            var dimension = SemanticDimensions.All[index];
            Assert.Contains(NimbleQuestionSet.KeyFor(index), rendered, StringComparison.Ordinal);
            Assert.Contains(dimension.Instructions, rendered, StringComparison.Ordinal);
            Assert.Contains(dimension.CriteriaTrue, rendered, StringComparison.Ordinal);
            Assert.Contains(dimension.CriteriaFalse, rendered, StringComparison.Ordinal);
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
    public void Constrains_every_question_to_the_two_offered_codes()
    {
        var schema = NimbleQuestionSet.BuildSchema(SemanticDimensions.All);

        Assert.Equal(SemanticDimensions.All.Count, schema.Properties.Count);

        // Required, not optional. An optional property is one the decoder may omit, and an omitted
        // answer is indistinguishable from a negative one downstream.
        Assert.Equal(schema.Properties.Keys, schema.Required);

        Assert.All(schema.Properties.Values, property =>
            Assert.Equal(NimbleQuestionSet.Codes, property.Enum));
    }

    [Theory]
    [InlineData("A", 1.0)]
    [InlineData("B", 0.0)]
    [InlineData("a", 1.0)]
    [InlineData(" b ", 0.0)]
    public void Maps_the_two_codes_to_the_extremes_and_nothing_between(string code, double expected)
    {
        Assert.Equal(expected, NimbleQuestionSet.MapCode(code));
    }

    [Theory]
    [InlineData("C")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_to_turn_anything_else_into_a_value(string? code)
    {
        // Null, never a default. A code we did not offer is reported as unavailable; inventing a
        // score for it would put a fabricated answer into the evidence chain.
        Assert.Null(NimbleQuestionSet.MapCode(code));
    }

    [Fact]
    public void Leaves_the_room_the_default_body_budget_needs()
    {
        var bytes = Encoding.UTF8.GetByteCount(NimbleQuestionSet.RenderSystem(SemanticDimensions.All));
        var options = new NimbleOptions();

        // The three defaults are a single budget and have to be read together: the questions, then the
        // body, must fit the window. Measured, the questions alone are 4,264 bytes, which is why the
        // body default is 2,500 rather than the hosted adapter's 12,000. If someone lengthens a
        // criterion until this fails, the failure is a window that no longer holds a message.
        Assert.True(
            bytes + options.MaxBodyCharacters <= options.NumCtx,
            $"the question set is {bytes} bytes, leaving "
            + $"{options.NumCtx - bytes} for a body budget of {options.MaxBodyCharacters}");
    }

    [Fact]
    public void The_shape_that_ships_is_the_one_that_was_measured()
    {
        // The version is in the cache key, so it cannot be bumped without invalidating memoised
        // assessments. That is the point of pinning it in a test as well as in the contract.
        Assert.Equal("nimble-request-shape/1", NimbleQuestionSet.Version);
    }
}

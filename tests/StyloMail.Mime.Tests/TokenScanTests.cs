using System.Reflection;
using System.Text.RegularExpressions;

namespace StyloMail.Mime.Tests;

public sealed class TokenScanTests
{
    private static readonly Type Tools = typeof(BoundedMimeMessageAnalyzer).Assembly.GetType("StyloMail.Mime.TextTools", throwOnError: true)!;
    private static readonly Func<string, IReadOnlyList<string>> Tokenize = Tools.GetMethod("Tokenize", BindingFlags.Public | BindingFlags.Static)!
        .CreateDelegate<Func<string, IReadOnlyList<string>>>();
    private static readonly Func<string, int> CountTokens = Tools.GetMethod("CountTokens", BindingFlags.Public | BindingFlags.Static)!
        .CreateDelegate<Func<string, int>>();
    private static readonly Regex ReferencePattern = new(@"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("a I 1 ! _ .")]
    [InlineData("Alpha BETA ab12 123 A1 a b 1 2")]
    [InlineData("ΑΒΓ αβγ ЩЁЛК шёлк İI ıi ＡＢ ۱۲٣")]
    [InlineData("cafe\u0301 e\u0301lan naïve \u0301ab")]
    [InlineData("one-two https://example.invalid email@example.invalid email url id num")]
    [InlineData("\uD800Alpha\uDC00 Beta\uD800\uD800 Gamma")]
    [InlineData("New body\r\n\r\n> OLD HISTORY 123")]
    public void Ordered_tokens_and_count_preserve_the_original_regex_contract(string? value)
    {
        var input = value!;
        var expected = ReferenceTokens(input);

        Assert.Equal(expected, Tokenize(input));
        Assert.Equal(expected.Count, CountTokens(input));
    }

    [Fact]
    public void Count_only_scan_avoids_per_token_materialization()
    {
        var input = string.Concat(Enumerable.Repeat("ALPHA beta 123 delta ", 10_000));
        var expected = ReferenceTokens(input).Count;
        var referenceBytes = Allocated(() => AssertCount(ReferenceTokens(input).Count, expected));
        var actualBytes = Allocated(() => AssertCount(CountTokens(input), expected));

        Assert.Equal(40_000, expected);
        Assert.True(referenceBytes > input.Length, "The materializing reference must allocate before zero can prove absence.");
        // The regression is per-token materialization, not occasional runtime/regex setup.
        Assert.True(actualBytes < referenceBytes / 100,
            $"Count scan allocated {actualBytes} bytes; materializing reference allocated {referenceBytes} bytes.");
    }

    [Fact]
    public void Ordered_token_scan_avoids_the_materializing_reference_allocation()
    {
        var input = string.Concat(Enumerable.Repeat("ALPHA beta 123 delta ", 10_000));
        var expected = ReferenceTokens(input);
        Assert.Equal(expected, Tokenize(input));
        var referenceBytes = Allocated(() => AssertCount(ReferenceTokens(input).Count, expected.Count));
        var actualBytes = Allocated(() => AssertCount(Tokenize(input).Count, expected.Count));

        Assert.Equal(40_000, expected.Count);
        Assert.True(referenceBytes > input.Length, "The materializing reference is the positive allocation control.");
        Assert.True(actualBytes < referenceBytes / 2,
            $"Span scan allocated {actualBytes} bytes; materializing reference allocated {referenceBytes} bytes.");
    }

    private static IReadOnlyList<string> ReferenceTokens(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        var matches = ReferencePattern.Matches(value);
        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches) tokens.Add(match.Value.ToLowerInvariant());
        return tokens;
    }

    private static void AssertCount(int actual, int expected)
    {
        if (actual != expected) throw new InvalidOperationException("Measured token count changed.");
    }

    private static long Allocated(Action action)
    {
        for (var warmup = 0; warmup < 10; warmup++) action();
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        return bytes;
    }
}

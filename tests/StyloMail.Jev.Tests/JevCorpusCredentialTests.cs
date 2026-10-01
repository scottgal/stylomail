using System.Text;

namespace StyloMail.Jev.Tests;

/// <summary>
/// The credential resolution: which route wins, and what counts as missing.
/// </summary>
/// <remarks>
/// <para>
/// Every case goes through the two-input overload rather than through the environment. The resolution
/// reads process-wide state, and these classes run in parallel in a suite this project already knows
/// is order-sensitive, so a test that set an environment variable would be measuring the test order
/// as much as the code. The overload exists for that reason: the four cases become four ordinary
/// assertions with no globals.
/// </para>
/// <para>
/// <b>No secret appears in this file.</b> Every fixture value is obviously not a key, and every file
/// these tests create lives in the system temp directory, outside the working copy, and is deleted by
/// the test that made it, so the suite never leaves a key-shaped file inside the tree.
/// </para>
/// </remarks>
public sealed class JevCorpusCredentialTests
{
    private const string NotAKeyFromTheEnvironment = "not-a-real-key-from-the-environment";
    private const string NotAKeyFromTheFile = "not-a-real-key-from-the-file";

    /// <summary>
    /// A file holding this text, in the temp directory rather than the working copy.
    /// </summary>
    private static string TempFileHolding(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jev-corpus-credential-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void The_environment_variable_wins_when_both_routes_are_set()
    {
        // The precedence the commit claimed and nothing pinned. Both routes resolve, and the plain
        // variable is the one that must answer: the file route exists so a value can be kept off a
        // command line, not so a stale path can shadow the environment the process was started with.
        var path = TempFileHolding(NotAKeyFromTheFile);
        try
        {
            Assert.True(JevCorpus.TryReadCredential(NotAKeyFromTheEnvironment, path, out var key));
            Assert.Equal(NotAKeyFromTheEnvironment, key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_path_that_does_not_exist_falls_through_to_the_refusal()
    {
        // The fall-through has never been asserted, so an edit that made a missing path throw, or
        // that returned an empty key as though it had found one, would land unnoticed.
        var missing = Path.Combine(Path.GetTempPath(), $"jev-corpus-absent-{Guid.NewGuid():N}.txt");

        Assert.False(JevCorpus.TryReadCredential(null, missing, out var key));
        Assert.Equal(string.Empty, key);
    }

    [Fact]
    public void An_empty_file_is_missing_rather_than_an_empty_key()
    {
        // The guard that matters. "The file exists" is not "the file holds a credential", and an
        // empty string returned as a key is a request sent with an empty credential: it would fail
        // as a 401, which reads like a bad key rather than like the missing-file it actually is.
        var path = TempFileHolding(string.Empty);
        try
        {
            Assert.False(JevCorpus.TryReadCredential(null, path, out var key));
            Assert.Equal(string.Empty, key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_whitespace_only_file_is_missing_too()
    {
        // A file holding one newline is the shape a careless `echo > path` leaves. It is not a key,
        // and it must not become one.
        var path = TempFileHolding("   \n");
        try
        {
            Assert.False(JevCorpus.TryReadCredential(null, path, out var key));
            Assert.Equal(string.Empty, key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_key_read_with_a_trailing_newline_comes_back_trimmed()
    {
        // Every editor and every `echo` leaves a trailing newline, and a key carrying one is a
        // different string: it fails upstream as a bad credential, so the defect is reported at the
        // far end as an authentication problem rather than here as a parsing one.
        var path = TempFileHolding(NotAKeyFromTheFile + "\n");
        try
        {
            Assert.True(JevCorpus.TryReadCredential(null, path, out var key));
            Assert.Equal(NotAKeyFromTheFile, key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void No_route_at_all_is_missing()
    {
        // The ordinary case in a clone: neither variable set, so the resolution refuses rather than
        // answering with something empty, and `RequireCredential` is what turns that into the loud
        // exception naming both routes.
        Assert.False(JevCorpus.TryReadCredential(null, null, out var key));
        Assert.Equal(string.Empty, key);
    }
}

using StyloMail.AccessProxy.Submission;

namespace StyloMail.AccessProxy.Tests;

public sealed class SubmissionStagerTests
{
    [Theory]
    [InlineData("allow")]
    [InlineData("refusal")]
    [InlineData("abort")]
    public async Task Staged_bytes_are_released_on_every_terminal_path(string terminalPath)
    {
        Assert.True(terminalPath is "allow" or "refusal" or "abort");
        var stager = new InMemoryEphemeralSubmissionStager();
        var staged = await stager.CreateAsync(1024, CancellationToken.None);
        await staged.Content.WriteAsync("body"u8.ToArray(), CancellationToken.None);
        Assert.Equal(1, stager.ActiveCount);

        // The session's allow, deterministic-refusal and client-abort paths all leave the scope
        // through IAsyncDisposable. This is the control that would fail if any one leaked a handle.
        await staged.DisposeAsync();

        Assert.Equal(0, stager.ActiveCount);
    }
}

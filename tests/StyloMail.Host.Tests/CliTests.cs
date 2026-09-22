using StyloMail.Host.Cli;

namespace StyloMail.Host.Tests;

/// <summary>
/// The command-line surface.
/// </summary>
/// <remarks>
/// The command that matters most here is <c>assess</c>, because of what it must not do: an operator
/// running it against a real message on a real workstation must not have that message silently
/// transmitted to a hosted classifier. Provider use is opt-in.
/// </remarks>
public sealed class CliTests
{
    private const string SensitiveBody = "Wire the payment to account 99887766 today.";

    private static string WriteFixture(TestHost host, string name, string content)
    {
        var path = Path.Combine(host.Root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string FixtureMessage(string body) =>
        $"""
        From: "Sender" <sender@example.com>
        To: <recipient@example.com>
        Subject: Quarterly figures
        Message-ID: <abc123@example.com>
        Date: Mon, 21 Sep 2026 09:00:00 +0000
        Content-Type: text/plain; charset=utf-8

        {body}
        """;

    [Fact]
    public async Task Assess_does_not_transmit_message_content_by_default()
    {
        // Constraint: CLI assessment must not silently transmit private content externally.
        // "Silently" is the operative word, the default path stays entirely local.
        using var host = new TestHost();
        var path = WriteFixture(host, "message.eml", FixtureMessage(SensitiveBody));

        var output = new StringWriter();
        var exitCode = await CliCommands.AssessAsync(
            host.Services,
            new AssessCommand(path, TenantId: null, UseSemanticProvider: false, AsJson: false),
            output);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, host.Assessor.CallCount);

        var text = output.ToString();
        Assert.Contains("not configured", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SensitiveBody, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assess_uses_the_provider_only_when_explicitly_asked()
    {
        using var host = new TestHost();
        var path = WriteFixture(host, "message.eml", FixtureMessage(SensitiveBody));

        var exitCode = await CliCommands.AssessAsync(
            host.Services,
            new AssessCommand(path, TenantId: null, UseSemanticProvider: true, AsJson: true),
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(1, host.Assessor.CallCount);
    }

    [Fact]
    public async Task Assess_refuses_to_pretend_when_the_provider_is_asked_for_but_absent()
    {
        // Asking explicitly and getting a quiet local-only answer would be the worst outcome: the
        // operator would believe the semantic layer ran.
        using var host = new TestHost().WithoutAssessor();
        var path = WriteFixture(host, "message.eml", FixtureMessage(SensitiveBody));

        var output = new StringWriter();
        var exitCode = await CliCommands.AssessAsync(
            host.Services,
            new AssessCommand(path, TenantId: null, UseSemanticProvider: true, AsJson: false),
            output);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("assessor", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_reports_a_missing_file_rather_than_crashing()
    {
        using var host = new TestHost();

        var output = new StringWriter();
        var exitCode = await CliCommands.AssessAsync(
            host.Services,
            new AssessCommand(
                Path.Combine(host.Root, "absent.eml"),
                TenantId: null,
                UseSemanticProvider: false,
                AsJson: false),
            output);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Replay_produces_identical_output_for_identical_input()
    {
        // Deterministic replay is the point of the command: a fixture directory that decides
        // differently on two consecutive runs cannot be used to compare policy versions.
        using var host = new TestHost();
        var fixtures = Path.Combine(host.Root, "fixtures");
        Directory.CreateDirectory(fixtures);
        File.WriteAllText(Path.Combine(fixtures, "a.eml"), FixtureMessage("First message."));
        File.WriteAllText(Path.Combine(fixtures, "b.eml"), FixtureMessage("Second message."));

        var first = new StringWriter();
        var second = new StringWriter();

        var command = new ReplayCommand(fixtures, TenantId: null, AsJson: true);
        await CliCommands.ReplayAsync(host.Services, command, first);
        await CliCommands.ReplayAsync(host.Services, command, second);

        Assert.Equal(first.ToString(), second.ToString());
        Assert.Contains("a.eml", first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_never_transmits_fixture_content()
    {
        using var host = new TestHost();
        var fixtures = Path.Combine(host.Root, "fixtures");
        Directory.CreateDirectory(fixtures);
        File.WriteAllText(Path.Combine(fixtures, "a.eml"), FixtureMessage("First message."));

        await CliCommands.ReplayAsync(
            host.Services,
            new ReplayCommand(fixtures, TenantId: null, AsJson: true),
            new StringWriter());

        Assert.Equal(0, host.Assessor.CallCount);
    }

    [Fact]
    public async Task Quarantine_list_shows_quarantined_mail_for_its_tenant()
    {
        using var host = new TestHost();
        host.Assessor.Action = StyloMail.Core.MailAction.Quarantine;
        var queueId = await QuarantinedSubmissionAsync(host);

        var output = new StringWriter();
        var exitCode = await CliCommands.QuarantineListAsync(
            host.Services,
            new QuarantineListCommand(TestPrincipals.AcmeTenant, AsJson: true),
            output);

        Assert.Equal(0, exitCode);
        Assert.Contains(queueId, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quarantine_list_shows_nothing_to_another_tenant()
    {
        // Quarantined mail is the most sensitive listing on the surface: it is mail that was not
        // delivered. A tenant must not see another's, and must not be able to tell a tenant with
        // no quarantined mail from a tenant that does not exist.
        using var host = new TestHost();
        host.Assessor.Action = StyloMail.Core.MailAction.Quarantine;
        var queueId = await QuarantinedSubmissionAsync(host);

        var output = new StringWriter();
        await CliCommands.QuarantineListAsync(
            host.Services,
            new QuarantineListCommand(TestPrincipals.GlobexTenant, AsJson: true),
            output);

        Assert.DoesNotContain(queueId, output.ToString(), StringComparison.Ordinal);
    }

    private static async Task<string> QuarantinedSubmissionAsync(TestHost host)
    {
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = System.Net.Http.Json.JsonContent.Create(TestMessages.Request()),
        };
        request.Headers.Add("Idempotency-Key", $"cli-q-{Guid.NewGuid():N}");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("queueId").GetString()!;
    }

    [Fact]
    public void An_unrecognised_command_is_not_silently_served()
    {
        // Falling through to `serve` on a typo would start a listener when the operator asked for
        // something else entirely.
        var dispatched = CliApplication.TryParse(["frobnicate"], out var command, out var error);

        Assert.Null(command);
        Assert.NotNull(error);
        Assert.Contains("frobnicate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_command_is_serve()
    {
        var dispatched = CliApplication.TryParse([], out var command, out _);

        Assert.True(dispatched);
        Assert.IsType<ServeCommand>(command);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StyloMail.Host.Auth;
using StyloMail.Host.Cli;
using StyloMail.Host.Storage;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Tests;

/// <summary>
/// Minted API keys: the store, the CLI, and the authentication path both channels share.
/// </summary>
/// <remarks>
/// The properties pinned here are the ones that make a credential path safe rather than the ones
/// that make it work: the store holds no recoverable value, the store's answer wins whole, a
/// revocation is effective on the next request rather than at the next restart, and the value is
/// printed to exactly one place.
/// </remarks>
public sealed class MintedKeyTests
{
    /// <summary>
    /// Mints a key through the CLI and returns the value it printed.
    /// </summary>
    /// <remarks>
    /// The first line of the output, because that is what the command promises an operator can
    /// capture. A test that reached into the store for the value would be testing a route to it that
    /// deliberately does not exist.
    /// </remarks>
    private static async Task<string> MintAsync(
        TestHost host,
        string principalId,
        string tenantId,
        string[] privileges,
        string[]? senders = null)
    {
        var output = new StringWriter();
        var exitCode = await KeyCommands.CreateAsync(
            host.Services,
            new KeyCreateCommand(principalId, tenantId, privileges, senders ?? [], "test-operator"),
            output);

        Assert.Equal(0, exitCode);

        var value = output.ToString().Split('\n')[0].Trim();
        Assert.StartsWith("smk_", value, StringComparison.Ordinal);

        return value;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        TestHost host,
        CliCommand command) =>
        command switch
        {
            KeyCreateCommand create => await Capture(host, output => KeyCommands.CreateAsync(host.Services, create, output)),
            KeyListCommand list => await Capture(host, output => KeyCommands.ListAsync(host.Services, list, output)),
            KeyRevokeCommand revoke => await Capture(host, output => KeyCommands.RevokeAsync(host.Services, revoke, output)),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static async Task<(int ExitCode, string Output)> Capture(
        TestHost host,
        Func<StringWriter, Task<int>> run)
    {
        var output = new StringWriter();
        var exitCode = await run(output);
        return (exitCode, output.ToString());
    }

    // ---------------------------------------------------------------------------------------------
    // The store holds digests only
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_store_holds_a_slow_digest_and_never_the_key()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "svc-store-assess", TestPrincipals.AcmeTenant, ["Assess"]);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(host.Root, "host.db")}");
        connection.Open();

        string row;
        byte[] digest;
        byte[] salt;

        using (var command = connection.CreateCommand())
        {
            // Every column of the row, spelled out. A key that reached any of them would reach the
            // others the same way, and `SELECT *` would stop covering the row the day a column is
            // added rather than failing.
            command.CommandText =
                """
                SELECT key_id, principal_id, tenant_id, kdf_algorithm, kdf_iterations, hex(salt),
                       hex(digest), privileges, approved_sender_identities, created_at, created_by,
                       revoked_at, revoked_by
                FROM host_principal;
                """;

            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());

            var fields = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                fields.Add(reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString() ?? string.Empty);
            }

            row = string.Join('|', fields);

            Assert.False(reader.Read(), "The store held more than one principal after one mint.");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT salt, digest FROM host_principal;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            salt = reader.GetFieldValue<byte[]>(0);
            digest = reader.GetFieldValue<byte[]>(1);
        }

        Assert.DoesNotContain(key, row, StringComparison.Ordinal);

        // A bare SHA-256 of the key would be offline-crackable by anyone who copied this file, which
        // is a plaintext store with extra steps. The stored digest must not be that value.
        var bareHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        Assert.False(
            CryptographicOperations.FixedTimeEquals(bareHash, digest),
            "The stored digest is a bare SHA-256 of the key.");

        Assert.Equal(32, digest.Length);
        Assert.Equal(16, salt.Length);

        // The secret half alone must not appear either, in any encoding, because a store that leaked
        // it would have leaked the credential as surely as storing the whole key.
        var secret = key[(key.LastIndexOf('_') + 1)..];
        Assert.DoesNotContain(secret, row, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_minted_key_gets_its_own_salt()
    {
        // Two rows derived from two different secrets still have to differ in the store for the
        // per-key salt to be doing anything; the sharper property is that the same secret minted
        // twice would too, which this pins by checking the salts differ across two principals.
        using var host = new TestHost();
        await MintAsync(host, "svc-a", TestPrincipals.AcmeTenant, ["Assess"]);
        await MintAsync(host, "svc-b", TestPrincipals.AcmeTenant, ["Assess"]);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(host.Root, "host.db")}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hex(salt) FROM host_principal ORDER BY principal_id;";

        var salts = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            salts.Add(reader.GetString(0));
        }

        Assert.Equal(2, salts.Count);
        Assert.NotEqual(salts[0], salts[1]);
    }

    [Fact]
    public void A_stored_digest_of_the_wrong_length_reads_as_no_match_rather_than_throwing()
    {
        // A truncated or hand-edited digest must be an authentication failure, not an exception out
        // of the request pipeline. CryptographicOperations rejects unequal lengths.
        Assert.False(MintedApiKey.DigestMatches(new byte[16], new byte[32]));
        Assert.False(MintedApiKey.DigestMatches([], new byte[32]));
        Assert.True(MintedApiKey.DigestMatches([1, 2, 3], [1, 2, 3]));
    }

    [Theory]
    [InlineData("smk_abc_secret", "abc")]
    [InlineData("smk_abc_se_cr_et", "abc")]
    [InlineData("smk_AbC-123_x", "AbC-123")]
    // The case the id alphabet has to exclude. An id carrying the separator is read short, which is
    // a lookup miss rather than an error, so the credential would simply never resolve.
    [InlineData("smk_ab_cd_secret", "ab")]
    public void A_minted_value_names_its_own_row(string value, string expected)
    {
        Assert.True(MintedApiKey.TryReadKeyId(value, out var keyId));
        Assert.Equal(expected, keyId);
    }

    [Fact]
    public void Every_minted_value_names_its_own_row()
    {
        // The property the case above makes fatal if it is ever broken, asserted over the values this
        // host actually produces rather than over the parser's contract. An id alphabet containing
        // the separator fails this roughly 30% of the time, which is exactly the shape of failure
        // that reads as a flaky host rather than as a defect.
        for (var i = 0; i < 64; i++)
        {
            var material = MintedApiKey.Mint();

            Assert.Matches("^[0-9a-f]{32}$", material.KeyId);
            Assert.True(MintedApiKey.TryReadKeyId(material.Value, out var keyId));
            Assert.Equal(material.KeyId, keyId);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-key-acme-send")]
    [InlineData("smk_")]
    [InlineData("smk_abc")]
    [InlineData("smk__secret")]
    [InlineData("smk_abc_")]
    [InlineData("xmk_abc_secret")]
    public void Anything_that_is_not_a_minted_value_names_no_row(string? value)
    {
        // Total, not throwing: this runs on the path every unrecognised credential takes, where
        // "not a minted key" is an ordinary answer that falls through to the other source.
        Assert.False(MintedApiKey.TryReadKeyId(value, out var keyId));
        Assert.Empty(keyId);
    }

    // ---------------------------------------------------------------------------------------------
    // Store first, wholesale, with the environment as a fallback
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_minted_key_authenticates_over_http()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);

        using var client = host.ClientAs(key);
        var response = await client.GetAsync("/v1/senders");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"status={response.StatusCode} body={await response.Content.ReadAsStringAsync()} "
            + $"db={Path.Combine(host.Root, "host.db")} exists={File.Exists(Path.Combine(host.Root, "host.db"))}");
    }

    [Fact]
    public async Task A_minted_key_carries_exactly_the_privileges_it_was_minted_with()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "svc-store-assessor", TestPrincipals.AcmeTenant, ["Assess"]);

        using var client = host.ClientAs(key);

        var assessed = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        Assert.Equal(HttpStatusCode.OK, assessed.StatusCode);

        // Review was not granted, so the ledger listing is refused. A minted key that could reach
        // this would have been granted more than it was asked for.
        var listed = await client.GetAsync("/v1/senders");
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
    }

    [Fact]
    public async Task A_key_carrying_the_wrong_secret_for_a_real_row_is_refused()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "svc-store-assessor", TestPrincipals.AcmeTenant, ["Assess"]);

        // Same row, same shape, one character different at the end: the expensive path runs and must
        // reject it.
        var tampered = key[..^1] + (key[^1] == 'A' ? 'B' : 'A');

        using var client = host.ClientAs(tampered);
        var response = await client.GetAsync("/v1/senders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_store_wins_wholesale_and_never_unions_with_the_environment()
    {
        using var host = new TestHost();

        // The same principal is configured with every privilege the host has, and minted with one.
        var minted = await MintAsync(
            host,
            TestPrincipals.AcmeOperatorPrincipal,
            TestPrincipals.AcmeTenant,
            ["Assess"]);

        using var environmentKey = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        using var mintedKey = host.ClientAs(minted);

        // The configuration entry does not authenticate at all, with the key that used to work.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await environmentKey.GetAsync("/v1/senders")).StatusCode);

        // And the minted key has the minted privilege set, not the union of the two. Review came
        // from the configuration entry and must not travel with the store's key.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await mintedKey.GetAsync("/v1/senders")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await mintedKey.PostAsJsonAsync("/v1/assessments", TestMessages.Request())).StatusCode);
    }

    [Fact]
    public async Task Revoking_a_mint_does_not_hand_authority_back_to_the_configuration_entry()
    {
        using var host = new TestHost();

        var minted = await MintAsync(
            host,
            TestPrincipals.AcmeOperatorPrincipal,
            TestPrincipals.AcmeTenant,
            ["Assess"]);

        var (exitCode, _) = await RunAsync(
            host,
            new KeyRevokeCommand(TestPrincipals.AcmeOperatorPrincipal, "test-operator"));

        Assert.Equal(0, exitCode);

        // The minted key is dead, and so is the configuration entry of the same name. If the second
        // were alive, a revocation would have quietly restored a credential nobody re-examined.
        using (var mintedKey = host.ClientAs(minted))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await mintedKey.GetAsync("/v1/senders")).StatusCode);
        }

        using (var environmentKey = host.ClientAs(TestPrincipals.AcmeOperatorKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await environmentKey.GetAsync("/v1/senders")).StatusCode);
        }
    }

    [Fact]
    public async Task An_environment_principal_the_store_has_not_claimed_still_authenticates()
    {
        using var host = new TestHost();

        // Minting a key for a different principal must not disturb the configured ones. Two sources
        // are honoured at once; that is the whole reason the fallback exists.
        await MintAsync(host, "svc-store-assessor", TestPrincipals.AcmeTenant, ["Assess"]);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/senders")).StatusCode);

        var (_, listed) = await RunAsync(host, new KeyListCommand(AsJson: false));

        Assert.Contains("environment", listed, StringComparison.Ordinal);
        Assert.Contains("store", listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_minted_principals_tenant_comes_from_its_row_not_from_the_request()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "svc-store-globex", TestPrincipals.GlobexTenant, ["Review"]);

        using var client = host.ClientAs(key);
        var response = await client.GetAsync("/v1/senders");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(TestPrincipals.GlobexSenderPrincipal, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TestPrincipals.AcmeSenderPrincipal, body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Revocation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_revoked_key_stops_authenticating_on_the_very_next_request()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);

        using var client = host.ClientAs(key);

        // Twice, so the resolution is certainly in whatever the host caches, and so a first-request
        // special case cannot pass for cache invalidation.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/senders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/senders")).StatusCode);

        var (exitCode, _) = await RunAsync(host, new KeyRevokeCommand("svc-store-reviewer", "test-operator"));
        Assert.Equal(0, exitCode);

        // Same client, same process, same cache. A revocation a cache serves past its moment is the
        // same defect one layer down from a revocation that does not happen.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/senders")).StatusCode);
    }

    [Fact]
    public async Task A_revocation_written_by_another_process_is_seen_immediately()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);

        using var client = host.ClientAs(key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/senders")).StatusCode);

        // A second store over the same database file, which is what `stylomail key revoke` is: a
        // different process that cannot reach into the server's memory. The store's own change
        // counter is the channel, and this is what proves it is the channel rather than a shared
        // object.
        var elsewhere = new MintedPrincipalStore(new HostDatabase(
            Options.Create(new HostStorageOptions
            {
                DatabasePath = Path.Combine(host.Root, "host.db"),
                SpoolRoot = Path.Combine(host.Root, "spool"),
            })));

        Assert.True(elsewhere.Revoke("svc-store-reviewer", "another-process", DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/senders")).StatusCode);
    }

    [Fact]
    public async Task Key_revoke_refuses_an_environment_principal_and_names_the_configuration()
    {
        using var host = new TestHost();

        var (exitCode, output) = await RunAsync(
            host,
            new KeyRevokeCommand(TestPrincipals.AcmeSenderPrincipal, "test-operator"));

        Assert.Equal(3, exitCode);

        // A refusal that does not say where to go is a no-op with better manners: this one names the
        // entry that owns the principal.
        Assert.Contains("StyloMail:Auth:Principals", output, StringComparison.Ordinal);
        Assert.Contains("refused", output, StringComparison.Ordinal);

        // And it did not revoke anything: the configured key still authenticates. Checked on a route
        // this principal actually holds, so a privilege refusal cannot be mistaken for the refusal
        // under test.
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request())).StatusCode);
    }

    [Fact]
    public async Task Key_revoke_refuses_a_principal_the_host_has_never_heard_of()
    {
        using var host = new TestHost();

        var (exitCode, output) = await RunAsync(host, new KeyRevokeCommand("nobody", "test-operator"));

        Assert.Equal(2, exitCode);
        Assert.Contains("no principal named", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoking_twice_reports_the_state_rather_than_failing_the_second_time()
    {
        using var host = new TestHost();
        await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);

        var (first, _) = await RunAsync(host, new KeyRevokeCommand("svc-store-reviewer", "test-operator"));
        var (second, output) = await RunAsync(host, new KeyRevokeCommand("svc-store-reviewer", "test-operator"));

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Contains("already revoked", output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // The CLI's promises about the value
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Key_create_prints_the_value_once_and_says_it_will_not_be_shown_again()
    {
        using var host = new TestHost();

        var (exitCode, output) = await RunAsync(
            host,
            new KeyCreateCommand("svc-store-assessor", TestPrincipals.AcmeTenant, ["Assess"], [], "test-operator"));

        Assert.Equal(0, exitCode);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("smk_", lines[0], StringComparison.Ordinal);
        Assert.Contains("will not be shown again", output, StringComparison.OrdinalIgnoreCase);

        // Exactly once. A second mention is how a value ends up in a captured log.
        Assert.Equal(1, lines.Count(line => line.Trim() == lines[0].Trim()));
    }

    [Fact]
    public async Task Key_list_shows_neither_the_key_nor_its_digest()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);

        // Both renderings, because the JSON one is the one a console or a script reads and the one
        // that would be easiest to widen by accident.
        foreach (var command in new CliCommand[] { new KeyListCommand(AsJson: false), new KeyListCommand(AsJson: true) })
        {
            var (exitCode, output) = await RunAsync(host, command);
            Assert.Equal(0, exitCode);

            Assert.Contains("svc-store-reviewer", output, StringComparison.Ordinal);

            Assert.DoesNotContain(key, output, StringComparison.Ordinal);
            Assert.DoesNotContain(key["smk_".Length..], output, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
                output,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Key_list_reports_the_source_and_the_status_of_every_principal()
    {
        using var host = new TestHost();
        await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);

        var (_, output) = await RunAsync(host, new KeyListCommand(AsJson: false));

        Assert.Contains("svc-store-reviewer", output, StringComparison.Ordinal);
        Assert.Contains("active", output, StringComparison.Ordinal);
        Assert.Contains(TestPrincipals.AcmeReviewerPrincipal, output, StringComparison.Ordinal);
        Assert.Contains("read-only", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_list_shows_a_configuration_entry_the_store_has_shadowed()
    {
        using var host = new TestHost();
        await MintAsync(host, TestPrincipals.AcmeReviewerPrincipal, TestPrincipals.AcmeTenant, ["Review"]);

        var (_, output) = await RunAsync(host, new KeyListCommand(AsJson: false));

        // The visibility half of the wholesale rule. Without this the first sign that a
        // configuration entry went inert is a credential that stops working with nothing saying why.
        Assert.Contains("shadowed by store", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_create_refuses_an_unknown_privilege_by_name()
    {
        using var host = new TestHost();

        var (exitCode, output) = await RunAsync(
            host,
            new KeyCreateCommand("svc-store-assessor", TestPrincipals.AcmeTenant, ["Superuser"], [], "test-operator"));

        Assert.Equal(2, exitCode);
        Assert.Contains("Superuser", output, StringComparison.Ordinal);
        Assert.Contains("Assess", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_create_refuses_a_key_that_would_grant_nothing()
    {
        using var host = new TestHost();

        var (exitCode, output) = await RunAsync(
            host,
            new KeyCreateCommand("svc-store-assessor", TestPrincipals.AcmeTenant, [], [], "test-operator"));

        Assert.Equal(2, exitCode);
        Assert.Contains("at least one privilege", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_create_refuses_a_second_live_key_for_one_principal()
    {
        using var host = new TestHost();
        var first = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);

        var (exitCode, output) = await RunAsync(
            host,
            new KeyCreateCommand("svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"], [], "test-operator"));

        Assert.Equal(2, exitCode);
        Assert.Contains("already has a live minted key", output, StringComparison.Ordinal);

        // The refusal must not have invalidated the key that already existed.
        using var client = host.ClientAs(first);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/senders")).StatusCode);
    }

    [Fact]
    public async Task A_principal_can_be_minted_again_after_its_key_is_revoked()
    {
        using var host = new TestHost();
        var first = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);
        await RunAsync(host, new KeyRevokeCommand("svc-store-reviewer", "test-operator"));

        var second = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);
        Assert.NotEqual(first, second);

        using (var retired = host.ClientAs(first))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await retired.GetAsync("/v1/senders")).StatusCode);
        }

        using (var replacement = host.ClientAs(second))
        {
            Assert.Equal(HttpStatusCode.OK, (await replacement.GetAsync("/v1/senders")).StatusCode);
        }
    }

    [Fact]
    public async Task A_replacement_key_can_itself_be_revoked()
    {
        // A principal accumulates one row per mint, so a revocation that picked its row by principal
        // id alone would find the oldest and report "already revoked" forever, leaving the current
        // key permanently un-revocable. Nothing else in this suite revokes twice, which is how that
        // stayed invisible until it was read for.
        using var host = new TestHost();

        var first = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);
        Assert.Equal(0, (await RunAsync(host, new KeyRevokeCommand("svc-store-reviewer", "operator"))).ExitCode);

        var second = await MintAsync(host, "svc-store-reviewer", TestPrincipals.AcmeTenant, ["Review"]);

        using (var live = host.ClientAs(second))
        {
            Assert.Equal(HttpStatusCode.OK, (await live.GetAsync("/v1/senders")).StatusCode);
        }

        var (exitCode, output) = await RunAsync(host, new KeyRevokeCommand("svc-store-reviewer", "operator"));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("already revoked", output, StringComparison.Ordinal);

        using (var retired = host.ClientAs(second))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await retired.GetAsync("/v1/senders")).StatusCode);
        }

        using (var older = host.ClientAs(first))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await older.GetAsync("/v1/senders")).StatusCode);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Both channels resolve through the one directory
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_smtp_submission_channel_authenticates_the_same_minted_key()
    {
        using var host = new TestHost();
        var key = await MintAsync(
            host,
            "store-sender",
            TestPrincipals.AcmeTenant,
            ["Send"],
            ["store-sender@acme.example"]);

        // The instance the SMTP listener is constructed with, resolved from the container rather
        // than built by the test, so this is the object a live session would call.
        var authenticator = host.Services.GetRequiredService<ISubmissionAuthenticator>();

        var principal = await authenticator.AuthenticateAsync("store-sender", key, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal("store-sender", principal!.PrincipalId);
        Assert.Equal(TestPrincipals.AcmeTenant, principal.TenantId);
        Assert.Equal(["store-sender@acme.example"], principal.ApprovedSenderIdentities);
    }

    [Fact]
    public async Task The_smtp_submission_channel_refuses_a_revoked_minted_key()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "store-sender", TestPrincipals.AcmeTenant, ["Send"]);

        var authenticator = host.Services.GetRequiredService<ISubmissionAuthenticator>();
        Assert.NotNull(await authenticator.AuthenticateAsync("store-sender", key, CancellationToken.None));

        await RunAsync(host, new KeyRevokeCommand("store-sender", "test-operator"));

        // The same directory the HTTP handler resolves through, so a revocation cannot reach one
        // channel and miss the other.
        Assert.Null(await authenticator.AuthenticateAsync("store-sender", key, CancellationToken.None));
    }

    [Fact]
    public async Task The_smtp_submission_channel_still_requires_the_username_to_match_the_principal()
    {
        using var host = new TestHost();
        var key = await MintAsync(host, "store-sender", TestPrincipals.AcmeTenant, ["Send"]);

        var authenticator = host.Services.GetRequiredService<ISubmissionAuthenticator>();

        // A valid key presented under another principal's name authenticates nothing: the audit
        // record downstream would otherwise carry a name that never proved anything.
        Assert.Null(await authenticator.AuthenticateAsync("someone-else", key, CancellationToken.None));
    }

    [Fact]
    public async Task An_environment_principals_key_still_authenticates_over_smtp()
    {
        using var host = new TestHost();
        var authenticator = host.Services.GetRequiredService<ISubmissionAuthenticator>();

        var principal = await authenticator.AuthenticateAsync(
            TestPrincipals.AcmeSenderPrincipal,
            TestPrincipals.AcmeSenderKey,
            CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal(TestPrincipals.AcmeTenant, principal!.TenantId);
    }

    // ---------------------------------------------------------------------------------------------
    // The command surface
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Key_verbs_parse()
    {
        Assert.True(CliApplication.TryParse(
            ["key", "create", "--principal", "ops@acme", "--tenant", "acme",
             "--privileges", "Review,Administer", "--sender", "ops@acme.example", "--by", "operator"],
            out var command,
            out _));

        var create = Assert.IsType<KeyCreateCommand>(command);
        Assert.Equal("ops@acme", create.PrincipalId);
        Assert.Equal("acme", create.TenantId);
        Assert.Equal(["Review", "Administer"], create.Privileges);
        Assert.Equal(["ops@acme.example"], create.ApprovedSenderIdentities);
        Assert.Equal("operator", create.CreatedBy);

        Assert.True(CliApplication.TryParse(["key", "list"], out command, out _));
        Assert.IsType<KeyListCommand>(command);

        Assert.True(CliApplication.TryParse(
            ["key", "revoke", "--principal", "ops@acme", "--by", "operator"],
            out command,
            out _));

        Assert.Equal("ops@acme", Assert.IsType<KeyRevokeCommand>(command).PrincipalId);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("key", "frobnicate")]
    [InlineData("key", "create", "--tenant", "acme", "--privileges", "Review", "--by", "operator")]
    [InlineData("key", "create", "--principal", "ops", "--privileges", "Review", "--by", "operator")]
    [InlineData("key", "create", "--principal", "ops", "--tenant", "acme", "--by", "operator")]
    [InlineData("key", "create", "--principal", "ops", "--tenant", "acme", "--privileges", "Review")]
    [InlineData("key", "revoke", "--principal", "ops")]
    [InlineData("key", "revoke", "--by", "operator")]
    public void Key_verbs_refuse_incomplete_arguments(params string[] args)
    {
        Assert.False(CliApplication.TryParse(args, out var command, out var error));
        Assert.Null(command);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Repeatable_senders_are_all_recorded()
    {
        using var host = new TestHost();
        var key = await MintAsync(
            host,
            "store-sender",
            TestPrincipals.AcmeTenant,
            ["Send"],
            ["a@acme.example", "b@acme.example"]);

        var principal = await host.Services
            .GetRequiredService<ISubmissionAuthenticator>()
            .AuthenticateAsync("store-sender", key, CancellationToken.None);

        Assert.Equal(["a@acme.example", "b@acme.example"], principal!.ApprovedSenderIdentities);
    }

    [Fact]
    public async Task A_store_principals_identity_cannot_be_reused_across_tenants()
    {
        // Principal ids are globally unique in the store, unlike the configuration's, which is
        // scoped by tenant. A second mint under the same name in another tenant would resolve one
        // key to an ambiguous identity, so it is refused rather than allowed to create that state.
        using var host = new TestHost();
        await MintAsync(host, "shared-name", TestPrincipals.AcmeTenant, ["Assess"]);

        var (exitCode, output) = await RunAsync(
            host,
            new KeyCreateCommand("shared-name", TestPrincipals.GlobexTenant, ["Assess"], [], "operator"));

        Assert.Equal(2, exitCode);
        Assert.Contains("already has a live minted key", output, StringComparison.Ordinal);
    }
}

using Microsoft.Data.Sqlite;

namespace StyloMail.Persistence.Tests;

/// <summary>
/// Exercises inserts against the decision ledger and its dependants.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the previous tests only *created* the schema. SQLite accepts a foreign key
/// that references a non-unique column at <c>CREATE TABLE</c> time and only rejects it on the first
/// **write** — so making <c>decision_ledger</c>'s primary key composite silently invalidated two
/// foreign keys while every existing test stayed green. The failure would have surfaced later, in
/// production, as "foreign key mismatch" on the first disposition write.
/// </para>
/// <para>
/// A schema test that never writes is a test that cannot fail.
/// </para>
/// </remarks>
public sealed class DecisionLedgerSchemaTests : IDisposable
{
    private readonly string _dbPath;

    public DecisionLedgerSchemaTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"stylomail-ledger-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void A_disposition_can_reference_a_decision()
    {
        using var connection = Open();

        InsertDecision(connection, "tenant-a", "assessment-1");
        InsertDisposition(connection, "tenant-a", "assessment-1", "recipient-key-1");
    }

    [Fact]
    public void Feedback_can_reference_a_decision()
    {
        using var connection = Open();

        InsertDecision(connection, "tenant-a", "assessment-1");
        InsertFeedback(connection, "tenant-a", "assessment-1");
    }

    [Fact]
    public void A_disposition_cannot_reference_a_decision_in_another_tenant()
    {
        using var connection = Open();

        InsertDecision(connection, "tenant-a", "assessment-1");

        // Cross-tenant attribution is a foreign-key violation, not a query discipline.
        Assert.Throws<SqliteException>(() =>
            InsertDisposition(connection, "tenant-b", "assessment-1", "recipient-key-1"));
    }

    [Fact]
    public void Deleting_a_decision_cascades_to_its_dependants()
    {
        using var connection = Open();

        InsertDecision(connection, "tenant-a", "assessment-1");
        InsertDisposition(connection, "tenant-a", "assessment-1", "recipient-key-1");
        InsertFeedback(connection, "tenant-a", "assessment-1");

        Execute(connection, "DELETE FROM decision_ledger WHERE tenant_id = 'tenant-a' AND assessment_id = 'assessment-1';");

        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM recipient_disposition;"));
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM feedback;"));
    }

    [Fact]
    public void The_same_assessment_id_may_exist_in_two_tenants()
    {
        using var connection = Open();

        // The composite key makes ids tenant-scoped rather than globally unique, which is what
        // stops one tenant's identifier from addressing another tenant's row.
        InsertDecision(connection, "tenant-a", "assessment-1");
        InsertDecision(connection, "tenant-b", "assessment-1");

        Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM decision_ledger;"));
    }

    private SqliteConnection Open()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        var connection = factory.Open();
        SqliteSchema.EnsureCreated(connection);
        return connection;
    }

    private static void InsertDecision(SqliteConnection connection, string tenantId, string assessmentId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO decision_ledger (
                tenant_id, assessment_id, internal_message_id, correlation_id, direction,
                action, risk_index, policy_version, question_schema_version, preprocessing_version,
                coverage_json, reasons_json, evidence_json, risk_dimensions_json,
                cache_hit, cache_stale, assessed_at)
            VALUES ($tenant, $assessment, 'msg-1', 'corr-1', 0, 0, 0.1,
                    'policy/1', 'schema/1', 'prep/1', '{}', '[]', '[]', '[]', 0, 0, '2026-09-22T00:00:00Z');
            """;
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        cmd.Parameters.AddWithValue("$assessment", assessmentId);
        cmd.ExecuteNonQuery();
    }

    private static void InsertDisposition(
        SqliteConnection connection,
        string tenantId,
        string assessmentId,
        string recipientKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO recipient_disposition
                (assessment_id, tenant_id, recipient_key, recipient_risk, action, delivery_state)
            VALUES ($assessment, $tenant, $key, 0.1, 0, 0);
            """;
        cmd.Parameters.AddWithValue("$assessment", assessmentId);
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        cmd.Parameters.AddWithValue("$key", recipientKey);
        cmd.ExecuteNonQuery();
    }

    private static void InsertFeedback(SqliteConnection connection, string tenantId, string assessmentId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO feedback
                (feedback_id, tenant_id, assessment_id, scope, label, provenance, recorded_at)
            VALUES ($id, $tenant, $assessment, 'recipient', 'wanted', 'operator', '2026-09-22T00:00:00Z');
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        cmd.Parameters.AddWithValue("$assessment", assessmentId);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}

namespace StyloMail.Host.Storage;

/// <summary>Bound from the <c>StyloMail:Storage</c> configuration section.</summary>
public sealed class HostStorageOptions
{
    public const string SectionName = "StyloMail:Storage";

    /// <summary>Directory holding spooled message payloads.</summary>
    public string SpoolRoot { get; set; } = "data/spool";

    /// <summary>SQLite database holding the decision ledger, idempotency records and queue metadata.</summary>
    public string DatabasePath { get; set; } = "data/host.db";
}

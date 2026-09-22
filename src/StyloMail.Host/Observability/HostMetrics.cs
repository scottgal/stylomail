using System.Globalization;

namespace StyloMail.Host.Observability;

/// <summary>
/// The host's counters, exposed on <c>/metrics</c>.
/// </summary>
/// <remarks>
/// <b>Every counter is a named field with its own method; there is no general
/// <c>Increment(string name)</c>.</b> That is a deliberate constraint rather than a stylistic one.
/// A metrics surface is polled without credentials and is the easiest place in a system for
/// personal data to escape, and the usual mechanism is a label: "submissions by recipient" or
/// "decisions by sender" reads as harmless instrumentation and is a content leak with unbounded
/// cardinality attached. Here it is not possible to write one — the only thing that can be
/// counted is a thing this class already knows how to count.
///
/// <para>
/// Tenants are deliberately not a dimension anywhere. Per-tenant counts are operationally
/// interesting and belong in the authenticated operator surface, not on an open endpoint.
/// </para>
/// </remarks>
public sealed class HostMetrics
{
    private long _assessments;
    private long _submissionsAccepted;
    private long _submissionsRefused;
    private long _submissionsDuplicate;
    private long _storageUnavailable;
    private long _assessorUnavailable;
    private long _quarantineReleases;
    private long _feedback;
    private long _senderPauses;
    private long _senderResumes;

    public void AssessmentCompleted() => Interlocked.Increment(ref _assessments);

    public void AssessorUnavailable() => Interlocked.Increment(ref _assessorUnavailable);

    public void SubmissionAccepted() => Interlocked.Increment(ref _submissionsAccepted);

    public void SubmissionRefused() => Interlocked.Increment(ref _submissionsRefused);

    public void SubmissionDuplicate() => Interlocked.Increment(ref _submissionsDuplicate);

    public void StorageUnavailable() => Interlocked.Increment(ref _storageUnavailable);

    public void QuarantineReleased() => Interlocked.Increment(ref _quarantineReleases);

    public void FeedbackRecorded() => Interlocked.Increment(ref _feedback);

    public void SenderPaused() => Interlocked.Increment(ref _senderPauses);

    public void SenderResumed() => Interlocked.Increment(ref _senderResumes);

    /// <summary>Renders the counters in Prometheus text exposition format.</summary>
    public void Render(TextWriter writer)
    {
        Write(writer, "stylomail_assessments_total", "Assessments produced.", _assessments);
        Write(writer, "stylomail_submissions_accepted_total", "Submissions that durably transferred responsibility.", _submissionsAccepted);
        Write(writer, "stylomail_submissions_duplicate_total", "Submissions answered from an existing idempotent record.", _submissionsDuplicate);
        Write(writer, "stylomail_submissions_refused_total", "Submissions refused before acceptance.", _submissionsRefused);
        Write(writer, "stylomail_storage_unavailable_total", "Requests answered with a temporary storage failure.", _storageUnavailable);
        Write(writer, "stylomail_assessor_unavailable_total", "Requests refused because no assessor is configured.", _assessorUnavailable);
        Write(writer, "stylomail_quarantine_releases_total", "Quarantine releases applied by a reviewer.", _quarantineReleases);
        Write(writer, "stylomail_feedback_total", "Labels recorded through the feedback route.", _feedback);
        Write(writer, "stylomail_sender_pauses_total", "Sender pauses applied.", _senderPauses);
        Write(writer, "stylomail_sender_resumes_total", "Sender pauses lifted.", _senderResumes);
    }

    private static void Write(TextWriter writer, string name, string help, long value)
    {
        writer.Write("# HELP ");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(help);

        writer.Write("# TYPE ");
        writer.Write(name);
        writer.WriteLine(" counter");

        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(value.ToString(CultureInfo.InvariantCulture));
    }
}

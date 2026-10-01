namespace StyloMail.Conversation.Measure;

/// <summary>
/// Keeps the body of the last request the adapter sent.
/// </summary>
/// <remarks>
/// <para>
/// A pass-through, not a stub: it changes nothing about the call. Reading a
/// <c>JsonContent</c> body is repeatable, so recording it does not consume what is sent, and it
/// happens before the send, so a request that fails still leaves its payload behind.
/// </para>
/// <para>
/// It exists because a prompt token count is not a reproduction. Without the payload there is no
/// way to tell a shape difference from a model difference, and this lane would have to take its own
/// word for which it had measured.
/// </para>
/// </remarks>
internal sealed class RecordingHandler : DelegatingHandler
{
    public string? LastRequestBody { get; private set; }

    /// <summary>
    /// The body of the last response, kept for the server's own timing counters.
    /// </summary>
    /// <remarks>
    /// <b>The adapter surfaces token counts but not durations, and the split matters.</b> "46
    /// seconds per call" is a different problem depending on whether the time went into reading the
    /// window or into writing twelve answers, and the two imply opposite things about window size:
    /// a prefill cost grows with the window and argues for a shorter one, a generation cost does
    /// not. Ollama reports <c>prompt_eval_duration</c> and <c>eval_duration</c> separately and
    /// nothing in the pipeline carries them, so they are read here off the wire.
    /// </remarks>
    public string? LastResponseBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Reading the content buffers it, so the classifier's own read afterwards still sees the
        // body. This observes the call rather than consuming it.
        try
        {
            LastResponseBody = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A response whose body cannot be read is still a response; the recording is a
            // convenience and must never turn an answered call into a failed one.
            LastResponseBody = null;
        }

        return response;
    }
}

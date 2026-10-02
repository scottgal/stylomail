using System.Net.Http;

namespace StyloMail.Cascade.Tests;

/// <summary>
/// A fact that measures a corpus batch against a live local endpoint, and is skipped when there is
/// no batch named or no server to ask.
/// </summary>
/// <remarks>
/// <para>
/// <b>The suite stays runnable with nothing installed and no batch on disk.</b> A normal
/// <c>dotnet test</c> that failed because a model server is absent would be a suite people learn to
/// ignore, and this one holds a live call that costs minutes.
/// </para>
/// <para>
/// <b>The gate names the batch rather than defaulting to one.</b> A measurement is only readable
/// against the population it ran on, so a default batch would let a figure be published without its
/// population, which is the defect this fleet keeps finding in its own measurements.
/// </para>
/// <para>
/// <b>The reachability probe is the fleet's measured discrimination, not a ping.</b> A POST to
/// <c>/v1/systemone</c> is answered by a server that HAS the route with a JSON error body, and a
/// server without it answers the bare <c>404 page not found</c>. A probe that treated any response
/// as reachable would run the whole measurement against a route that does not exist, and every
/// dimension would come back unavailable for a reason that has nothing to do with the model.
/// </para>
/// </remarks>
public sealed class CascadeBatchFactAttribute : FactAttribute
{
    public const string BatchVariable = "CASCADE_BATCH";

    public const string OutVariable = "CASCADE_MEASUREMENT_OUT";

    public const string LocalEndpointVariable = "CASCADE_LOCAL_ENDPOINT";

    public const string PriorEndpointVariable = "CASCADE_PRIOR_ENDPOINT";

    /// <summary>The bare-404 body a server without the SystemOne route answers.</summary>
    private const string AbsentRouteBody = "404 page not found";

    public CascadeBatchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(BatchVariable) is not { Length: > 0 } batch
            || !Directory.Exists(batch)
            || Directory.GetFiles(batch, "*.eml").Length == 0)
        {
            Skip = $"Set {BatchVariable} to a generated corpus batch (tools/corpus/corpus.py generate) "
                + "and this measurement runs. It is skipped rather than failed because a figure needs "
                + "its population, and the population is the batch this names.";
            return;
        }

        var endpoint = LocalEndpoint();
        if (!ServesTheRoute(endpoint))
        {
            Skip = $"No server at {endpoint} serves POST /v1/systemone. Start the local model server, "
                + $"or set {LocalEndpointVariable} to its SystemOne endpoint, and this runs.";
        }
    }

    internal static string LocalEndpoint()
        => Environment.GetEnvironmentVariable(LocalEndpointVariable) is { Length: > 0 } configured
            ? configured
            : "http://127.0.0.1:11435/v1/systemone";

    internal static string? PriorEndpoint()
        => Environment.GetEnvironmentVariable(PriorEndpointVariable) is { Length: > 0 } configured
            ? configured
            : null;

    private static bool ServesTheRoute(string endpoint)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(
                    """{"model":"cascade-probe-no-such-model","state":"{}","questions":{}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };

            using var response = http.Send(request);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
            return !string.Equals(body, AbsentRouteBody, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

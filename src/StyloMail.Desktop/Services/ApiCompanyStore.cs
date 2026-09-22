using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Views;

namespace StyloMail.Desktop.Services;

/// <summary>
/// The company operations, over the Host HTTP API.
/// </summary>
/// <remarks>
/// The console's own rule applied to a dialog: it reaches the Host and nothing
/// else, so a headless deployment can do everything this screen does.
/// </remarks>
public sealed class ApiCompanyStore : ICompanyStore
{
    private readonly StyloMailApiClient _client;

    public ApiCompanyStore(StyloMailApiClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<IReadOnlyList<CompanyResponse>> ListAsync(CancellationToken cancellationToken = default)
        => (await _client.GetCompaniesAsync(cancellationToken).ConfigureAwait(false)).Companies;

    public Task<CompanyResponse> CreateAsync(CompanyRequest request, CancellationToken cancellationToken = default)
        => _client.CreateCompanyAsync(request, cancellationToken);

    public Task<CompanyResponse> SaveAsync(string companyId, CompanyRequest request, CancellationToken cancellationToken = default)
        => _client.SaveCompanyAsync(companyId, request, cancellationToken);
}

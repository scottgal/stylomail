using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>Coordinates one account mutation so a typed Host refusal remains an operator-visible result.</summary>
public sealed class ProxyAccountMutationViewModel : ObservableObject
{
    private bool _isPending;
    private string _statusLabel = string.Empty;

    /// <summary>True while an enable, disable, or credential revocation is in flight.</summary>
    public bool IsPending
    {
        get => _isPending;
        private set => Set(ref _isPending, value);
    }

    /// <summary>The safe status to render after the most recent mutation attempt.</summary>
    public string StatusLabel
    {
        get => _statusLabel;
        private set => Set(ref _statusLabel, value);
    }

    /// <summary>
    /// Runs one mutation. Host refusals and transport failures are normal operator outcomes, not
    /// exceptions allowed to escape an Avalonia <c>async void</c> event handler.
    /// </summary>
    public async Task<bool> ExecuteAsync(Func<Task> mutation, string successStatus)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        if (IsPending) return false;

        IsPending = true;
        try
        {
            await mutation();
            StatusLabel = successStatus;
            return true;
        }
        catch (StyloMailApiException error)
        {
            StatusLabel = DescribeFailure(error);
            return false;
        }
        finally
        {
            IsPending = false;
        }
    }

    public static string DescribeFailure(StyloMailApiException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return "Could not change proxy account state: "
            + (error.Detail ?? error.Code ?? error.Failure.ToString());
    }
}

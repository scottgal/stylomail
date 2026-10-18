using System.Diagnostics;
using System.Net;

namespace StyloMail.ProxyHarness.Reliability;

/// <summary>A nonempty UNIX table is not evidence that the TCP table was readable.</summary>
internal static class SocketTableControl
{
    internal static async Task<SocketTableResult> Capture(string directory, IPEndPoint local, IPEndPoint remote)
    {
        using var command = Process.Start(new ProcessStartInfo("/usr/sbin/netstat")
        {
            ArgumentList = { "-an" }, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        }) ?? throw new IOException("netstat did not start");
        var output = command.StandardOutput.ReadToEndAsync();
        var error = command.StandardError.ReadToEndAsync();
        try
        {
            await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            try { command.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new("UNUSABLE", 0, false, null, "TimeoutException");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "socket-table-control.stdout.txt"), output.Result);
        await File.WriteAllTextAsync(Path.Combine(directory, "socket-table-control.stderr.txt"), error.Result);
        var rows = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("tcp", StringComparison.OrdinalIgnoreCase)).ToArray();
        var pair = rows.Any(row =>
        {
            var fields = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Contains($"{local.Address}.{local.Port}", StringComparer.Ordinal)
                && fields.Contains($"{remote.Address}.{remote.Port}", StringComparer.Ordinal);
        });
        var usable = command.ExitCode == 0 && rows.Length > 0 && pair;
        return new(usable ? "AVAILABLE" : "UNUSABLE", rows.Length, pair, command.ExitCode, null);
    }
}

internal sealed record SocketTableResult(string Status, int TcpRows, bool NativeControlPairSeen, int? ExitCode, string? ErrorType);

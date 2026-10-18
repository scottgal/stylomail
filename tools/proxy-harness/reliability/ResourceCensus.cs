using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StyloMail.ProxyHarness.Reliability;

internal sealed record ResourceSample(int Processes, int Handles, int Sockets, int SessionSlots,
    int TrackedClients, int TrackedSessions, int BackendSessions, int StagingHandles,
    long ManagedBytes, long WorkingSetBytes, long TotalAllocatedBytes, int Gen0, int Gen1, int Gen2);

internal static class ResourceCensus
{
    // Darwin's sys/proc_info.h: PROC_PIDLISTFDS=1; proc_fdinfo={int32 fd,uint32 type};
    // PROX_FDTYPE_SOCKET=2. Missing/denied/truncated probes fail; they never become zero.
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int bufferSize);

    public static ResourceSample Capture(ProxyFixture fixture)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This census requires Darwin libproc.");
        using var process = Process.GetCurrentProcess();
        var processes = CensusExecutable(process.Id);
        var needed = proc_pidinfo(process.Id, 1, 0, IntPtr.Zero, 0);
        if (needed <= 0) throw new IOException("proc_pidinfo size probe failed");
        var size = checked(needed + 8192);
        var buffer = Marshal.AllocHGlobal(size);
        int handles, sockets = 0;
        try
        {
            var read = proc_pidinfo(process.Id, 1, 0, buffer, size);
            if (read <= 0 || read >= size || read % 8 != 0) throw new IOException("proc_pidinfo returned an unusable census");
            handles = read / 8;
            for (var offset = 0; offset < read; offset += 8)
                if (Marshal.ReadInt32(buffer, offset + 4) == 2) sockets++;
        }
        finally { Marshal.FreeHGlobal(buffer); }

        process.Refresh();
        return new(processes, handles, sockets, fixture.Limiter.ActiveSessions,
            fixture.TrackedClients, fixture.TrackedSessions, fixture.BackendSessions, fixture.Stager.ActiveCount,
            GC.GetTotalMemory(forceFullCollection: true), process.WorkingSet64, GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private static int CensusExecutable(int ownPid)
    {
        using var command = Process.Start(new ProcessStartInfo("/bin/ps")
        {
            ArgumentList = { "-axo", "pid=,comm=" }, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        }) ?? throw new IOException("ps did not start");
        var output = command.StandardOutput.ReadToEnd();
        if (!command.WaitForExit(5000)) { command.Kill(); throw new IOException("ps timed out"); }
        if (command.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) throw new IOException("ps census failed");
        var rows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid)) throw new IOException("Unparseable ps comm row");
            return (Pid: pid, Executable: Path.GetFileName(parts[1]));
        }).ToArray();
        var own = rows.SingleOrDefault(row => row.Pid == ownPid);
        if (string.IsNullOrEmpty(own.Executable)) throw new IOException("Own executable missing from ps comm census");
        return rows.Count(row => string.Equals(row.Executable, own.Executable, StringComparison.OrdinalIgnoreCase));
    }
}

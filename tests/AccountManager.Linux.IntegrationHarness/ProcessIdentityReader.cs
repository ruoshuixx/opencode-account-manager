using System.Globalization;

namespace AccountManager.Linux.IntegrationHarness;

/// <summary>Identity of a running process, read from Linux <c>/proc/&lt;pid&gt;</c>.</summary>
internal sealed record ProcessIdentity(
    int ProcessId,
    int ParentProcessId,
    int UserId,
    ulong StartTimeTicks,
    string? ExecutablePath,
    IReadOnlyList<string> Arguments);

/// <summary>
/// Minimal procfs reader for the native harness assertions. The start time together with the pid
/// identifies one process incarnation, so a reused pid is never mistaken for the original.
/// </summary>
internal static class ProcessIdentityReader
{
    public static ProcessIdentity? Read(int pid)
    {
        if (pid <= 0 || !OperatingSystem.IsLinux()) { return null; }

        try
        {
            var directory = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
            var stat = File.ReadAllText(directory + "/stat");
            // The command name may contain spaces and parentheses; the numeric fields follow the last ')'.
            var close = stat.LastIndexOf(')');
            if (close < 0) { return null; }
            var fields = stat[(close + 2)..].Split(' ');
            if (fields.Length < 20) { return null; }
            var parent = int.Parse(fields[1], CultureInfo.InvariantCulture);
            var start = ulong.Parse(fields[19], CultureInfo.InvariantCulture);

            var uid = -1;
            foreach (var line in File.ReadLines(directory + "/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) { continue; }
                var parts = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) { uid = int.Parse(parts[1], CultureInfo.InvariantCulture); }
                break;
            }
            if (uid < 0) { return null; }

            var cmdline = File.ReadAllText(directory + "/cmdline");
            var arguments = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);

            string? exe = null;
            try { exe = File.ResolveLinkTarget(directory + "/exe", false)?.FullName; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return new ProcessIdentity(pid, parent, uid, start, exe, arguments);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return null;
        }
    }
}

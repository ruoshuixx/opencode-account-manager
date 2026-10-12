using System.Diagnostics;
using System.Text;

namespace AccountManager.App.Services;

/// <summary>Raw outcome of one fixed busctl query; stderr is discarded, never retained.</summary>
internal readonly record struct TrayCommandResult(int ExitCode, string? StandardOutput, bool TimedOut = false);

/// <summary>Runs one fixed busctl argument vector. The default launches only the owned busctl child.</summary>
internal delegate Task<TrayCommandResult> TrayCommandRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// Queries the real StatusNotifier host through fixed, shell-free busctl calls within a single
/// three-second budget. A definite <c>b false</c> owner check or host-registered property is
/// Unavailable; any malformed, denied, missing or timed-out answer is Unknown — never an English
/// stderr guess and never a fabricated result. The watcher name merely existing is not a host.
/// </summary>
public sealed class LinuxTrayAvailability
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(3);

    private readonly TrayCommandRunner _runner;
    private readonly TimeSpan _budget;

    public LinuxTrayAvailability()
        : this(null, null)
    {
    }

    internal LinuxTrayAvailability(TrayCommandRunner? runner, TimeSpan? budget)
    {
        _runner = runner ?? SystemTrayCommandRunner.Instance.RunAsync;
        _budget = budget ?? DefaultBudget;
    }

    public async Task<TrayAvailability> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) { return TrayAvailability.Unknown; }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clock = Stopwatch.StartNew();
        deadline.CancelAfter(_budget);
        var token = deadline.Token;

        var owner = await RunAsync(BuildNameHasOwnerArguments(), token).ConfigureAwait(false);
        // Guard cancellation and the one monotonic total budget before parsing a possibly-completed result.
        if (token.IsCancellationRequested || clock.Elapsed >= _budget) { return TrayAvailability.Unknown; }

        var ownerState = ParseNameHasOwner(owner);
        if (ownerState != TrayAvailability.Available) { return ownerState; }

        var host = await RunAsync(BuildHostRegisteredArguments(), token).ConfigureAwait(false);
        if (token.IsCancellationRequested || clock.Elapsed >= _budget) { return TrayAvailability.Unknown; }

        return ParseHostRegistered(host);
    }

    private async Task<TrayCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (token.IsCancellationRequested) { return new TrayCommandResult(-1, null, TimedOut: true); }

        Task<TrayCommandResult> runnerTask;
        try
        {
            runnerTask = _runner(arguments, token);
        }
        catch
        {
            return new TrayCommandResult(-1, null);
        }

        try
        {
            // Never trust the runner to honor the token; WaitAsync bounds the wait regardless.
            var result = await runnerTask.WaitAsync(token).ConfigureAwait(false);

            // WaitAsync can hand back an already-completed result even though the runner cancelled the
            // caller; never parse a success after cancellation.
            if (token.IsCancellationRequested)
            {
                SystemTrayCommandRunner.Observe(runnerTask);
                return new TrayCommandResult(-1, null, TimedOut: true);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            SystemTrayCommandRunner.Observe(runnerTask);
            return new TrayCommandResult(-1, null, TimedOut: true);
        }
        catch
        {
            SystemTrayCommandRunner.Observe(runnerTask);
            return new TrayCommandResult(-1, null);
        }
    }

    internal static IReadOnlyList<string> BuildNameHasOwnerArguments() => new[]
    {
        "--user",
        "call",
        "org.freedesktop.DBus",
        "/org/freedesktop/DBus",
        "org.freedesktop.DBus",
        "NameHasOwner",
        "s",
        "org.kde.StatusNotifierWatcher",
    };

    internal static IReadOnlyList<string> BuildHostRegisteredArguments() => new[]
    {
        "--user",
        "get-property",
        "org.kde.StatusNotifierWatcher",
        "/StatusNotifierWatcher",
        "org.kde.StatusNotifierWatcher",
        "IsStatusNotifierHostRegistered",
    };

    internal static TrayAvailability ParseNameHasOwner(TrayCommandResult result) => ParseStrictBool(result);

    internal static TrayAvailability ParseHostRegistered(TrayCommandResult result) => ParseStrictBool(result);

    private static TrayAvailability ParseStrictBool(TrayCommandResult result)
    {
        if (result.TimedOut || result.ExitCode != 0 || result.StandardOutput is null)
        {
            return TrayAvailability.Unknown;
        }

        return result.StandardOutput.Trim() switch
        {
            "b true" => TrayAvailability.Available,
            "b false" => TrayAvailability.Unavailable,
            _ => TrayAvailability.Unknown,
        };
    }
}

/// <summary>
/// The production tray runner: one owned busctl child, fixed argv, a small overflow-aware stdout cap
/// and a discarded stderr, read concurrently with exit. It never calls ReadToEnd, never waits all
/// streams after a first failure, and never appends a drain delay.
/// </summary>
internal sealed class SystemTrayCommandRunner
{
    internal static readonly SystemTrayCommandRunner Instance = new();

    private const string Executable = "busctl";
    private const int MaxStdoutBytes = 256;
    private const int MaxStderrBytes = 256;

    internal async Task<TrayCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) { return new TrayCommandResult(-1, null); }

        // Pre-cancelled: do not start the owned child at all.
        if (cancellationToken.IsCancellationRequested) { return new TrayCommandResult(-1, null, TimedOut: true); }

        var startInfo = new ProcessStartInfo
        {
            FileName = Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) { startInfo.ArgumentList.Add(argument); }
        // Deterministic numeric/bool formatting regardless of the caller's locale.
        startInfo.Environment["LC_ALL"] = "C";

        Process? process = null;
        Task<byte[]>? stdoutTask = null;
        Task? stderrTask = null;
        Task? exitTask = null;
        var aborted = 0;

        // Owns the child's IO; a once-only abort kills the child and cancels every started read/exit.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readToken = readCts.Token;

        Action abortOwned = () =>
        {
            if (Interlocked.Exchange(ref aborted, 1) != 0) { return; }
            try { if (process is { } p && !p.HasExited) { p.Kill(entireProcessTree: false); } }
            catch { }
            try { readCts.Cancel(); } catch { }
        };

        try
        {
            process = Process.Start(startInfo);
            if (process is null) { return new TrayCommandResult(-1, null); }

            // Record the started tasks on the enclosing scope so a later getter/WaitForExitAsync setup
            // failure can still observe them.
            stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, MaxStdoutBytes, readToken);
            stderrTask = DrainAsync(process.StandardError.BaseStream, MaxStderrBytes, readToken);
            exitTask = process.WaitForExitAsync(readToken);

            return await CompleteAsync(stdoutTask, stderrTask, exitTask, () => process.ExitCode, abortOwned, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Abort(abortOwned);
            return new TrayCommandResult(-1, null, TimedOut: true);
        }
        catch
        {
            // Covers a stream getter / WaitForExitAsync setup exception after the owned child started.
            Abort(abortOwned);
            return new TrayCommandResult(-1, null);
        }
        finally
        {
            // Observe every started task (and the recorded aggregate) on every setup/error/finally path.
            if (stdoutTask is not null) { Observe(stdoutTask); }
            if (stderrTask is not null) { Observe(stderrTask); }
            if (exitTask is not null) { Observe(exitTask); }
            if (stdoutTask is not null && stderrTask is not null && exitTask is not null)
            {
                Observe(Task.WhenAll(stdoutTask, stderrTask, exitTask));
            }
            process?.Dispose();
        }
    }

    /// <summary>
    /// Reads up to <paramref name="cap"/> bytes and fails closed on overflow: the first byte beyond the
    /// cap throws <see cref="InvalidDataException"/> so a valid-looking prefix padded with later junk
    /// can never be silently truncated into a success.
    /// </summary>
    internal static async Task<byte[]> ReadCappedAsync(Stream stream, int cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[cap + 1];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read <= 0) { break; }

            total += read;
            if (total > cap)
            {
                throw new InvalidDataException("The child output exceeded its bounded cap.");
            }
        }
        return buffer[..total];
    }

    /// <summary>
    /// Monitors the recorded live tasks. A fault/cancel in any task aborts the owned child promptly,
    /// even while another task never ends. Success requires all three tasks plus a successful exit
    /// code; a successful exit is allowed to let the pipes drain inside the caller's original budget,
    /// and there is no appended drain window. Every failure path returns the fixed unknown, aborts at
    /// most once, and observes the individual and aggregate tasks.
    /// </summary>
    internal static async Task<TrayCommandResult> CompleteAsync(
        Task<byte[]> stdout,
        Task stderr,
        Task exit,
        Func<int> exitCode,
        Action abortOwned,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(exitCode);
        ArgumentNullException.ThrowIfNull(abortOwned);

        var aborted = 0;
        void AbortOnce()
        {
            if (Interlocked.Exchange(ref aborted, 1) == 0)
            {
                try { abortOwned(); } catch { }
            }
        }

        var all = Task.WhenAll(stdout, stderr, exit);
        try
        {
            if (ct.IsCancellationRequested)
            {
                AbortOnce();
                return new TrayCommandResult(-1, null, TimedOut: true);
            }

            var outstanding = new List<Task> { stdout, stderr, exit };
            var cancellation = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => cancellation.TrySetResult(null));

            while (outstanding.Count > 0)
            {
                var waiters = new List<Task>(outstanding.Count + 1);
                waiters.AddRange(outstanding);
                waiters.Add(cancellation.Task);

                var completed = await Task.WhenAny(waiters).ConfigureAwait(false);

                if (completed == cancellation.Task)
                {
                    AbortOnce();
                    return new TrayCommandResult(-1, null, TimedOut: true);
                }

                outstanding.Remove(completed);

                if (completed.IsFaulted || completed.IsCanceled)
                {
                    AbortOnce();
                    return new TrayCommandResult(-1, null);
                }

                if (ReferenceEquals(completed, exit))
                {
                    // The exit is known: evaluate the code immediately, even while a pipe never ends.
                    if (ct.IsCancellationRequested)
                    {
                        AbortOnce();
                        return new TrayCommandResult(-1, null, TimedOut: true);
                    }

                    int code;
                    try
                    {
                        code = exitCode();
                    }
                    catch
                    {
                        AbortOnce();
                        return new TrayCommandResult(-1, null);
                    }

                    if (code != 0)
                    {
                        // A known nonzero exit is a fixed unknown failure, aborted once (no pipe drain).
                        AbortOnce();
                        return new TrayCommandResult(-1, null);
                    }

                    // Only a successful exit may let the remaining valid pipes drain, still inside the
                    // caller's original budget/ct; no appended drain window and no fresh budget.
                    while (outstanding.Count > 0)
                    {
                        var pipeWaiters = new List<Task>(outstanding.Count + 1);
                        pipeWaiters.AddRange(outstanding);
                        pipeWaiters.Add(cancellation.Task);

                        var drained = await Task.WhenAny(pipeWaiters).ConfigureAwait(false);
                        if (drained == cancellation.Task)
                        {
                            AbortOnce();
                            return new TrayCommandResult(-1, null, TimedOut: true);
                        }

                        outstanding.Remove(drained);

                        if (drained.IsFaulted || drained.IsCanceled)
                        {
                            AbortOnce();
                            return new TrayCommandResult(-1, null);
                        }
                    }

                    // Final re-check: the caller must not have cancelled during the drain.
                    if (ct.IsCancellationRequested)
                    {
                        AbortOnce();
                        return new TrayCommandResult(-1, null, TimedOut: true);
                    }

                    return new TrayCommandResult(0, Encoding.UTF8.GetString(await stdout.ConfigureAwait(false)));
                }
            }

            // Unreachable in practice (the exit always settles the loop), kept total for the compiler.
            AbortOnce();
            return new TrayCommandResult(-1, null);
        }
        finally
        {
            // Observe every started task and the aggregate so no fault is left unobserved.
            Observe(stdout);
            Observe(stderr);
            Observe(exit);
            Observe(all);
        }
    }

    /// <summary>Attaches a no-op observation to a task so a fault is never left unobserved.</summary>
    internal static void Observe(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception; // mark the fault observed
            return;
        }
        if (task.IsCompleted) { return; }

        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task DrainAsync(Stream stream, int cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[cap];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, cap), cancellationToken).ConfigureAwait(false);
            if (read <= 0) { return; }
        }
    }

    private static void Abort(Action? abortOwned)
    {
        try { abortOwned?.Invoke(); } catch { }
    }
}

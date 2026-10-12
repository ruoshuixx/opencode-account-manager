using System.Text;

namespace AccountManager.App.Services;

internal sealed record LinuxProbeOutput(string? StandardOutput, string? StandardError);

/// <summary>The narrow owned-process boundary used by the default probe runner and synthetic tests.</summary>
internal interface ILinuxUpdateProbeProcess : IDisposable
{
    Task<LinuxProbeOutput> StartReadingOutputAsync(int maximumCharacters, CancellationToken cancellationToken);
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    Task<bool> StopAndConfirmExitAsync();
}

/// <summary>Production adapter for the two redirected probe streams.</summary>
internal sealed class LinuxUpdateProbeOutputReader(
    Func<TextReader> standardOutput,
    Func<TextReader> standardError) : IDisposable
{
    private const int ReaderCleanupTimeoutMilliseconds = 10_000;
    private readonly object _sync = new();
    private TextReader? _standardOutputReader;
    private TextReader? _standardErrorReader;
    private Task<string?>? _standardOutputTask;
    private Task<string?>? _standardErrorTask;
    private CancellationTokenSource? _readCancellation;
    private bool _standardOutputDisposed;
    private bool _standardErrorDisposed;
    private bool _readStarted;
    private bool _disposed;

    internal bool ReaderTasksObserved { get; private set; }

    public async Task<LinuxProbeOutput> ReadAsync(int maximumCharacters, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        CancellationTokenSource readCancellation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readStarted) { throw new InvalidOperationException("Probe output readers can only be started once."); }
            _readStarted = true;
            readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readCancellation = readCancellation;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stdoutReader = standardOutput() ?? throw new InvalidOperationException("The probe stdout reader was unavailable.");
            lock (_sync) { _standardOutputReader = stdoutReader; }
            var stdout = ReadBoundedOutputAsync(stdoutReader, maximumCharacters, readCancellation.Token);
            lock (_sync) { _standardOutputTask = stdout; }

            var stderrReader = standardError() ?? throw new InvalidOperationException("The probe stderr reader was unavailable.");
            lock (_sync) { _standardErrorReader = stderrReader; }
            var stderr = ReadBoundedOutputAsync(stderrReader, maximumCharacters, readCancellation.Token, rejectNonEmpty: true);
            lock (_sync) { _standardErrorTask = stderr; }

            var first = await Task.WhenAny(stdout, stderr).ConfigureAwait(false);
            var firstResult = await first.ConfigureAwait(false);
            if (firstResult is null)
            {
                await CancelDisposeAndObserveAsync(readCancellation, stdout, stderr).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return first == stdout
                    ? new LinuxProbeOutput(null, string.Empty)
                    : new LinuxProbeOutput(string.Empty, null);
            }

            var second = first == stdout ? stderr : stdout;
            var secondResult = await second.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ReaderTasksObserved = true;
            return first == stdout
                ? new LinuxProbeOutput(firstResult, secondResult)
                : new LinuxProbeOutput(secondResult, firstResult);
        }
        catch
        {
            Task<string?>? stdout;
            Task<string?>? stderr;
            lock (_sync)
            {
                stdout = _standardOutputTask;
                stderr = _standardErrorTask;
            }
            await CancelDisposeAndObserveAsync(readCancellation, stdout, stderr)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            DisposeReaders();
            lock (_sync)
            {
                if (ReferenceEquals(_readCancellation, readCancellation)) { _readCancellation = null; }
            }
            readCancellation.Dispose();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? readCancellation;
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            readCancellation = _readCancellation;
        }
        try { readCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        DisposeReaders();
    }

    private async Task CancelDisposeAndObserveAsync(
        CancellationTokenSource readCancellation,
        Task<string?>? stdout,
        Task<string?>? stderr)
    {
        try { readCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        DisposeReaders();
        var tasks = new[] { stdout, stderr }.Where(static task => task is not null).Cast<Task<string?>>().ToArray();
        if (tasks.Length == 0)
        {
            ReaderTasksObserved = true;
            return;
        }

        var all = Task.WhenAll(tasks);
        try
        {
            await all.WaitAsync(TimeSpan.FromMilliseconds(ReaderCleanupTimeoutMilliseconds)).ConfigureAwait(false);
            ReaderTasksObserved = true;
        }
        catch (TimeoutException) when (!all.IsCompleted)
        {
            ReaderTasksObserved = false;
            foreach (var task in tasks) { ObserveReader(task); }
        }
        catch
        {
            ReaderTasksObserved = all.IsCompleted;
        }
    }

    private void DisposeReaders()
    {
        TextReader? stdout = null;
        TextReader? stderr = null;
        lock (_sync)
        {
            if (!_standardOutputDisposed && _standardOutputReader is not null)
            {
                _standardOutputDisposed = true;
                stdout = _standardOutputReader;
            }
            if (!_standardErrorDisposed && _standardErrorReader is not null)
            {
                _standardErrorDisposed = true;
                stderr = _standardErrorReader;
            }
        }
        DisposeReader(stdout);
        DisposeReader(stderr);
    }

    private static void DisposeReader(TextReader? reader)
    {
        if (reader is null) { return; }
        try { reader.Dispose(); }
        catch { /* Preserve the operation failure; both redirected readers still get disposal. */ }
    }

    private static async Task<string?> ReadBoundedOutputAsync(
        TextReader reader,
        int maximum,
        CancellationToken cancellationToken,
        bool rejectNonEmpty = false)
    {
        var buffer = new char[512];
        var output = new StringBuilder(Math.Min(maximum, 512));
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) { return output.ToString(); }
            if (rejectNonEmpty || output.Length + read > maximum) { return null; }
            output.Append(buffer, 0, read);
        }
    }

    private static void ObserveReader(Task<string?> reader)
        => _ = reader.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

internal sealed class LinuxProbeExecutionException : Exception
{
    internal LinuxProbeExecutionException(Exception innerException, bool processExitConfirmed)
        : base("The candidate version probe did not complete safely.", innerException)
        => ProcessExitConfirmed = processExitConfirmed;

    internal bool ProcessExitConfirmed { get; }
}

internal sealed class LinuxUpdatePreparationException : Exception
{
    internal const string Guidance = "Linux 更新依赖准备失败；程序未替换。请从发布页手动下载并替换程序。";

    internal LinuxUpdatePreparationException(Exception innerException)
        : base(Guidance, innerException) { }
}

/// <summary>Production-owned probe orchestration over a replaceable process/stream boundary.</summary>
internal static class LinuxUpdateProbeRunner
{
    private const int CleanupTimeoutMilliseconds = 10_000;

    internal static async Task<string> RunAsync(
        Func<CancellationToken, ILinuxUpdateProbeProcess> startOwnedProcess,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        Func<long>? getTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(startOwnedProcess);
        if (timeoutMilliseconds <= 0) { throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds)); }
        getTimestamp ??= static () => Environment.TickCount64;
        var deadline = checked(getTimestamp() + timeoutMilliseconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter((int)Math.Clamp(deadline - getTimestamp(), 1, int.MaxValue));
        ILinuxUpdateProbeProcess? process = null;
        Task<LinuxProbeOutput>? outputTask = null;
        Task<int>? exitTask = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureBeforeDeadline(deadline, getTimestamp);
            process = startOwnedProcess(timeout.Token)
                ?? throw new InvalidOperationException("The candidate probe process was not created.");
            EnsureBeforeDeadline(deadline, getTimestamp);
            cancellationToken.ThrowIfCancellationRequested();
            timeout.Token.ThrowIfCancellationRequested();

            // Stream setup, reads, wait, and output validation all share the owned-process cleanup
            // boundary. The process adapter drains stdout and stderr concurrently.
            outputTask = process.StartReadingOutputAsync(LinuxUpdateProtocol.MaxMessageBytes, timeout.Token);
            exitTask = process.WaitForExitAsync(timeout.Token);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
            var first = await Task.WhenAny(outputTask, exitTask, cancellationTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            timeout.Token.ThrowIfCancellationRequested();
            if (first == cancellationTask) { timeout.Token.ThrowIfCancellationRequested(); }

            LinuxProbeOutput output;
            int exitCode;
            if (first == outputTask)
            {
                output = await AwaitBeforeDeadlineAsync(outputTask, deadline, getTimestamp, timeout.Token).ConfigureAwait(false);
                ValidateOutput(output);
                exitCode = await AwaitBeforeDeadlineAsync(exitTask, deadline, getTimestamp, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                exitCode = await AwaitBeforeDeadlineAsync(exitTask, deadline, getTimestamp, timeout.Token).ConfigureAwait(false);
                output = await AwaitBeforeDeadlineAsync(outputTask, deadline, getTimestamp, timeout.Token).ConfigureAwait(false);
                ValidateOutput(output);
            }

            cancellationToken.ThrowIfCancellationRequested();
            timeout.Token.ThrowIfCancellationRequested();
            EnsureBeforeDeadline(deadline, getTimestamp);
            if (exitCode != 0) { throw new InvalidDataException("The candidate probe exited unsuccessfully."); }
            cancellationToken.ThrowIfCancellationRequested();
            timeout.Token.ThrowIfCancellationRequested();
            timeout.Cancel(); // Completes the cancellation observer created for the shared budget.
            return output.StandardOutput!;
        }
        catch (Exception ex)
        {
            if (process is null) { throw; }

            timeout.Cancel();
            var exitConfirmed = false;
            try { exitConfirmed = await process.StopAndConfirmExitAsync().ConfigureAwait(false); }
            catch { exitConfirmed = false; }
            var outputObserved = await ObserveAsync(outputTask).ConfigureAwait(false);
            var exitObserved = await ObserveAsync(exitTask).ConfigureAwait(false);
            if (!outputObserved || !exitObserved) { exitConfirmed = false; }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested && exitConfirmed)
            {
                throw new OperationCanceledException("The candidate probe was cancelled.", ex, cancellationToken);
            }
            throw new LinuxProbeExecutionException(ex, exitConfirmed);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void ValidateOutput(LinuxProbeOutput output)
    {
        if (output.StandardOutput is null || output.StandardError is null || output.StandardError.Length != 0)
        {
            throw new InvalidDataException("The staged update probe failed its bounded output check.");
        }
    }

    private static async Task<T> AwaitBeforeDeadlineAsync<T>(Task<T> task, long deadline, Func<long> getTimestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBeforeDeadline(deadline, getTimestamp);
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        _ = await Task.WhenAny(task, cancelled).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBeforeDeadline(deadline, getTimestamp);
        return result;
    }

    private static async Task<bool> ObserveAsync(Task? task)
    {
        if (task is null) { return true; }
        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(CleanupTimeoutMilliseconds)).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }
        catch
        {
            return true; // Awaiting observes stream-read faults and cancellation.
        }
    }

    private static void EnsureBeforeDeadline(long deadline, Func<long> getTimestamp)
    {
        if (getTimestamp() >= deadline) { throw new TimeoutException("Candidate probe deadline expired."); }
    }
}

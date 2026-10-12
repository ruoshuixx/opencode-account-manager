using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Runtime.Versioning;
using AccountManager.App.Services;
using AccountManager.Core.OpenCode;

namespace AccountManager.Linux.IntegrationHarness;

[SupportedOSPlatform("linux")]
internal static class LinuxUpdateScenarios
{
    private const string ChildResultFileVariable = "OCAM_LINUX_HARNESS_CHILD_RESULT_FILE";
    private const string ChildStartFileVariable = "OCAM_LINUX_HARNESS_CHILD_START_FILE";

    internal static async Task<int> RunSuccessfulParentAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 4);
        HarnessSupport.RequireLinuxX64();
        var candidate = Path.GetFullPath(args[1]);
        var expected = ParseVersion(args[2]);
        var markers = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(markers);

        await SelfUpdater.PrepareAndLaunchAsync(candidate, expected).ConfigureAwait(false);
        HarnessSupport.WriteAtomic(Path.Combine(markers, "parent-handover-returned"), "production-parent-returned-after-authenticated-readiness\n");
        return 0;
    }

    internal static async Task<int> RunReadinessTimeoutParentAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 4);
        HarnessSupport.RequireLinuxX64();
        var candidate = Path.GetFullPath(args[1]);
        var expected = ParseVersion(args[2]);
        var resultPath = Path.GetFullPath(args[3]);
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Harness process path is unavailable.");
        var beforeHash = HarnessSupport.Sha256(current);

        var elapsed = Stopwatch.StartNew();
        Exception? deadlineFailure = null;
        try
        {
            await SelfUpdater.PrepareAndLaunchAsync(candidate, expected).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            deadlineFailure = ex;
        }
        catch (InvalidOperationException ex)
        {
            deadlineFailure = ex;
        }
        elapsed.Stop();

        HarnessSupport.Assert(deadlineFailure is not null, "The real updater orchestration did not fail while waiting for a managed-entry child.");
        HarnessSupport.Assert(elapsed.Elapsed >= TimeSpan.FromSeconds(18) && elapsed.Elapsed <= TimeSpan.FromSeconds(35),
            "The readiness failure did not span the production bounded-entry deadline.");
        var resultMarker = Environment.GetEnvironmentVariable(ChildResultFileVariable);
        HarnessSupport.Assert(!string.IsNullOrWhiteSpace(resultMarker) && File.Exists(resultMarker),
            "The readiness timeout was not caused by the explicit harness-owned pre-entry child control.");
        var childFields = File.ReadAllText(resultMarker!).TrimEnd('\r', '\n').Split(':');
        HarnessSupport.Assert(childFields.Length == 4 && childFields[0] == "held-before-entry",
            "The delayed child did not record its exact Linux process identity.");
        HarnessSupport.Assert(int.TryParse(childFields[1], out var childPid) && childPid > 0,
            "The delayed child PID was invalid.");
        HarnessSupport.Assert(ulong.TryParse(childFields[2], out var childStartTicks) && childStartTicks > 0,
            "The delayed child start time was invalid.");
        HarnessSupport.Assert(long.TryParse(childFields[3], out var childStartedAt) && childStartedAt >= 0,
            "The delayed child monotonic start marker was invalid.");
        var survivingIdentity = ProcessIdentityReader.Read(childPid);
        HarnessSupport.Assert(survivingIdentity is null || survivingIdentity.StartTimeTicks != childStartTicks,
            "The production readiness-timeout path left its exact controlled child alive.");
        HarnessSupport.Assert(HarnessSupport.Sha256(current) == beforeHash, "The previous bundle was not restored after readiness timeout.");
        HarnessSupport.Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(current)!, "OpenCodeAccountManager.old-*", SearchOption.TopDirectoryOnly).Any(),
            "A compensated readiness timeout left an attributable old-bundle backup behind.");
        HarnessSupport.Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(current)!, ".OpenCodeAccountManager.new-*", SearchOption.TopDirectoryOnly).Any(),
            "A compensated readiness timeout left an attributable staged bundle behind.");
        HarnessSupport.WriteAtomic(resultPath, $"readiness-timeout-restored-original:{elapsed.ElapsedMilliseconds}\n");
        return 0;
    }

    internal static async Task<int> RunParentExitTimeoutParentAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 4);
        HarnessSupport.RequireLinuxX64();
        var candidate = Path.GetFullPath(args[1]);
        var expected = ParseVersion(args[2]);
        var markers = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(markers);

        await SelfUpdater.PrepareAndLaunchAsync(candidate, expected).ConfigureAwait(false);
        var childStart = Environment.GetEnvironmentVariable(ChildStartFileVariable);
        HarnessSupport.Assert(!string.IsNullOrWhiteSpace(childStart), "The updater child start marker was not configured.");
        await HarnessSupport.WaitForFileAsync(childStart!, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        HarnessSupport.WriteAtomic(Path.Combine(markers, "parent-handover-returned"), "parent-kept-alive-past-production-exit-budget\n");

        // The production child enforces the actual 30-second parent-exit bound. Holding this exact
        // parent alive for 35 seconds exercises that wait without altering production constants.
        var parentHoldStart = Environment.TickCount64;
        HarnessSupport.WriteAtomic(Path.Combine(markers, "parent-hold-start"), parentHoldStart.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        await Task.Delay(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
        HarnessSupport.WriteAtomic(Path.Combine(markers, "parent-hold-end"), Environment.TickCount64.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        return 0;
    }

    internal static async Task<int> RunReadOnlyRefusalAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 4);
        HarnessSupport.RequireLinuxX64();
        var candidate = Path.GetFullPath(args[1]);
        var expected = ParseVersion(args[2]);
        var resultPath = Path.GetFullPath(args[3]);
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Harness process path is unavailable.");
        var beforeHash = HarnessSupport.Sha256(current);
        var refused = false;
        try
        {
            await SelfUpdater.PrepareAndLaunchAsync(candidate, expected).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        HarnessSupport.Assert(refused, "The production updater did not refuse this read-only installation.");
        HarnessSupport.Assert(HarnessSupport.Sha256(current) == beforeHash, "Read-only refusal changed the installed bundle.");
        HarnessSupport.WriteAtomic(resultPath, "read-only-install-refused\n");
        return 0;
    }

    internal static async Task RunCandidateValidationScenarioAsync(string candidate)
    {
        HarnessSupport.RequireLinuxX64();
        AssertExecutionDeniedClassification();
        var expectedCurrent = AppUpdateInfo.Version;
        var probeCalls = 0;
        var mismatchRejected = false;
        try
        {
            await LinuxSelfUpdater.ValidateCandidateAsync(
                candidate,
                expectedCurrent,
                (path, cancellationToken) =>
                {
                    probeCalls++;
                    return RunProductionProbeRunnerAsync(path, cancellationToken);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            mismatchRejected = true;
        }
        HarnessSupport.Assert(mismatchRejected && probeCalls == 1,
            "The actual second bundle probe did not expose and reject its numeric version mismatch.");
        Console.Out.WriteLine("UPDATE_SCENARIO name=probe-version-mismatch status=PASS boundary=production-candidate-validator candidate=actual-second-bundle");

        var scenario = HarnessSupport.NewPrivateDirectory(Path.GetDirectoryName(candidate)!, "nonexecutable-candidate");
        try
        {
            var nonExecutable = Path.Combine(scenario, "OpenCodeAccountManager");
            File.Copy(candidate, nonExecutable);
            File.SetUnixFileMode(nonExecutable, HarnessSupport.PrivateFileMode);
            var nonExecutableProbeCalls = 0;
            var successfullyStartedChildren = 0;
            Exception? executionDenied = null;
            try
            {
                await LinuxSelfUpdater.ValidateCandidateAsync(
                    nonExecutable,
                    AppUpdateInfo.Version,
                    (path, cancellationToken) =>
                    {
                        nonExecutableProbeCalls++;
                        return RunProductionProbeRunnerAsync(path, cancellationToken,
                            () => successfullyStartedChildren++);
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExecutionDeniedAtProbeStart(ex))
            {
                executionDenied = ex;
            }
            HarnessSupport.Assert(executionDenied is not null && nonExecutableProbeCalls == 1 && successfullyStartedChildren == 0,
                "The actual production probe runner did not fail at the non-executable process-start boundary.");
            Console.Out.WriteLine("UPDATE_SCENARIO name=nonexecutable-candidate status=PASS boundary=production-probe-runner-start-denied child_started=no");
        }
        finally { HarnessSupport.DeleteOwnedDirectory(scenario); }
    }

    internal static Task RunConcurrentTargetScenarioAsync(string root, string candidate)
    {
        HarnessSupport.RequireLinuxX64();
        var scenario = HarnessSupport.NewPrivateDirectory(root, "target-concurrent-change");
        try
        {
            var current = Path.Combine(scenario, "OpenCodeAccountManager");
            var source = Path.Combine(scenario, "candidate-bundle");
            var staged = Path.Combine(scenario, ".OpenCodeAccountManager.new-" + Guid.NewGuid().ToString("N"));
            var old = Path.Combine(scenario, "OpenCodeAccountManager.old-" + Guid.NewGuid().ToString("N"));
            var originalHash = HarnessSupport.Sha256(Environment.ProcessPath ?? throw new InvalidOperationException("Harness process path is unavailable."));
            File.Copy(Environment.ProcessPath!, current);
            File.Copy(candidate, source);
            File.SetUnixFileMode(current, File.GetUnixFileMode(Environment.ProcessPath!));
            File.SetUnixFileMode(source, File.GetUnixFileMode(candidate));

            var swap = new LinuxUpdateSwap(source, current, staged, old);
            swap.Stage();
            swap.Execute();
            const string replacement = "controlled-concurrent-target-replacement";
            File.WriteAllText(current, replacement, new UTF8Encoding(false));
            var recovery = swap.Rollback(childExitConfirmed: true);

            HarnessSupport.Assert(recovery is not null, "The production rollback accepted a concurrently changed current target.");
            HarnessSupport.Assert(File.ReadAllText(current) == replacement, "Rollback overwrote the unrelated concurrent target.");
            HarnessSupport.Assert(File.Exists(old) && HarnessSupport.Sha256(old) == originalHash,
                "Rollback did not retain the attributable old bundle for manual recovery.");
            Console.Out.WriteLine("UPDATE_SCENARIO name=target-concurrent-modification status=PASS boundary=production-swap-mechanism-only orchestration=not-tested");
            return Task.CompletedTask;
        }
        finally { HarnessSupport.DeleteOwnedDirectory(scenario); }
    }

    internal static async Task RunUnconfirmedChildCompensationScenarioAsync(string root, string candidate)
    {
        HarnessSupport.RequireLinuxX64();
        var scenario = HarnessSupport.NewPrivateDirectory(root, "unconfirmed-child");
        try
        {
            var current = Path.Combine(scenario, "OpenCodeAccountManager");
            var source = Path.Combine(scenario, "candidate-bundle");
            var staged = Path.Combine(scenario, ".OpenCodeAccountManager.new-" + Guid.NewGuid().ToString("N"));
            var old = Path.Combine(scenario, "OpenCodeAccountManager.old-" + Guid.NewGuid().ToString("N"));
            var originalHash = HarnessSupport.Sha256(Environment.ProcessPath ?? throw new InvalidOperationException("Harness process path is unavailable."));
            var replacementHash = HarnessSupport.Sha256(candidate);
            File.Copy(Environment.ProcessPath!, current);
            File.Copy(candidate, source);
            File.SetUnixFileMode(current, File.GetUnixFileMode(Environment.ProcessPath!));
            File.SetUnixFileMode(source, File.GetUnixFileMode(candidate));

            var swap = new LinuxUpdateSwap(source, current, staged, old);
            var refusedCompensation = false;
            try
            {
                await LinuxSelfUpdater.RunSwapAndAwaitReadyAsync(
                    swap,
                    () => Task.FromException(new InvalidOperationException("controlled harness launch boundary")),
                    childExitConfirmed: () => false).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                refusedCompensation = true;
            }

            HarnessSupport.Assert(refusedCompensation, "The production coordinator did not refuse compensation when child exit was unconfirmed.");
            HarnessSupport.Assert(File.Exists(current) && HarnessSupport.Sha256(current) == replacementHash,
                "Unconfirmed-child compensation removed or replaced the current target.");
            HarnessSupport.Assert(File.Exists(old) && HarnessSupport.Sha256(old) == originalHash,
                "Unconfirmed-child compensation did not preserve the attributable old bundle.");
            Console.Out.WriteLine("UPDATE_SCENARIO name=unconfirmed-child-compensation status=PASS boundary=production-coordinator-mechanism-only launch=controlled-exception");
        }
        finally { HarnessSupport.DeleteOwnedDirectory(scenario); }
    }

    private static Version ParseVersion(string value)
    {
        if (!UpdateProbe.TryParseThreePartVersion(value, out var version))
        {
            throw new ArgumentException("A numeric three-part candidate version is required.");
        }
        return version;
    }

    private static bool IsExecutionDeniedAtProbeStart(Exception exception)
        => IsAccessDenied(exception) ||
            exception is LinuxProbeExecutionException { InnerException: { } inner } && IsAccessDenied(inner);

    private static bool IsAccessDenied(Exception exception)
        => exception is UnauthorizedAccessException ||
            exception is Win32Exception { NativeErrorCode: 13 };

    private static void AssertExecutionDeniedClassification()
    {
        HarnessSupport.Assert(IsExecutionDeniedAtProbeStart(new Win32Exception(13)),
            "Linux EACCES did not classify as an execution-denied start failure.");
        HarnessSupport.Assert(IsExecutionDeniedAtProbeStart(new UnauthorizedAccessException()),
            "The supported direct unauthorized-access representation was rejected.");
        HarnessSupport.Assert(IsExecutionDeniedAtProbeStart(
                new LinuxProbeExecutionException(new Win32Exception(13), processExitConfirmed: false)),
            "The supported wrapped Linux EACCES representation was rejected.");
        HarnessSupport.Assert(!IsExecutionDeniedAtProbeStart(new Win32Exception(2)) &&
                              !IsExecutionDeniedAtProbeStart(new Win32Exception(8)) &&
                              !IsExecutionDeniedAtProbeStart(
                                  new LinuxProbeExecutionException(new Win32Exception(2), processExitConfirmed: false)) &&
                              !IsExecutionDeniedAtProbeStart(
                                  new LinuxProbeExecutionException(new Win32Exception(8), processExitConfirmed: false)),
            "ENOENT or ENOEXEC incorrectly satisfied the execution-denied predicate.");
    }

    private static async Task<string> RunProductionProbeRunnerAsync(
        string candidate,
        CancellationToken cancellationToken,
        Action? onProcessStarted = null)
        => await LinuxUpdateProbeRunner.RunAsync(
            _ =>
            {
                var process = ProbeProcess.Start(candidate);
                onProcessStarted?.Invoke();
                return process;
            },
            timeoutMilliseconds: 5000,
            cancellationToken).ConfigureAwait(false);

    private sealed class ProbeProcess : ILinuxUpdateProbeProcess
    {
        private readonly Process _process;
        private readonly LinuxUpdateProbeOutputReader _outputReader;

        private ProbeProcess(Process process)
        {
            _process = process;
            _outputReader = new LinuxUpdateProbeOutputReader(
                () => _process.StandardOutput,
                () => _process.StandardError);
        }

        internal static ILinuxUpdateProbeProcess Start(string candidate)
        {
            var startInfo = HarnessSupport.CreateStartInfo(candidate, new[] { UpdateProbe.Flag }, redirectOutput: true);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the actual candidate probe process.");
            return new ProbeProcess(process);
        }

        public Task<LinuxProbeOutput> StartReadingOutputAsync(int maximumCharacters, CancellationToken cancellationToken)
            => _outputReader.ReadAsync(maximumCharacters, cancellationToken);

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public async Task<bool> StopAndConfirmExitAsync()
        {
            try
            {
                if (!_process.HasExited) { _process.Kill(entireProcessTree: false); }
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return _process.HasExited;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            _outputReader.Dispose();
            _process.Dispose();
        }
    }
}

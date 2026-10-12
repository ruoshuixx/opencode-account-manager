using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.App.Services;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Quota;
using AccountManager.Core.Security;

namespace AccountManager.Linux.IntegrationHarness;

[SupportedOSPlatform("linux")]
internal static class LinuxNativeScenarios
{
    private const string SyntheticCurlToken = "synthetic-harness-token-not-a-credential";
    private const string SyntheticAccount = "synthetic-harness-account";

    internal static async Task<int> RunAllAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 3);
        HarnessSupport.RequireLinuxX64();
        var root = Path.GetFullPath(args[1]);
        var candidate = Path.GetFullPath(args[2]);
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            File.SetUnixFileMode(root, HarnessSupport.PrivateDirectoryMode);
        }
        var rootInfo = new DirectoryInfo(root);
        HarnessSupport.Assert(rootInfo.LinkTarget is null, "The native harness root must not be a link.");
        HarnessSupport.Assert(File.GetUnixFileMode(root) == HarnessSupport.PrivateDirectoryMode,
            "The native harness root must already be owned with exact 0700 permissions.");
        HarnessSupport.Assert(LinuxUpdateSwap.GetLinuxOwnerUid(root) == LinuxSelfUpdater.GetEffectiveUid(),
            "The native harness root must belong to the current effective user.");
        HarnessSupport.Assert(!Directory.EnumerateFileSystemEntries(root).Any(),
            "The native harness root must be a new empty directory.");
        HarnessSupport.Assert(File.Exists(candidate) && !Directory.Exists(candidate), "The second harness bundle is missing.");

        var runner = new ScenarioRunner();
        await runner.RunAsync("unix-secret-store-cross-process", root, () => RunSecretStoreScenarioAsync(root)).ConfigureAwait(false);
        await runner.RunAsync("single-instance-race-activation-abrupt-restart", root, () => RunSingleInstanceScenarioAsync(root)).ConfigureAwait(false);
        await runner.RunAsync("curl-local-fixture-stdin", root, () => RunCurlSuccessScenarioAsync()).ConfigureAwait(false);
        await runner.RunAsync("curl-output-bound", root, () => RunCurlOutputBoundScenarioAsync()).ConfigureAwait(false);
        await runner.RunAsync("curl-owned-process-cancellation", root, () => RunCurlCancellationScenarioAsync()).ConfigureAwait(false);
        await runner.RunAsync("updater-probe-mismatch-and-nonexecutable", root, () => LinuxUpdateScenarios.RunCandidateValidationScenarioAsync(candidate)).ConfigureAwait(false);
        await runner.RunAsync("updater-target-concurrent-modification", root, () => LinuxUpdateScenarios.RunConcurrentTargetScenarioAsync(root, candidate)).ConfigureAwait(false);
        await runner.RunAsync("updater-unconfirmed-child-compensation", root, () => LinuxUpdateScenarios.RunUnconfirmedChildCompensationScenarioAsync(root, candidate)).ConfigureAwait(false);
        return runner.Finish();
    }

    internal static async Task<int> RunKeyWriterChildAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 5);
        HarnessSupport.RequireLinuxX64();
        var keyDirectory = Path.GetFullPath(args[1]);
        var gate = Path.GetFullPath(args[2]);
        var ciphertextPath = Path.GetFullPath(args[3]);
        var payload = Encoding.UTF8.GetBytes(args[4]);
        await HarnessSupport.WaitForFileAsync(gate, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var ciphertext = new UnixSecretStore(keyDirectory).Protect(payload);
        await File.WriteAllBytesAsync(ciphertextPath, ciphertext).ConfigureAwait(false);
        return 0;
    }

    internal static async Task<int> RunKeyReaderChildAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 4);
        HarnessSupport.RequireLinuxX64();
        var keyDirectory = Path.GetFullPath(args[1]);
        var ciphertext = await File.ReadAllBytesAsync(Path.GetFullPath(args[2])).ConfigureAwait(false);
        var expected = Encoding.UTF8.GetBytes(args[3]);
        var actual = new UnixSecretStore(keyDirectory).Unprotect(ciphertext);
        HarnessSupport.Assert(actual.AsSpan().SequenceEqual(expected), "Cross-process secret decryption did not match its synthetic payload.");
        return 0;
    }

    internal static async Task<int> RunInstanceRaceWorkerAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 6);
        HarnessSupport.RequireLinuxX64();
        var runtime = Path.GetFullPath(args[1]);
        var name = args[2];
        await HarnessSupport.WaitForFileAsync(Path.GetFullPath(args[3]), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        using var instance = new LinuxSingleInstance(name, runtime, dispatch: action => action());
        HarnessSupport.WriteAtomic(Path.GetFullPath(args[5]), instance.IsFirstInstance ? "owner\n" : "contender\n");
        if (instance.IsFirstInstance)
        {
            await HarnessSupport.WaitForFileAsync(Path.GetFullPath(args[4]), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        return 0;
    }

    internal static async Task<int> RunInstanceOwnerAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 7);
        HarnessSupport.RequireLinuxX64();
        var runtime = Path.GetFullPath(args[1]);
        var name = args[2];
        var ready = Path.GetFullPath(args[3]);
        var activated = Path.GetFullPath(args[4]);
        var stop = Path.GetFullPath(args[5]);
        var mode = args[6];
        HarnessSupport.Assert(mode is "hold" or "once", "Invalid single-instance child mode.");

        using var instance = new LinuxSingleInstance(name, runtime, dispatch: action => action());
        HarnessSupport.Assert(instance.IsFirstInstance, "The controlled owner did not acquire first-instance ownership.");
        instance.Listen(() => HarnessSupport.WriteAtomic(activated, "activation-dispatched\n"));
        HarnessSupport.Assert(instance.CanActivate, "The production activation listener did not become ready.");
        HarnessSupport.WriteAtomic(ready, "owner-ready\n");
        if (mode == "hold")
        {
            await HarnessSupport.WaitForFileAsync(stop, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        return 0;
    }

    internal static Task<int> RunInstanceClientAsync(string[] args)
    {
        HarnessSupport.RequireArgs(args, 4);
        HarnessSupport.RequireLinuxX64();
        using var instance = new LinuxSingleInstance(args[2], Path.GetFullPath(args[1]), dispatch: action => action());
        var activated = !instance.IsFirstInstance && instance.TryActivatePrimary();
        HarnessSupport.WriteAtomic(Path.GetFullPath(args[3]), activated ? "activation-acknowledged\n" : "activation-failed\n");
        return Task.FromResult(activated ? 0 : 1);
    }

    private static async Task RunSecretStoreScenarioAsync(string root)
    {
        var scenario = HarnessSupport.NewPrivateDirectory(root, "secret-store");
        var gate = Path.Combine(scenario, "start-gate");
        var ownedChildren = new List<Process>();
        try
        {
            var keyParent = HarnessSupport.NewPrivateDirectory(scenario, "keys");
            var keyDirectory = Path.Combine(keyParent, "OpenCodeAccountImporter");
            var syntheticHome = HarnessSupport.NewPrivateDirectory(scenario, "home");
            var firstCipher = Path.Combine(scenario, "first.cipher");
            var secondCipher = Path.Combine(scenario, "second.cipher");
            const string firstPayload = "synthetic-secret-payload-one";
            const string secondPayload = "synthetic-secret-payload-two";

            var first = HarnessSupport.StartTrackedProcess(ownedChildren,
                new[] { "--key-write", keyDirectory, gate, firstCipher, firstPayload }, syntheticHome);
            var second = HarnessSupport.StartTrackedProcess(ownedChildren,
                new[] { "--key-write", keyDirectory, gate, secondCipher, secondPayload }, syntheticHome);
            HarnessSupport.WriteAtomic(gate, "start\n");
            await HarnessSupport.WaitForExitAsync(first, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            await HarnessSupport.WaitForExitAsync(second, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            HarnessSupport.Assert(first.ExitCode == 0 && second.ExitCode == 0, "Concurrent secret-store child failed.");

            var keyPath = Path.Combine(keyDirectory, UnixSecretStore.KeyFileName);
            HarnessSupport.Assert(Directory.Exists(keyDirectory), "Concurrent first use did not create the private key directory.");
            HarnessSupport.Assert(new FileInfo(keyPath).Length == 32, "The published Unix key was not exactly 32 bytes.");
            HarnessSupport.AssertMode(keyDirectory, HarnessSupport.PrivateDirectoryMode, "Unix key directory");
            HarnessSupport.AssertMode(keyPath, HarnessSupport.PrivateFileMode, "Unix key file");

            var firstReader = HarnessSupport.StartTrackedProcess(ownedChildren,
                new[] { "--key-read", keyDirectory, firstCipher, firstPayload }, syntheticHome);
            var secondReader = HarnessSupport.StartTrackedProcess(ownedChildren,
                new[] { "--key-read", keyDirectory, secondCipher, secondPayload }, syntheticHome);
            await HarnessSupport.WaitForExitAsync(firstReader, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            await HarnessSupport.WaitForExitAsync(secondReader, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            HarnessSupport.Assert(firstReader.ExitCode == 0 && secondReader.ExitCode == 0, "A separate process could not decrypt a concurrent writer's ciphertext.");

            var store = new UnixSecretStore(keyDirectory);
            var backupDirectory = Path.Combine(scenario, "backups");
            var backup = new BackupService(backupDirectory, store.Protect, store.Unprotect);
            var syntheticCredential = JsonNode.Parse("{\"id\":\"synthetic-only\"}")!;
            var backupPath = backup.Save(new[] { syntheticCredential }, "/synthetic/config.json", "{\"synthetic\":true}");
            var restored = backup.Read(backupPath);
            HarnessSupport.Assert(restored?["credentials"]?[0]?["id"]?.GetValue<string>() == "synthetic-only", "The synthetic encrypted backup did not round-trip.");
            HarnessSupport.AssertMode(backupDirectory, HarnessSupport.PrivateDirectoryMode, "Encrypted backup directory");
            HarnessSupport.AssertMode(backupPath, HarnessSupport.PrivateFileMode, "Encrypted backup file");
        }
        finally
        {
            Exception? markerFailure = null;
            try
            {
                if (!File.Exists(gate)) { HarnessSupport.WriteAtomic(gate, "release\n"); }
            }
            catch (Exception ex) { markerFailure = ex; }

            await HarnessSupport.CleanupOwnedProcessesAsync(ownedChildren, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            HarnessSupport.DeleteOwnedDirectory(scenario);
            if (markerFailure is not null)
            {
                throw new IOException("The secret-store release marker could not be written.", markerFailure);
            }
        }
    }

    private static async Task RunSingleInstanceScenarioAsync(string root)
    {
        var scenario = HarnessSupport.NewPrivateDirectory(root, "single-instance");
        var runtime = Path.Combine(root, "r");
        Directory.CreateDirectory(runtime);
        File.SetUnixFileMode(runtime, HarnessSupport.PrivateDirectoryMode);
        var ownedChildren = new List<Process>();
        var releaseGates = new List<string>();
        var ownerStopMarkers = new List<string>();
        try
        {
            var raceName = "ocam-harness-race-" + Guid.NewGuid().ToString("N");
            HarnessSupport.Assert(SingleInstanceProtocol.IsSocketPathWithinLimit(LinuxSingleInstance.ComputeSocketPath(raceName, runtime)),
                "The controlled runtime path does not fit the production Unix socket limit.");
            var startGate = Path.Combine(scenario, "race-start");
            var releaseGate = Path.Combine(scenario, "race-release");
            releaseGates.Add(releaseGate);
            var firstResult = Path.Combine(scenario, "race-first");
            var secondResult = Path.Combine(scenario, "race-second");
            var first = HarnessSupport.StartTrackedProcess(ownedChildren,
                new[] { "--instance-race-worker", runtime, raceName, startGate, releaseGate, firstResult });
            var second = HarnessSupport.StartTrackedProcess(ownedChildren,
                new[] { "--instance-race-worker", runtime, raceName, startGate, releaseGate, secondResult });
            HarnessSupport.WriteAtomic(startGate, "race\n");
            await HarnessSupport.WaitForFileAsync(firstResult, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            await HarnessSupport.WaitForFileAsync(secondResult, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            HarnessSupport.WriteAtomic(releaseGate, "release\n");
            await HarnessSupport.WaitForExitAsync(first, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await HarnessSupport.WaitForExitAsync(second, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            HarnessSupport.Assert(first.ExitCode == 0 && second.ExitCode == 0, "A simultaneous first-instance child failed.");
            var winners = new[] { File.ReadAllText(firstResult), File.ReadAllText(secondResult) }.Count(value => value == "owner\n");
            HarnessSupport.Assert(winners == 1, "Simultaneous independent processes did not produce exactly one owner.");

            var activationName = "ocam-harness-activation-" + Guid.NewGuid().ToString("N");
            HarnessSupport.Assert(SingleInstanceProtocol.IsSocketPathWithinLimit(LinuxSingleInstance.ComputeSocketPath(activationName, runtime)),
                "The activation runtime path does not fit the production Unix socket limit.");
            var ownerReady = Path.Combine(scenario, "owner-ready");
            var activationSeen = Path.Combine(scenario, "activation-seen");
            var ownerStop = Path.Combine(scenario, "owner-stop");
            ownerStopMarkers.Add(ownerStop);
            var owner = await StartOwnerAsync(ownedChildren, runtime, activationName, ownerReady, activationSeen, ownerStop, "hold").ConfigureAwait(false);
            try
            {
                var clientResult = Path.Combine(scenario, "client-result");
                var client = HarnessSupport.StartTrackedProcess(ownedChildren,
                    new[] { "--instance-client", runtime, activationName, clientResult });
                await HarnessSupport.WaitForExitAsync(client, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                HarnessSupport.Assert(client.ExitCode == 0 && File.ReadAllText(clientResult) == "activation-acknowledged\n", "The production second-instance activation was not acknowledged.");
                await HarnessSupport.WaitForFileAsync(activationSeen, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            finally { HarnessSupport.WriteAtomic(ownerStop, "stop\n"); }
            await HarnessSupport.WaitForExitAsync(owner, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            HarnessSupport.Assert(owner.ExitCode == 0, "The graceful owner did not release its production IPC channel.");

            var gracefulRestartReady = Path.Combine(scenario, "graceful-restart-ready");
            var gracefulRestart = HarnessSupport.StartTrackedProcess(ownedChildren, new[]
            {
                "--instance-owner", runtime, activationName, gracefulRestartReady,
                Path.Combine(scenario, "unused-activation"), Path.Combine(scenario, "unused-stop"), "once",
            });
            await HarnessSupport.WaitForFileAsync(gracefulRestartReady, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await HarnessSupport.WaitForExitAsync(gracefulRestart, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            HarnessSupport.Assert(gracefulRestart.ExitCode == 0, "A fresh process could not acquire ownership after graceful exit.");

            var abruptName = "ocam-harness-abrupt-" + Guid.NewGuid().ToString("N");
            HarnessSupport.Assert(SingleInstanceProtocol.IsSocketPathWithinLimit(LinuxSingleInstance.ComputeSocketPath(abruptName, runtime)),
                "The abrupt-exit runtime path does not fit the production Unix socket limit.");
            var abruptReady = Path.Combine(scenario, "abrupt-owner-ready");
            var abruptStop = Path.Combine(scenario, "abrupt-owner-stop");
            ownerStopMarkers.Add(abruptStop);
            var abruptOwner = await StartOwnerAsync(ownedChildren, runtime, abruptName, abruptReady,
                Path.Combine(scenario, "abrupt-activation"), abruptStop, "hold").ConfigureAwait(false);
            var identity = ProcessIdentityReader.Read(abruptOwner.Id);
            HarnessSupport.Assert(identity is not null && identity.ProcessId == abruptOwner.Id, "The abrupt-exit target was not a readable harness-owned process.");
            abruptOwner.Kill(entireProcessTree: false);
            await HarnessSupport.WaitForExitAsync(abruptOwner, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            HarnessSupport.Assert(abruptOwner.ExitCode != 0, "The deliberately abrupt owner unexpectedly reported a graceful exit.");

            var abruptRestartReady = Path.Combine(scenario, "abrupt-restart-ready");
            var abruptRestart = HarnessSupport.StartTrackedProcess(ownedChildren, new[]
            {
                "--instance-owner", runtime, abruptName, abruptRestartReady,
                Path.Combine(scenario, "unused-abrupt-activation"), Path.Combine(scenario, "unused-abrupt-stop"), "once",
            });
            await HarnessSupport.WaitForFileAsync(abruptRestartReady, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await HarnessSupport.WaitForExitAsync(abruptRestart, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            HarnessSupport.Assert(abruptRestart.ExitCode == 0, "A fresh process could not acquire ownership after abrupt owner exit.");
        }
        finally
        {
            Exception? markerFailure = null;
            foreach (var releaseGate in releaseGates)
            {
                try { HarnessSupport.WriteAtomic(releaseGate, "release\n"); }
                catch (Exception ex) { markerFailure ??= ex; }
            }
            foreach (var stopMarker in ownerStopMarkers)
            {
                try { HarnessSupport.WriteAtomic(stopMarker, "stop\n"); }
                catch (Exception ex) { markerFailure ??= ex; }
            }

            await HarnessSupport.CleanupOwnedProcessesAsync(ownedChildren, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            HarnessSupport.DeleteOwnedDirectory(runtime);
            HarnessSupport.DeleteOwnedDirectory(scenario);
            if (markerFailure is not null)
            {
                throw new IOException("A single-instance shutdown marker could not be written.", markerFailure);
            }
        }
    }

    private static async Task<Process> StartOwnerAsync(
        List<Process> ownedChildren,
        string runtime,
        string name,
        string ready,
        string activated,
        string stop,
        string mode)
    {
        var process = HarnessSupport.StartTrackedProcess(ownedChildren,
            new[] { "--instance-owner", runtime, name, ready, activated, stop, mode });
        try
        {
            await HarnessSupport.WaitForFileAsync(ready, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            HarnessSupport.Assert(!process.HasExited, "The production single-instance owner exited before the caller used it.");
            return process;
        }
        catch
        {
            HarnessSupport.WriteAtomic(stop, "stop\n");
            throw;
        }
    }

    private static async Task RunCurlSuccessScenarioAsync()
    {
        var body = Encoding.UTF8.GetBytes("{\"synthetic\":true}");
        using var fixture = LocalCurlFixture.Start(body, holdUntilDisconnect: false);
        var server = fixture.ServeAsync();
        var transport = new SystemCurlTransport();
        var config = SubscriptionService.BuildCurlStdinConfig(SyntheticCurlToken, SyntheticAccount, "/synthetic/native-harness");
        var result = await transport.FetchAsync(FindCurl(), fixture.Url, proxy: null, config,
            budget: TimeSpan.FromSeconds(6)).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        var authenticated = await server.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        HarnessSupport.Assert(authenticated, "The local fixture did not receive the synthetic Authorization header from curl stdin config.");
        HarnessSupport.Assert(result.TransportOk && result.Status == 200 && result.Body.AsSpan().SequenceEqual(body),
            "The production curl transport did not return the bounded local fixture response.");
    }

    private static async Task RunCurlOutputBoundScenarioAsync()
    {
        var body = new byte[SubscriptionService.MaxBytes + 1];
        Array.Fill(body, (byte)'x');
        using var fixture = LocalCurlFixture.Start(body, holdUntilDisconnect: false);
        var server = fixture.ServeAsync();
        var config = SubscriptionService.BuildCurlStdinConfig(SyntheticCurlToken, SyntheticAccount, "/synthetic/oversize");
        var result = await new SystemCurlTransport().FetchAsync(FindCurl(), fixture.Url, proxy: null, config,
            budget: TimeSpan.FromSeconds(8)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await server.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        HarnessSupport.Assert(!result.TransportOk && result.Body.Length == 0,
            "The production curl transport accepted a response one byte beyond its body cap.");
    }

    private static async Task RunCurlCancellationScenarioAsync()
    {
        using var fixture = LocalCurlFixture.Start(Array.Empty<byte>(), holdUntilDisconnect: true);
        var server = fixture.ServeAsync();
        var config = SubscriptionService.BuildCurlStdinConfig(SyntheticCurlToken, SyntheticAccount, "/synthetic/cancel");
        using var cancellation = new CancellationTokenSource();
        var fetch = new SystemCurlTransport().FetchAsync(FindCurl(), fixture.Url, proxy: null, config,
            budget: TimeSpan.FromSeconds(8), cancellationToken: cancellation.Token);
        await fixture.RequestReceived.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        cancellation.Cancel();
        await AssertCancelledAsync(fetch).ConfigureAwait(false);
        var authenticated = await server.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        HarnessSupport.Assert(authenticated, "The cancellation fixture did not receive the synthetic curl request.");
    }

    private static async Task AssertCancelledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("The production curl transport did not propagate controlled cancellation.");
    }

    private static string FindCurl()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return SubscriptionService.FindCurl(windows: false, directories, systemDirectory: null)
            ?? throw new FileNotFoundException("No executable system curl was found on the controlled PATH.");
    }

    private sealed class ScenarioRunner
    {
        private int _executed;
        private int _passed;
        private int _failed;

        internal async Task RunAsync(string name, string root, Func<Task> scenario)
        {
            _executed++;
            var childRoot = HarnessSupport.NewPrivateDirectory(root, "children-" + name);
            using var rootScope = HarnessSupport.UseChildRoot(childRoot);
            var cleanupUnconfirmed = false;
            try
            {
                await scenario().ConfigureAwait(false);
                _passed++;
                Console.Out.WriteLine($"NATIVE_SCENARIO name={name} status=PASS");
            }
            catch (Exception ex)
            {
                _failed++;
                cleanupUnconfirmed = ex is HarnessSupport.OwnedChildCleanupException;
                Console.Out.WriteLine($"NATIVE_SCENARIO name={name} status=FAIL type={ex.GetType().Name}");
            }
            finally
            {
                var cleanupMarkers = cleanupUnconfirmed;
                try
                {
                    cleanupMarkers |= Directory.Exists(childRoot)
                        && (Directory.EnumerateFiles(childRoot, "owned-child-*.marker", SearchOption.TopDirectoryOnly).Any()
                            || File.Exists(Path.Combine(childRoot, "owned-child-identity-unavailable")));
                }
                catch { cleanupMarkers = true; }
                if (cleanupMarkers)
                {
                    Console.Out.WriteLine("NATIVE_OWNED_CHILD_CLEANUP=UNCONFIRMED");
                }
                else
                {
                    HarnessSupport.DeleteOwnedDirectory(childRoot);
                }
            }
        }

        internal int Finish()
        {
            Console.Out.WriteLine($"NATIVE_SCENARIOS executed={_executed} passed={_passed} failed={_failed} skipped=0");
            Console.Out.WriteLine("NATIVE_UI=NOT_TESTED (no Avalonia window, tray host, or desktop session was opened)");
            return _failed == 0 && _passed == _executed ? 0 : 1;
        }
    }

    private sealed class LocalCurlFixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _responseBody;
        private readonly bool _holdUntilDisconnect;
        private readonly TaskCompletionSource<bool> _requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private LocalCurlFixture(byte[] responseBody, bool holdUntilDisconnect)
        {
            _responseBody = responseBody;
            _holdUntilDisconnect = holdUntilDisconnect;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/synthetic";
        }

        internal string Url { get; }
        internal Task<bool> RequestReceived => _requestReceived.Task;

        internal static LocalCurlFixture Start(byte[] responseBody, bool holdUntilDisconnect)
            => new(responseBody, holdUntilDisconnect);

        internal async Task<bool> ServeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var client = await _listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            var request = await ReadHeadersAsync(stream, timeout.Token).ConfigureAwait(false);
            var authenticated = request.Contains("Authorization: Bearer " + SyntheticCurlToken, StringComparison.Ordinal);
            _requestReceived.TrySetResult(authenticated);
            if (_holdUntilDisconnect)
            {
                var buffer = new byte[256];
                while (await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false) != 0) { }
                return authenticated;
            }

            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {_responseBody.Length}\r\nConnection: close\r\nContent-Type: application/json\r\n\r\n");
            await stream.WriteAsync(headers, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(_responseBody, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            return authenticated;
        }

        public void Dispose()
        {
            try { _listener.Stop(); }
            catch (SocketException) { }
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var bytes = new MemoryStream();
            var single = new byte[1];
            while (bytes.Length < 64 * 1024)
            {
                var read = await stream.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) { throw new EndOfStreamException("The local curl fixture received a truncated request."); }
                bytes.WriteByte(single[0]);
                var length = bytes.Length;
                if (length >= 4)
                {
                    var content = bytes.GetBuffer();
                    if (content[length - 4] == '\r' && content[length - 3] == '\n' &&
                        content[length - 2] == '\r' && content[length - 1] == '\n')
                    {
                        return Encoding.ASCII.GetString(content, 0, checked((int)length));
                    }
                }
            }
            throw new InvalidDataException("The local curl fixture request headers exceeded their bound.");
        }
    }
}

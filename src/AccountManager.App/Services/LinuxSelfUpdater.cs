using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountManager.Core.Updates;

namespace AccountManager.App.Services;

/// <summary>Linux x64 portable-bundle handover. Windows continues to use <see cref="SelfUpdater"/>.</summary>
public static class LinuxSelfUpdater
{
    private const int ReadyTimeoutMilliseconds = 20_000;
    private const int ParentExitTimeoutMilliseconds = 30_000;
    private const int ChildExitTimeoutMilliseconds = 10_000;
    private const int ProbeTimeoutMilliseconds = 5_000;
    private const string UnsupportedMessage = "Linux 自动升级仅支持 Linux x64、当前用户拥有且可写的单文件便携版；请从发布页手动下载并替换程序。";
    private const string RecoveryMessage = "Linux 自动更新失败，旧版本备份已保留。请勿删除备份，并按手动恢复说明恢复程序。";

    private static readonly object PendingSync = new();
    private static string? _pendingOldPath;
    private static LinuxUpdateSwap.FileProof? _pendingOldProof;
    private static string? _pendingCurrentPath;
    private static LinuxUpdateSwap.FileProof? _pendingCurrentProof;

    /// <summary>True only for the owned, writable, single-file Linux x64 portable executable.</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                var executable = Environment.ProcessPath;
                var entry = Assembly.GetEntryAssembly();
                if (entry is null) { return false; }
#pragma warning disable IL3000 // A non-empty location means this is not the deployed single-file bundle.
                var singleFile = string.IsNullOrEmpty(entry.Location);
#pragma warning restore IL3000
                if (executable is null || !IsEligibleBundle(OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture == Architecture.X64, executable, singleFile)) { return false; }
                return LinuxUpdateSwap.IsSafeOwnedInstallation(executable);
            }
            catch { return false; }
        }
    }

    internal static bool IsEligibleBundle(bool isLinux, bool isX64, string? executablePath, bool isSingleFile)
    {
        if (!isLinux || !isX64 || !isSingleFile || string.IsNullOrWhiteSpace(executablePath)) { return false; }
        var name = Path.GetFileName(executablePath);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "testhost", StringComparison.OrdinalIgnoreCase)) { return false; }
        return string.Equals(name, LinuxUpdateProtocol.ProductName, StringComparison.Ordinal);
    }

    internal static bool IsExpectedRuntimeVersion(Version actual, Version expected)
        => UpdateProbe.TryParseThreePartVersion(actual.ToString(3), out var normalizedActual)
            && UpdateProbe.TryParseThreePartVersion(expected.ToString(3), out var normalizedExpected)
            && normalizedActual == normalizedExpected;

    internal static bool IsSameProcessIdentity(
        int expectedPid,
        long expectedStartUtcTicks,
        long expectedEffectiveUid,
        int actualPid,
        long actualStartUtcTicks,
        long actualEffectiveUid)
        => expectedPid > 0 && expectedPid == actualPid
            && expectedStartUtcTicks > 0 && expectedStartUtcTicks == actualStartUtcTicks
            && expectedEffectiveUid >= 0 && expectedEffectiveUid == actualEffectiveUid;

    internal static bool CanRollbackAfterProbe(Exception exception)
        => exception is not LinuxProbeExecutionException { ProcessExitConfirmed: false };

    /// <summary>Stages, probes, swaps and launches the already checksum-verified Linux candidate.</summary>
    public static async Task PrepareAndLaunchAsync(
        string verifiedDownloadPath,
        Version expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported) { throw new InvalidOperationException(UnsupportedMessage); }
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedDownloadPath);
        ArgumentNullException.ThrowIfNull(expectedVersion);
        if (!UpdateProbe.TryParseThreePartVersion(expectedVersion.ToString(3), out var normalizedVersion))
        {
            throw new InvalidDataException("The expected update version is invalid.");
        }

        var currentExe = Path.GetFullPath(Environment.ProcessPath!);
        var directory = Path.GetDirectoryName(currentExe)!;
        var baseName = Path.GetFileName(currentExe);
        var suffix = Guid.NewGuid().ToString("N");
        var staged = Path.Combine(directory, "." + baseName + ".new-" + suffix);
        var oldName = baseName + ".old-" + suffix;
        var oldPath = Path.Combine(directory, oldName);
        var pipeName = LinuxUpdateProtocol.PipePrefix + Guid.NewGuid().ToString("N");
        var token = LinuxUpdateProtocol.CreateRandomToken();
        var swap = new LinuxUpdateSwap(verifiedDownloadPath, currentExe, staged, oldPath);
        Process? child = null;
        ProcessIdentity? childIdentity = null;
        var childExitConfirmed = true;
        LinuxUpdatePipeSet? preparedPipes = null;
        var rollbackHandled = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            swap.Stage();
            await ValidateCandidateAsync(swap.StagedPath, normalizedVersion, RunProbeAsync, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var parent = Process.GetCurrentProcess();
            var parentIdentity = ReadProcessIdentity(parent);
            if (parentIdentity is null || parentIdentity.Value.EffectiveUid != GetEffectiveUid())
            {
                throw new InvalidOperationException("Could not verify the current process identity.");
            }

            // Pipe, parent identity, paths, candidate validation and post-swap assemblies are all
            // ready before the installed bundle path is replaced. This same coordinator is used by
            // failure/timeout tests with only the external launch boundary substituted.
            rollbackHandled = true;
            await RunSwapAndAwaitReadyAsync(
                swap,
                async () =>
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = currentExe,
                        WorkingDirectory = directory,
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                    };
                    AddUpdateArguments(startInfo, parentIdentity.Value, pipeName, token, oldName, normalizedVersion);

                    var readyDeadline = Environment.TickCount64 + ReadyTimeoutMilliseconds;
                    child = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the updated application.");
                    childIdentity = ReadProcessIdentity(child);
                    if (childIdentity is null || childIdentity.Value.ProcessId != child.Id)
                    {
                        throw new InvalidOperationException("Could not verify the updated process identity.");
                    }

                    await WaitForReadyAsync(
                        preparedPipes!,
                        pipeName,
                        token,
                        normalizedVersion,
                        child,
                        childIdentity.Value,
                        swap.BackupProof!,
                        swap.CurrentProof!,
                        readyDeadline,
                        cancellationToken).ConfigureAwait(false);
                },
                () =>
                {
                    childExitConfirmed = child is null || TryStopOwnedChild(child, childIdentity);
                    return childExitConfirmed;
                },
                () =>
                {
                    PreloadAssembliesUsedAfterSwap();
                    preparedPipes = LinuxUpdateProtocol.CreatePipeSet(pipeName);
                    cancellationToken.ThrowIfCancellationRequested();
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (rollbackHandled) { throw; }

            if (!CanRollbackAfterProbe(ex)) { childExitConfirmed = false; }
            if (child is not null)
            {
                childExitConfirmed = TryStopOwnedChild(child, childIdentity);
            }

            var rollback = swap.Rollback(childExitConfirmed);
            if (rollback is not null)
            {
                throw new InvalidOperationException(RecoveryMessage, ex);
            }

            if (ex is OperationCanceledException) { throw; }
            throw new InvalidOperationException("Linux 自动更新未完成；旧版本已恢复。", ex);
        }
        finally
        {
            preparedPipes?.Dispose();
            child?.Dispose();
        }
    }

    /// <summary>Actual swap/launch coordinator; production supplies only the OS process/pipe boundary.</summary>
    internal static async Task RunSwapAndAwaitReadyAsync(
        LinuxUpdateSwap swap,
        Func<Task> launchAndAwaitReady,
        Func<bool> childExitConfirmed,
        Action? prepareBeforeSwap = null)
    {
        try
        {
            prepareBeforeSwap?.Invoke();
            swap.Execute();
            await launchAndAwaitReady().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bool exited;
            try { exited = childExitConfirmed(); }
            catch { exited = false; }
            var rollback = swap.Rollback(exited);
            if (rollback is not null) { throw new InvalidOperationException(RecoveryMessage, ex); }
            if (ex is OperationCanceledException) { throw; }
            if (ex is LinuxUpdatePreparationException)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            throw new InvalidOperationException("Linux 自动更新未完成；旧版本已恢复。", ex);
        }
    }

    /// <summary>Processes reserved update arguments before single-instance/UI startup.</summary>
    public static bool HandleStartup(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.Any(IsLinuxUpdateArgument)) { return true; }
        if (!OperatingSystem.IsLinux() || !IsSupported) { return false; }

        var ownExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(ownExe) || !LinuxUpdateProtocol.TryParseArguments(args, Path.GetFileName(ownExe), out var parsed) || parsed is null)
        {
            return false;
        }

        return HandleUpdateChild(parsed, ownExe);
    }

    /// <summary>Called only when the application's main window has actually opened.</summary>
    public static void CompleteStartup()
    {
        string? oldPath;
        LinuxUpdateSwap.FileProof? oldProof;
        string? currentPath;
        LinuxUpdateSwap.FileProof? currentProof;
        lock (PendingSync)
        {
            oldPath = _pendingOldPath;
            oldProof = _pendingOldProof;
            currentPath = _pendingCurrentPath;
            currentProof = _pendingCurrentProof;
            _pendingOldPath = null;
            _pendingOldProof = null;
            _pendingCurrentPath = null;
            _pendingCurrentProof = null;
        }

        if (oldPath is null || oldProof is null || currentPath is null || currentProof is null) { return; }
        if (!LinuxUpdateSwap.Matches(currentProof, currentPath)) { return; }
        LinuxUpdateSwap.TryCleanupOwnedBackup(oldPath, oldProof);
    }

    /// <summary>
    /// The production probe runner is injected only at the external process boundary; ELF, identity,
    /// hash and protocol decisions below are the same code used by the default updater path.
    /// </summary>
    internal static async Task ValidateCandidateAsync(
        string candidatePath,
        Version expectedVersion,
        Func<string, CancellationToken, Task<string>> runProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runProbe);
        if (!UpdateProbe.TryParseThreePartVersion(expectedVersion.ToString(3), out var normalizedVersion))
        {
            throw new InvalidDataException("The expected update version is invalid.");
        }

        LinuxUpdateSwap.FileProof before;
        try { before = LinuxUpdateSwap.Capture(candidatePath); }
        catch { throw new InvalidDataException("The staged update is not a regular file."); }
        if (!LinuxUpdateSwap.IsLinuxX64Executable(candidatePath))
        {
            throw new InvalidDataException("The staged update is not a complete Linux x64 ELF executable.");
        }

        var output = await runProbe(candidatePath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!UpdateProbe.IsExpectedOutput(output, normalizedVersion, LinuxUpdateProtocol.PlatformName))
        {
            throw new InvalidDataException("The staged update probe did not report the expected product, version, platform and protocol.");
        }
        if (!LinuxUpdateSwap.Matches(before, candidatePath))
        {
            throw new InvalidDataException("The staged update changed while it was being probed.");
        }
    }

    internal static uint GetEffectiveUid()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        return GetEuid();
    }

    private static async Task<string> RunProbeAsync(string candidatePath, CancellationToken cancellationToken)
    {
        return await LinuxUpdateProbeRunner.RunAsync(
            _ =>
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = candidatePath,
                        WorkingDirectory = Path.GetDirectoryName(candidatePath)!,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                    EnableRaisingEvents = true,
                };
                process.StartInfo.ArgumentList.Add(UpdateProbe.Flag);
                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidDataException("The staged update probe could not be started.");
                }
                return new ProcessProbe(process);
            },
            ProbeTimeoutMilliseconds,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForReadyAsync(
        LinuxUpdatePipeSet pipes,
        string pipeName,
        string token,
        Version expectedVersion,
        Process child,
        ProcessIdentity childIdentity,
        LinuxUpdateSwap.FileProof oldProof,
        LinuxUpdateSwap.FileProof newProof,
        long deadline,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var deadlineTimer = new Timer(_ => linked.Cancel(), null, Remaining(deadline), Timeout.Infinite);
        linked.Token.ThrowIfCancellationRequested();
        var requestBody = await ReceiveFrameAsync(pipes.Entry, deadline, linked.Token).ConfigureAwait(false);
        // Each phase has a pre-bound, distinct endpoint; close the consumed entry listener.
        linked.Token.ThrowIfCancellationRequested();
        pipes.Entry.Dispose();
        if (!TryParseEntryMessage(requestBody, out var entry) || entry is null ||
            entry.Product != LinuxUpdateProtocol.ProductName || entry.Version != expectedVersion.ToString(3) ||
            entry.Platform != LinuxUpdateProtocol.PlatformName || entry.Protocol != LinuxUpdateProtocol.ProtocolVersion ||
            entry.Token != token || entry.ChildPid != childIdentity.ProcessId ||
            entry.ChildStartUtcTicks != childIdentity.StartUtcTicks || !IsSameLiveProcess(child, childIdentity))
        {
            throw new InvalidDataException("The updated process did not authenticate its managed entry.");
        }
        linked.Token.ThrowIfCancellationRequested();
        LinuxUpdateProtocol.EnsureBeforeDeadline(deadline, static () => Environment.TickCount64);

        var message = new LinuxUpdateProofMessage(
            LinuxUpdateProtocol.ProductName,
            expectedVersion.ToString(3),
            LinuxUpdateProtocol.PlatformName,
            LinuxUpdateProtocol.ProtocolVersion,
            token,
            childIdentity.ProcessId,
            true,
            oldProof,
            newProof);
        await SendFrameAsync(pipes.Proof, LinuxUpdateProtocol.EncodeFrame(message), deadline, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        pipes.ReleaseProof();

        var finalBody = await ReceiveFrameAsync(pipes.Ready, deadline, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        var json = Encoding.UTF8.GetString(finalBody);
        if (!LinuxUpdateProtocol.IsValidReadinessMessage(json, expectedVersion, token, childIdentity.ProcessId) ||
            !IsSameLiveProcess(child, childIdentity))
        {
            throw new InvalidDataException("The updated process readiness message was invalid or late.");
        }
        linked.Token.ThrowIfCancellationRequested();
        LinuxUpdateProtocol.EnsureBeforeDeadline(deadline, static () => Environment.TickCount64);
    }

    private static bool HandleUpdateChild(LinuxUpdateLaunchArgs parsed, string ownExe)
    {
        var actualVersion = AppUpdateInfo.Version;
        if (!IsExpectedRuntimeVersion(actualVersion, parsed.ExpectedVersion)) { return false; }
        var actualVersionText = actualVersion.ToString(3);
        var ownIdentity = ReadCurrentProcessIdentity();
        if (ownIdentity is null || ownIdentity.Value.EffectiveUid != GetEffectiveUid()) { return false; }

        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(parsed.ParentPid);
            var parentIdentity = ReadProcessIdentity(parent);
            if (parentIdentity is null || !IsSameProcessIdentity(
                    parsed.ParentPid,
                    parsed.ParentStartUtcTicks,
                    ownIdentity.Value.EffectiveUid,
                    parentIdentity.Value.ProcessId,
                    parentIdentity.Value.StartUtcTicks,
                    parentIdentity.Value.EffectiveUid))
            {
                return false;
            }

            var deadline = Environment.TickCount64 + ReadyTimeoutMilliseconds;
            var request = new LinuxUpdateEntryMessage(
                LinuxUpdateProtocol.ProductName,
                actualVersionText,
                LinuxUpdateProtocol.PlatformName,
                LinuxUpdateProtocol.ProtocolVersion,
                parsed.Token,
                ownIdentity.Value.ProcessId,
                ownIdentity.Value.StartUtcTicks);
            SendSingleFrameAsync(parsed.PipeName, LinuxUpdatePipePhase.Entry, LinuxUpdateProtocol.EncodeFrame(request), deadline).GetAwaiter().GetResult();

            var proofBody = ReceiveSingleFrameAsync(parsed.PipeName, LinuxUpdatePipePhase.Proof, deadline).GetAwaiter().GetResult();
            if (!TryParseProofMessage(proofBody, out var proofMessage) || proofMessage is null ||
                !proofMessage.Accepted || proofMessage.Product != LinuxUpdateProtocol.ProductName ||
                proofMessage.Version != actualVersionText || proofMessage.Platform != LinuxUpdateProtocol.PlatformName ||
                proofMessage.Protocol != LinuxUpdateProtocol.ProtocolVersion || proofMessage.Token != parsed.Token ||
                proofMessage.ChildPid != ownIdentity.Value.ProcessId || proofMessage.OldFile is null || proofMessage.NewFile is null)
            {
                return false;
            }
            LinuxUpdateProtocol.EnsureBeforeDeadline(deadline, static () => Environment.TickCount64);

            var directory = Path.GetDirectoryName(Path.GetFullPath(ownExe));
            if (string.IsNullOrEmpty(directory) || proofMessage.OldFile.Name != parsed.OldFileName ||
                !LinuxUpdateProtocol.IsValidOldFileName(parsed.OldFileName, ownExe)) { return false; }
            var oldPath = Path.Combine(directory, parsed.OldFileName);
            if (!LinuxUpdateSwap.Matches(proofMessage.OldFile, oldPath) || !LinuxUpdateSwap.Matches(proofMessage.NewFile, ownExe)) { return false; }

            var ready = new LinuxUpdateReadyMessage(
                LinuxUpdateProtocol.ProductName,
                actualVersionText,
                LinuxUpdateProtocol.PlatformName,
                LinuxUpdateProtocol.ProtocolVersion,
                parsed.Token,
                ownIdentity.Value.ProcessId);
            var parentBeforeReady = ReadProcessIdentity(parent);
            if (parentBeforeReady is null || !IsSameProcessIdentity(
                    parentIdentity.Value.ProcessId,
                    parentIdentity.Value.StartUtcTicks,
                    parentIdentity.Value.EffectiveUid,
                    parentBeforeReady.Value.ProcessId,
                    parentBeforeReady.Value.StartUtcTicks,
                    parentBeforeReady.Value.EffectiveUid)) { return false; }
            LinuxUpdateProtocol.EnsureBeforeDeadline(deadline, static () => Environment.TickCount64);
            SendSingleFrameAsync(parsed.PipeName, LinuxUpdatePipePhase.Ready, LinuxUpdateProtocol.EncodeFrame(ready), deadline).GetAwaiter().GetResult();
            bool exited;
            try { exited = parent.WaitForExit(ParentExitTimeoutMilliseconds); }
            catch { return false; }
            if (!exited) { return false; }
            try { if (!parent.HasExited) { return false; } }
            catch { return false; }

            lock (PendingSync)
            {
                _pendingOldPath = oldPath;
                _pendingOldProof = proofMessage.OldFile;
                _pendingCurrentPath = Path.GetFullPath(ownExe);
                _pendingCurrentProof = proofMessage.NewFile;
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally { parent?.Dispose(); }
    }

    internal static async Task<byte[]> ReceiveFrameAsync(NamedPipeServerStream server, long deadline, CancellationToken cancellationToken)
    {
        await LinuxUpdateProtocol.WithDeadlineAsync(deadline, static () => Environment.TickCount64, cancellationToken,
            async token =>
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        return await LinuxUpdateProtocol.ReadFrameWithinDeadlineAsync(server, deadline, static () => Environment.TickCount64, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SendFrameAsync(NamedPipeServerStream server, byte[] frame, long deadline, CancellationToken cancellationToken)
    {
        await LinuxUpdateProtocol.WithDeadlineAsync(deadline, static () => Environment.TickCount64, cancellationToken,
            async token =>
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        await LinuxUpdateProtocol.WriteFrameWithinDeadlineAsync(server, frame, deadline, static () => Environment.TickCount64, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SendSingleFrameAsync(string pipeName, LinuxUpdatePipePhase phase, byte[] frame, long deadline)
    {
        using var client = await ConnectClientAsync(pipeName, phase, deadline, CancellationToken.None).ConfigureAwait(false);
        await LinuxUpdateProtocol.WriteFrameWithinDeadlineAsync(client, frame, deadline, static () => Environment.TickCount64, CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReceiveSingleFrameAsync(string pipeName, LinuxUpdatePipePhase phase, long deadline)
    {
        using var client = await ConnectClientAsync(pipeName, phase, deadline, CancellationToken.None).ConfigureAwait(false);
        return await LinuxUpdateProtocol.ReadFrameWithinDeadlineAsync(client, deadline, static () => Environment.TickCount64, CancellationToken.None).ConfigureAwait(false);
    }

    private static Task<NamedPipeClientStream> ConnectClientAsync(string pipeName, LinuxUpdatePipePhase phase, long deadline, CancellationToken cancellationToken)
        => LinuxUpdateProtocol.ConnectClientWithinDeadlineAsync(
            pipeName,
            phase,
            deadline,
            static () => Environment.TickCount64,
            cancellationToken,
            endpoint => new NamedPipeClientStream(".", endpoint, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
            (client, timeout, token) => client.ConnectAsync(timeout, token));

    private static bool TryParseEntryMessage(byte[] body, out LinuxUpdateEntryMessage? entry)
    {
        entry = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            if (!HasExactProperties(root, "product", "version", "platform", "protocol", "token", "childPid", "childStartUtcTicks")) { return false; }
            if (!TryString(root, "product", out var product) || !TryString(root, "version", out var version) ||
                !TryString(root, "platform", out var platform) || !TryInt(root, "protocol", out var protocol) ||
                !TryString(root, "token", out var token) || !TryInt(root, "childPid", out var childPid) ||
                !TryLong(root, "childStartUtcTicks", out var childStart)) { return false; }
            entry = new LinuxUpdateEntryMessage(product, version, platform, protocol, token, childPid, childStart);
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseProofMessage(byte[] body, out LinuxUpdateProofMessage? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            if (!HasExactProperties(root, "product", "version", "platform", "protocol", "token", "childPid", "accepted", "oldFile", "newFile")) { return false; }
            if (!TryString(root, "product", out var product) || !TryString(root, "version", out var version) ||
                !TryString(root, "platform", out var platform) || !TryInt(root, "protocol", out var protocol) ||
                !TryString(root, "token", out var token) || !TryInt(root, "childPid", out var childPid) ||
                !root.TryGetProperty("accepted", out var acceptedElement) || acceptedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryParseFileProof(root.GetProperty("oldFile"), out var oldProof) || !TryParseFileProof(root.GetProperty("newFile"), out var newProof)) { return false; }

            message = new LinuxUpdateProofMessage(product, version, platform, protocol, token, childPid,
                acceptedElement.GetBoolean(), oldProof, newProof);
            return true;
        }
        catch { return false; }
    }

    private static bool TryParseFileProof(JsonElement element, out LinuxUpdateSwap.FileProof? proof)
    {
        proof = null;
        if (!HasExactProperties(element, "name", "sha256", "length", "identity", "mode", "ownerUid", "lastWriteUtcTicks", "creationUtcTicks")) { return false; }
        if (!TryString(element, "name", out var name) || !TryString(element, "sha256", out var hash) ||
            !TryString(element, "identity", out var identity) || !TryLong(element, "length", out var length) || length < 0 ||
            !TryInt(element, "mode", out var mode) || !TryLong(element, "ownerUid", out var ownerUid) ||
            !TryLong(element, "lastWriteUtcTicks", out var lastWrite) || !TryLong(element, "creationUtcTicks", out var creation) ||
            hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)) || string.IsNullOrEmpty(identity)) { return false; }
        proof = new LinuxUpdateSwap.FileProof(name, hash, length, identity, mode, ownerUid, lastWrite, creation);
        return true;
    }

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) { return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) { return false; }
        }
        return seen.Count == names.Length && names.All(seen.Contains);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) { return false; }
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }

    private static bool IsSameLiveProcess(Process process, ProcessIdentity expected)
    {
        try
        {
            var actual = ReadProcessIdentity(process);
            return actual is not null && actual.Value == expected && !process.HasExited;
        }
        catch { return false; }
    }

    private static ProcessIdentity? ReadCurrentProcessIdentity()
    {
        try { return ReadProcessIdentity(Process.GetCurrentProcess()); }
        catch { return null; }
    }

    private static ProcessIdentity? ReadProcessIdentity(Process process)
    {
        try
        {
            process.Refresh();
            if (process.HasExited) { return null; }
            var pid = process.Id;
            var start = process.StartTime.ToUniversalTime().Ticks;
            var uid = ReadEffectiveUid(pid);
            return uid < 0 ? null : new ProcessIdentity(pid, start, uid);
        }
        catch { return null; }
    }

    private static long ReadEffectiveUid(int processId)
    {
        try
        {
            var statusPath = "/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/status";
            using var reader = new StreamReader(new FileStream(statusPath, FileMode.Open, FileAccess.Read, FileShare.Read));
            var total = 0;
            while (reader.ReadLine() is { } line)
            {
                total += line.Length;
                if (total > 64 * 1024) { return -1; }
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) { continue; }
                var values = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return values.Length >= 2 && uint.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : -1;
            }
        }
        catch { }
        return -1;
    }

    private static bool TryStopOwnedChild(Process child, ProcessIdentity? expected)
    {
        try
        {
            if (child.HasExited) { return true; }
            if (expected is null || !IsSameLiveProcess(child, expected.Value)) { return false; }
            child.Kill(entireProcessTree: false);
            if (!child.WaitForExit(ChildExitTimeoutMilliseconds) && !child.HasExited) { return false; }
            return true;
        }
        catch
        {
            try { return child.HasExited; }
            catch { return false; }
        }
    }

    private static bool TryStopProbeProcess(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: false); }
            return process.WaitForExit(ChildExitTimeoutMilliseconds) || process.HasExited;
        }
        catch { return false; }
    }

    private static void AddUpdateArguments(
        ProcessStartInfo startInfo,
        ProcessIdentity parent,
        string pipeName,
        string token,
        string oldName,
        Version version)
    {
        Add("--linux-update-parent", parent.ProcessId.ToString(CultureInfo.InvariantCulture));
        Add("--linux-update-start", parent.StartUtcTicks.ToString(CultureInfo.InvariantCulture));
        Add("--linux-update-pipe", pipeName);
        Add("--linux-update-token", token);
        Add("--linux-update-old", oldName);
        Add("--linux-update-version", version.ToString(3));
        void Add(string name, string value) { startInfo.ArgumentList.Add(name); startInfo.ArgumentList.Add(value); }
    }

    internal static void PrepareRequiredAssemblies(Action<AssemblyName> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        string[] names =
        {
            "System.Memory", "System.Threading.Thread", "System.Text.Encoding.Extensions",
            "System.Collections.NonGeneric", "Microsoft.Win32.Primitives", "System.IO.Pipes",
            "System.Diagnostics.Process", "System.Text.Json", "System.Security.Cryptography",
        };
        foreach (var name in names)
        {
            try { load(new AssemblyName(name)); }
            catch (Exception ex) { throw new LinuxUpdatePreparationException(ex); }
        }
    }

    private static void PreloadAssembliesUsedAfterSwap()
        => PrepareRequiredAssemblies(static name => Assembly.Load(name));

    private sealed class ProcessProbe : ILinuxUpdateProbeProcess
    {
        private readonly LinuxUpdateProbeOutputReader _outputReader;

        public ProcessProbe(Process process)
        {
            _outputReader = new LinuxUpdateProbeOutputReader(
                () => process.StandardOutput,
                () => process.StandardError);
            _process = process;
        }

        private readonly Process _process;

        public Task<LinuxProbeOutput> StartReadingOutputAsync(int maximumCharacters, CancellationToken cancellationToken)
            => _outputReader.ReadAsync(maximumCharacters, cancellationToken);

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public Task<bool> StopAndConfirmExitAsync() => Task.FromResult(TryStopProbeProcess(_process));

        public void Dispose()
        {
            _outputReader.Dispose();
            _process.Dispose();
        }
    }

    private static bool IsLinuxUpdateArgument(string argument)
        => argument.StartsWith("--linux-update", StringComparison.Ordinal);

    private static int Remaining(long deadline) => (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);

    private static uint GetEuid() => GetEuidNative();

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint GetEuidNative();

    private readonly record struct ProcessIdentity(int ProcessId, long StartUtcTicks, long EffectiveUid);
}

using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using AccountManager.App.Services;
using AccountManager.App.ViewModels;
using Xunit;

namespace AccountManager.App.Tests;

public sealed class LinuxSelfUpdaterTests
{
    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "am-linux-updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] BuildElf()
    {
        var bytes = new byte[128];
        bytes[0] = 0x7f;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 3); // ET_DYN
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 0x3e); // EM_X86_64
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        return bytes;
    }

    private static string[] ValidArgs(string baseName, string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") => new[]
    {
        "--linux-update-parent", "1234",
        "--linux-update-start", "638000000000000000",
        "--linux-update-pipe", "ocam-update-0123456789abcdef0123456789abcdef",
        "--linux-update-token", token,
        "--linux-update-old", baseName + ".old-0123456789abcdef0123456789abcdef",
        "--linux-update-version", "1.2.3",
    };

    [Fact]
    public void IsSupported_IsFalseForWindowsTestHostAndNeverRoutesIntoLinuxUpdate()
        => Assert.False(LinuxSelfUpdater.IsSupported);

    [Fact]
    public void BundleEligibilityRejectsDevelopmentTestHostAndNonX64Processes()
    {
        Assert.False(LinuxSelfUpdater.IsEligibleBundle(true, true, "/usr/bin/dotnet", true));
        Assert.False(LinuxSelfUpdater.IsEligibleBundle(true, true, "/tmp/testhost", true));
        Assert.False(LinuxSelfUpdater.IsEligibleBundle(true, false, "/home/user/OpenCodeAccountManager", true));
        Assert.False(LinuxSelfUpdater.IsEligibleBundle(true, true, "/home/user/OpenCodeAccountManager", false));
        Assert.False(LinuxSelfUpdater.IsEligibleBundle(false, true, "/home/user/OpenCodeAccountManager", true));
        Assert.True(LinuxSelfUpdater.IsEligibleBundle(true, true, "/home/user/OpenCodeAccountManager", true));
    }

    [Fact]
    public void ChildVersionMustBeTheActualNumericRuntimeVersionExpectedByTheParent()
    {
        Assert.True(LinuxSelfUpdater.IsExpectedRuntimeVersion(new Version(1, 2, 3, 0), new Version(1, 2, 3)));
        Assert.False(LinuxSelfUpdater.IsExpectedRuntimeVersion(new Version(1, 2, 4), new Version(1, 2, 3)));
    }

    [Fact]
    public void ParentIdentityMustMatchPidUtcStartAndEffectiveUidBeforeReadiness()
    {
        Assert.True(LinuxSelfUpdater.IsSameProcessIdentity(123, 638000000000000000, 1000, 123, 638000000000000000, 1000));
        Assert.False(LinuxSelfUpdater.IsSameProcessIdentity(123, 638000000000000000, 1000, 124, 638000000000000000, 1000));
        Assert.False(LinuxSelfUpdater.IsSameProcessIdentity(123, 638000000000000000, 1000, 123, 638000000000000001, 1000));
        Assert.False(LinuxSelfUpdater.IsSameProcessIdentity(123, 638000000000000000, 1000, 123, 638000000000000000, 1001));
    }

    [Fact]
    public void Protocol_ParsesExactlySixRequiredPairs()
    {
        var args = ValidArgs("OpenCodeAccountManager");

        Assert.True(LinuxUpdateProtocol.TryParseArguments(args, "OpenCodeAccountManager", out var parsed));
        Assert.Equal(1234, parsed!.ParentPid);
        Assert.Equal(638000000000000000L, parsed.ParentStartUtcTicks);
        Assert.Equal("ocam-update-0123456789abcdef0123456789abcdef", parsed.PipeName);
        Assert.Equal("1.2.3", parsed.ExpectedVersion.ToString(3));
    }

    [Fact]
    public void Protocol_RejectsMissingRepeatedUnknownAndMalformedValues()
    {
        var args = ValidArgs("OpenCodeAccountManager");
        Assert.False(LinuxUpdateProtocol.TryParseArguments(args[..^2], "OpenCodeAccountManager", out _));
        Assert.False(LinuxUpdateProtocol.TryParseArguments(args.Concat(new[] { "--linux-update-parent", "8" }).ToArray(), "OpenCodeAccountManager", out _));
        Assert.False(LinuxUpdateProtocol.TryParseArguments(args.Concat(new[] { "--unknown", "x" }).ToArray(), "OpenCodeAccountManager", out _));

        var badPid = (string[])args.Clone();
        badPid[1] = "0";
        Assert.False(LinuxUpdateProtocol.TryParseArguments(badPid, "OpenCodeAccountManager", out _));

        var badToken = (string[])args.Clone();
        badToken[7] = "abcd";
        Assert.False(LinuxUpdateProtocol.TryParseArguments(badToken, "OpenCodeAccountManager", out _));

        var badVersion = (string[])args.Clone();
        badVersion[11] = "1.2";
        Assert.False(LinuxUpdateProtocol.TryParseArguments(badVersion, "OpenCodeAccountManager", out _));
    }

    [Theory]
    [InlineData("../OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef")]
    [InlineData("/tmp/not-ours")]
    [InlineData("OtherApp.old-0123456789abcdef0123456789abcdef")]
    [InlineData("OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef/extra")]
    public void LinuxCleanupCannotTargetArbitraryFiles(string name)
        => Assert.False(LinuxUpdateProtocol.IsValidOldFileName(name, "/home/test/app/OpenCodeAccountManager"));

    [Fact]
    public void Protocol_ReadinessMustMatchProductPlatformVersionTokenAndActualChildPid()
    {
        const string valid = "{\"product\":\"OpenCodeAccountManager\",\"version\":\"1.2.3\",\"platform\":\"linux-x64\",\"protocol\":1,\"token\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"childPid\":77}";

        Assert.True(LinuxUpdateProtocol.IsValidReadinessMessage(valid, new Version(1, 2, 3), "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 77));
        Assert.False(LinuxUpdateProtocol.IsValidReadinessMessage(valid.Replace("\"childPid\":77", "\"childPid\":78", StringComparison.Ordinal), new Version(1, 2, 3), "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 77));
        Assert.False(LinuxUpdateProtocol.IsValidReadinessMessage(valid.Replace("1.2.3", "1.2.4", StringComparison.Ordinal), new Version(1, 2, 3), "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 77));
        Assert.False(LinuxUpdateProtocol.IsValidReadinessMessage(valid + "{}", new Version(1, 2, 3), "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 77));
    }

    [Fact]
    public async Task PipePhasesUseDistinctPreboundEndpointsAndCompleteInOrder()
    {
        const string baseName = "ocam-update-0123456789abcdef0123456789abcdef";
        using var pipes = LinuxUpdateProtocol.CreatePipeSet(baseName);

        Assert.Equal("ocam-update-0123456789abcdef0123456789abcdef-entry",
            LinuxUpdateProtocol.GetPhasePipeName(baseName, LinuxUpdatePipePhase.Entry));
        Assert.Equal("ocam-update-0123456789abcdef0123456789abcdef-proof",
            LinuxUpdateProtocol.GetPhasePipeName(baseName, LinuxUpdatePipePhase.Proof));
        Assert.Equal("ocam-update-0123456789abcdef0123456789abcdef-ready",
            LinuxUpdateProtocol.GetPhasePipeName(baseName, LinuxUpdatePipePhase.Ready));
        Assert.Equal(3, pipes.PreparedCount);

        var deadline = Environment.TickCount64 + 3000;
        var entry = LinuxUpdateProtocol.EncodeFrame(new { phase = "entry", token = "literal-token" });
        var proof = LinuxUpdateProtocol.EncodeFrame(new { phase = "proof", version = "1.2.3" });
        var ready = LinuxUpdateProtocol.EncodeFrame(new { phase = "ready", childPid = 77 });
        var parent = Task.Run(async () =>
        {
            Assert.Equal(entry[sizeof(int)..], await LinuxSelfUpdater.ReceiveFrameAsync(pipes.Entry, deadline, CancellationToken.None));
            await LinuxSelfUpdater.SendFrameAsync(pipes.Proof, proof, deadline, CancellationToken.None);
            pipes.ReleaseProof();
            return await LinuxSelfUpdater.ReceiveFrameAsync(pipes.Ready, deadline, CancellationToken.None);
        });

        await LinuxSelfUpdater.SendSingleFrameAsync(baseName, LinuxUpdatePipePhase.Entry, entry, deadline);
        var receivedProof = await LinuxSelfUpdater.ReceiveSingleFrameAsync(baseName, LinuxUpdatePipePhase.Proof, deadline);
        Assert.Equal(proof[sizeof(int)..], receivedProof);
        await LinuxSelfUpdater.SendSingleFrameAsync(baseName, LinuxUpdatePipePhase.Ready, ready, deadline);
        Assert.Equal(ready[sizeof(int)..], await parent);
    }

    [Fact]
    public async Task PipeFrameWriteCannotOutliveItsOriginalDeadline()
    {
        using var stream = new BlockingWriteStream();
        var deadline = Environment.TickCount64 + 40;
        var send = LinuxUpdateProtocol.WriteFrameWithinDeadlineAsync(
            stream, new byte[] { 1, 2, 3 }, deadline, static () => Environment.TickCount64, CancellationToken.None);

        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        var finishedInsideBudget = send.IsCompleted;
        stream.ReleaseWrite();
        var failure = await Record.ExceptionAsync(() => send);

        Assert.True(finishedInsideBudget);
        Assert.IsType<TimeoutException>(failure);
    }

    [Fact]
    public async Task PipeFrameFlushCannotOutliveItsOriginalDeadline()
    {
        using var stream = new BlockingWriteStream(blockWrite: false, blockFlush: true);
        var deadline = Environment.TickCount64 + 40;
        var send = LinuxUpdateProtocol.WriteFrameWithinDeadlineAsync(
            stream, new byte[] { 1, 2, 3 }, deadline, static () => Environment.TickCount64, CancellationToken.None);

        await stream.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        var finishedInsideBudget = send.IsCompleted;
        stream.ReleaseFlush();
        var failure = await Record.ExceptionAsync(() => send);

        Assert.True(finishedInsideBudget);
        Assert.IsType<TimeoutException>(failure);
    }

    [Fact]
    public async Task DeadlineRejectsAlreadyCompletedWorkWhenCallerWasAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LinuxUpdateProtocol.WithDeadlineAsync(
            100, static () => 0, cancellation.Token, _ => Task.FromResult(7)));
    }

    [Fact]
    public async Task DeadlineRejectsAndDisposesAlreadyCompletedWorkCancelledInsideTheOperation()
    {
        using var cancellation = new CancellationTokenSource();
        var resource = new TrackingDisposable();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LinuxUpdateProtocol.WithDeadlineAsync(
            100,
            static () => 0,
            cancellation.Token,
            _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(resource);
            }));

        Assert.True(resource.Disposed);
    }

    [Fact]
    public async Task UnsuccessfulClientConnectDisposesTheAllocatedClient()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new TrackingDisposable();

        var failure = await Record.ExceptionAsync(() => LinuxUpdateProtocol.ConnectClientWithinDeadlineAsync(
            "ocam-update-0123456789abcdef0123456789abcdef",
            LinuxUpdatePipePhase.Entry,
            100,
            static () => 0,
            cancellation.Token,
            _ => client,
            (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task ClientConnectSetupFailureDisposesTheAllocatedClient()
    {
        var client = new TrackingDisposable();

        var failure = await Record.ExceptionAsync(() => LinuxUpdateProtocol.ConnectClientWithinDeadlineAsync(
            "ocam-update-0123456789abcdef0123456789abcdef",
            LinuxUpdatePipePhase.Entry,
            100,
            static () => 0,
            CancellationToken.None,
            _ => client,
            (_, _, _) => Task.FromException(new InvalidOperationException("synthetic connect setup failure"))));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task PipeConnectCannotOutliveItsOriginalDeadline()
    {
        var baseName = "ocam-update-" + Guid.NewGuid().ToString("N");
        var failure = await Record.ExceptionAsync(() => LinuxSelfUpdater.SendSingleFrameAsync(
            baseName, LinuxUpdatePipePhase.Entry, new byte[] { 1, 2, 3, 4 }, Environment.TickCount64 + 60));

        Assert.IsType<TimeoutException>(failure);
    }

    [Fact]
    public async Task ValidFrameThatReachesEofAtTheDeadlineIsRejectedAsLate()
    {
        long now = 10;
        var frame = LinuxUpdateProtocol.EncodeFrame(new { ready = true });
        using var stream = new LateEofStream(frame, () => now = 20);

        await Assert.ThrowsAsync<TimeoutException>(() => LinuxUpdateProtocol.ReadFrameWithinDeadlineAsync(
            stream, 20, () => now, CancellationToken.None));
    }

    [Fact]
    public async Task SingleFrameReaderRejectsOversizeIncompleteAndTrailingData()
    {
        var oversizedHeader = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(oversizedHeader, LinuxUpdateProtocol.MaxMessageBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => LinuxUpdateProtocol.ReadSingleFrameAsync(
            new MemoryStream(oversizedHeader), CancellationToken.None));

        await Assert.ThrowsAsync<EndOfStreamException>(() => LinuxUpdateProtocol.ReadSingleFrameAsync(
            new MemoryStream(new byte[] { 1, 2 }), CancellationToken.None));

        var frame = LinuxUpdateProtocol.EncodeFrame(new byte[] { 9 });
        await Assert.ThrowsAsync<InvalidDataException>(() => LinuxUpdateProtocol.ReadSingleFrameAsync(
            new MemoryStream(frame.Concat(new byte[] { 0 }).ToArray()), CancellationToken.None));
    }

    [Fact]
    public void ReadinessValidationCannotSucceedAfterTheHandshakeBudget()
    {
        Assert.Throws<TimeoutException>(() => LinuxUpdateProtocol.EnsureBeforeDeadline(100, static () => 100));
    }

    [Fact]
    public async Task CandidateValidation_RequiresFullElfAndActualProbeVersionBeforeAccepting()
    {
        var directory = NewTempDir();
        try
        {
            var candidate = Path.Combine(directory, "candidate");
            await File.WriteAllBytesAsync(candidate, BuildElf());
            var probeCalls = 0;

            await Assert.ThrowsAsync<InvalidDataException>(() => LinuxSelfUpdater.ValidateCandidateAsync(
                candidate,
                new Version(1, 2, 3),
                (_, _) =>
                {
                    probeCalls++;
                    return Task.FromResult("{\"product\":\"OpenCodeAccountManager\",\"version\":\"1.2.4\",\"platform\":\"linux-x64\",\"protocol\":1}");
                },
                CancellationToken.None));

            Assert.Equal(1, probeCalls);

            await File.WriteAllTextAsync(candidate, "not an ELF");
            probeCalls = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => LinuxSelfUpdater.ValidateCandidateAsync(
                candidate,
                new Version(1, 2, 3),
                (_, _) =>
                {
                    probeCalls++;
                    return Task.FromResult("{}");
                },
                CancellationToken.None));
            Assert.Equal(0, probeCalls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Swap_CopiesVerifiedBytesToSameDirectoryAndKeepsUniqueOldBackup()
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());

            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Execute();

            Assert.Equal(BuildElf(), File.ReadAllBytes(current));
            Assert.Equal("old-bundle", File.ReadAllText(old));
            Assert.True(File.Exists(download));
            Assert.False(File.Exists(staged));
            Assert.NotNull(swap.BackupProof);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FileOwnershipProof_RemainsValidAcrossSameDirectoryRename()
    {
        var directory = NewTempDir();
        try
        {
            var source = Path.Combine(directory, "staged-candidate");
            var destination = Path.Combine(directory, "installed-candidate");
            File.WriteAllBytes(source, BuildElf());
            var proof = LinuxUpdateSwap.Capture(source);

            File.Move(source, destination);

            Assert.True(LinuxUpdateSwap.Matches(proof, destination));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Swap_StagedOwnershipProofStillIdentifiesTheInstalledFile()
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());

            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Stage();
            var stagedProof = LinuxUpdateSwap.Capture(staged);
            Exception? executeError = null;
            try { swap.Execute(); } catch (Exception ex) { executeError = ex; }
            var installedProof = LinuxUpdateSwap.Capture(current);

            Assert.True(LinuxUpdateSwap.Matches(stagedProof, current));
            Assert.Equal(stagedProof.Sha256, installedProof.Sha256);
            Assert.Equal(stagedProof.Identity, installedProof.Identity);
            Assert.Equal(stagedProof.Mode, installedProof.Mode);
            Assert.Equal(stagedProof.OwnerUid, installedProof.OwnerUid);
            Assert.Null(executeError);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Swap_RollbackPreservesConcurrentReplacementAndOldBackup()
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());

            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Execute();
            File.WriteAllText(current, "concurrent replacement");

            var error = swap.Rollback(childExitConfirmed: true);

            Assert.NotNull(error);
            Assert.Equal("concurrent replacement", File.ReadAllText(current));
            Assert.Equal("old-bundle", File.ReadAllText(old));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RollbackKeepsInstalledCandidateWhenBackupWasSubstituted()
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());
            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Execute();
            File.WriteAllText(old, "foreign backup replacement");

            var guidance = swap.Rollback(childExitConfirmed: true);

            Assert.NotNull(guidance);
            Assert.Equal(BuildElf(), File.ReadAllBytes(current));
            Assert.Equal("foreign backup replacement", File.ReadAllText(old));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Swap_RejectsStagedPayloadMutationBeforeMovingInstalledFile()
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());
            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Stage();
            File.WriteAllBytes(staged, Encoding.UTF8.GetBytes("replaced after probe"));

            Assert.Throws<IOException>(() => swap.Execute());

            Assert.Equal("old-bundle", File.ReadAllText(current));
            Assert.False(File.Exists(old));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DownloadCleanupRemovesOnlyKnownFileAndLeavesUnknownConcurrentInsertions()
    {
        var root = NewTempDir();
        try
        {
            var staging = Path.Combine(root, "update-0123456789abcdef");
            Directory.CreateDirectory(staging);
            var known = Path.Combine(staging, "OpenCodeAccountManager-linux-x64");
            var unknown = Path.Combine(staging, "concurrent-insertion.txt");
            File.WriteAllText(known, "verified bytes");
            var proof = LinuxUpdateSwap.Capture(known);
            File.WriteAllText(unknown, "leave me");

            MainWindowViewModel.CleanupDownloadedArtifact(root, known, proof);

            Assert.False(File.Exists(known));
            Assert.Equal("leave me", File.ReadAllText(unknown));
            Assert.True(Directory.Exists(staging));
            Assert.True(Directory.Exists(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DownloadCleanupPreservesAnEmptyDirectorySubstitutedAtTheRememberedPath()
    {
        var parent = NewTempDir();
        try
        {
            var remembered = Path.Combine(parent, "download-root");
            var displaced = Path.Combine(parent, "displaced-original");
            Directory.CreateDirectory(remembered);
            var proof = LinuxUpdateSwap.CaptureDirectory(remembered);
            Directory.Move(remembered, displaced); // Keep the original identity alive to prevent reuse.
            Directory.CreateDirectory(remembered); // A different empty directory at the same name.

            MainWindowViewModel.CleanupDownloadedArtifact(remembered, file: null, proof: null, rootProof: proof);

            Assert.True(Directory.Exists(remembered));
            Assert.Empty(Directory.GetFileSystemEntries(remembered));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void DownloadCleanupRemovesOnlyTheCapturedEmptyStagingDirectories()
    {
        var parent = NewTempDir();
        try
        {
            var root = Path.Combine(parent, "download-root");
            var staging = Path.Combine(root, "update-0123456789abcdef0123456789abcdef");
            Directory.CreateDirectory(staging);
            var rootProof = LinuxUpdateSwap.CaptureDirectory(root);
            var stagingProof = LinuxUpdateSwap.CaptureDirectory(staging);
            var file = Path.Combine(staging, "OpenCodeAccountManager-linux-x64");
            File.WriteAllText(file, "known bytes");
            var fileProof = LinuxUpdateSwap.Capture(file);

            MainWindowViewModel.CleanupDownloadedArtifact(root, file, fileProof, rootProof, stagingProof);

            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(staging));
            Assert.False(Directory.Exists(root));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task RequiredPreSwapAssemblyFailureStopsTheSwapAndLaunch()
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());
            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Stage();
            var launched = false;

            var failure = await Record.ExceptionAsync(() => LinuxSelfUpdater.RunSwapAndAwaitReadyAsync(
                swap,
                () =>
                {
                    launched = true;
                    return Task.CompletedTask;
                },
                childExitConfirmed: () => true,
                prepareBeforeSwap: () => LinuxSelfUpdater.PrepareRequiredAssemblies(_ => throw new IOException("synthetic loader failure"))));
            if (failure is null) { _ = swap.Rollback(childExitConfirmed: true); }

            Assert.IsType<InvalidOperationException>(failure);
            Assert.Contains("Linux 更新依赖准备失败", failure!.Message);
            Assert.False(launched);
            Assert.Equal("old-bundle", File.ReadAllText(current));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ProbeSetupFailureAfterStartTerminatesAndDisposesTheOwnedProcess()
    {
        var process = new FakeProbeProcess
        {
            ReadOutput = (_, _) => throw new IOException("synthetic redirected-stream setup failure"),
            Stop = () => Task.FromResult(true),
        };
        try
        {
            var failure = await Record.ExceptionAsync(() => LinuxUpdateProbeRunner.RunAsync(
                _ => process, 1000, CancellationToken.None));

            Assert.IsType<LinuxProbeExecutionException>(failure);
            Assert.True(process.StopCalled);
            Assert.True(process.Disposed);
        }
        finally { process.Dispose(); }
    }

    [Fact]
    public async Task ProbeReaderSecondAccessorFailureDisposesAndObservesTheFirstReader()
    {
        var stdout = ControlledTextReader.Pending();
        using var readers = new LinuxUpdateProbeOutputReader(
            () => stdout,
            () => throw new IOException("synthetic second-reader accessor failure"));

        var read = readers.ReadAsync(128, CancellationToken.None);
        await stdout.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var failure = await Record.ExceptionAsync(() => read);
        try
        {
            Assert.IsType<IOException>(failure);
            Assert.True(stdout.Disposed);
            Assert.True(stdout.ReadCompleted.Task.IsCompleted);
            Assert.True(readers.ReaderTasksObserved);
        }
        finally { stdout.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeReaderFailureOrOverflowCancelsDisposesAndObservesPendingPeer(bool overflow)
    {
        var stdout = overflow
            ? new ControlledTextReader("output exceeds limit")
            : new ControlledTextReader(failure: new IOException("synthetic stdout read failure"));
        var stderr = ControlledTextReader.Pending();
        using var readers = new LinuxUpdateProbeOutputReader(() => stdout, () => stderr);

        var read = readers.ReadAsync(4, CancellationToken.None);
        await stderr.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            if (overflow)
            {
                var output = await read;
                Assert.Null(output.StandardOutput);
            }
            else
            {
                Assert.IsType<IOException>(await Record.ExceptionAsync(() => read));
            }

            Assert.True(stderr.Disposed);
            Assert.True(stderr.ReadCompleted.Task.IsCompleted);
            Assert.True(readers.ReaderTasksObserved);
        }
        finally
        {
            stdout.Dispose();
            stderr.Dispose();
        }
    }

    [Fact]
    public async Task ProbeReaderPositivePathReturnsBothBoundedStreamsAndClosesBothReaders()
    {
        var stdout = new ControlledTextReader("{\"probe\":true}");
        var stderr = new ControlledTextReader(string.Empty);
        using var readers = new LinuxUpdateProbeOutputReader(() => stdout, () => stderr);

        var output = await readers.ReadAsync(128, CancellationToken.None);

        Assert.Equal("{\"probe\":true}", output.StandardOutput);
        Assert.Equal(string.Empty, output.StandardError);
        Assert.True(readers.ReaderTasksObserved);
        Assert.True(stdout.Disposed);
        Assert.True(stderr.Disposed);
    }

    [Fact]
    public async Task ProbeOutputOverflowStopsOwnedProcessWithoutWaitingForItsExit()
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProbeProcess
        {
            ReadOutput = (_, _) => Task.FromResult(new LinuxProbeOutput(null, string.Empty)),
            WaitForExit = token => exit.Task.WaitAsync(token),
            Stop = () => Task.FromResult(true),
        };
        try
        {
            var run = LinuxUpdateProbeRunner.RunAsync(_ => process, 1000, CancellationToken.None);
            await Task.Delay(60);
            var stoppedBeforeExit = process.StopCalled;
            exit.TrySetResult(0);
            _ = await Record.ExceptionAsync(() => run);

            Assert.True(stoppedBeforeExit);
        }
        finally
        {
            exit.TrySetResult(0);
            process.Dispose();
        }
    }

    [Fact]
    public async Task ProbeReadFailureStopsOwnedProcessBeforeItsExit()
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProbeProcess
        {
            ReadOutput = (_, _) => Task.FromException<LinuxProbeOutput>(new IOException("synthetic pipe read failure")),
            WaitForExit = token => exit.Task.WaitAsync(token),
            Stop = () => Task.FromResult(true),
        };
        try
        {
            var run = LinuxUpdateProbeRunner.RunAsync(_ => process, 1000, CancellationToken.None);
            await Task.Delay(50);
            var stoppedBeforeExit = process.StopCalled;
            exit.TrySetResult(0);
            _ = await Record.ExceptionAsync(() => run);

            Assert.True(stoppedBeforeExit);
        }
        finally
        {
            exit.TrySetResult(0);
            process.Dispose();
        }
    }

    [Fact]
    public async Task ProbeRejectsOtherwiseValidOutputThatCompletesAtItsDeadline()
    {
        long now = 0;
        var process = new FakeProbeProcess
        {
            ReadOutput = (_, _) =>
            {
                now = 50;
                return Task.FromResult(new LinuxProbeOutput("{}", string.Empty));
            },
            WaitForExit = _ => Task.FromResult(0),
            Stop = () => Task.FromResult(true),
        };
        try
        {
            var failure = await Record.ExceptionAsync(() => LinuxUpdateProbeRunner.RunAsync(
                _ => process, 50, CancellationToken.None, () => now));

            Assert.IsType<LinuxProbeExecutionException>(failure);
            Assert.True(process.Disposed);
        }
        finally { process.Dispose(); }
    }

    [Fact]
    public async Task UnconfirmedProbeExitPreventsStagedCandidateCleanup()
    {
        var directory = NewTempDir();
        var process = new FakeProbeProcess
        {
            ReadOutput = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new LinuxProbeOutput("{}", string.Empty);
            },
            WaitForExit = token => Task.Delay(Timeout.Infinite, token).ContinueWith<int>(
                _ => 1, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default),
            Stop = () => Task.FromResult(false),
        };
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());
            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Stage();
            using var cancellation = new CancellationTokenSource(40);
            var failure = await Record.ExceptionAsync(() => LinuxUpdateProbeRunner.RunAsync(
                _ => process, 1000, cancellation.Token));
            var probeFailure = Assert.IsType<LinuxProbeExecutionException>(failure);

            var rollbackGuidance = swap.Rollback(LinuxSelfUpdater.CanRollbackAfterProbe(probeFailure));

            Assert.False(probeFailure.ProcessExitConfirmed);
            Assert.NotNull(rollbackGuidance);
            Assert.True(File.Exists(staged));
            Assert.Equal("old-bundle", File.ReadAllText(current));
        }
        finally
        {
            process.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeTimeoutCancelsAndObservesPendingOutputAndExitTasks()
    {
        var process = new FakeProbeProcess
        {
            ReadOutput = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new LinuxProbeOutput("{}", string.Empty);
            },
            WaitForExit = async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return 1;
            },
            Stop = () => Task.FromResult(true),
        };
        try
        {
            var failure = await Record.ExceptionAsync(() => LinuxUpdateProbeRunner.RunAsync(
                _ => process, 40, CancellationToken.None));

            Assert.IsType<LinuxProbeExecutionException>(failure);
            Assert.True(process.OutputTask!.IsCompleted);
            Assert.True(process.Disposed);
        }
        finally { process.Dispose(); }
    }

    [Fact]
    public async Task ProbeStillAcceptsBoundedOutputWhenTheOwnedProcessExitsCleanly()
    {
        var process = new FakeProbeProcess
        {
            ReadOutput = (_, _) => Task.FromResult(new LinuxProbeOutput("{\"probe\":true}", string.Empty)),
            WaitForExit = _ => Task.FromResult(0),
            Stop = () => Task.FromResult(true),
        };
        try
        {
            var output = await LinuxUpdateProbeRunner.RunAsync(_ => process, 1000, CancellationToken.None);
            Assert.Equal("{\"probe\":true}", output);
        }
        finally { process.Dispose(); }
    }

    [Fact]
    public async Task ProbeDoesNotAcceptCompletedOutputIfTheCallerCancelsDuringItsCallback()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProbeProcess
        {
            ReadOutput = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new LinuxProbeOutput("{}", string.Empty));
            },
            WaitForExit = _ => Task.FromResult(0),
            Stop = () => Task.FromResult(true),
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LinuxUpdateProbeRunner.RunAsync(
                _ => process, 1000, cancellation.Token));
            Assert.True(process.StopCalled);
            Assert.True(process.Disposed);
        }
        finally { process.Dispose(); }
    }

    [Fact]
    public void Swap_RollbackDoesNotTouchFilesUntilChildExitIsConfirmed()
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());

            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Execute();
            var error = swap.Rollback(childExitConfirmed: false);

            Assert.NotNull(error);
            Assert.Equal(BuildElf(), File.ReadAllBytes(current));
            Assert.Equal("old-bundle", File.ReadAllText(old));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoverLaunchFailureOrReadinessTimeoutRestoresTheOriginalBundle(bool timeout)
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());
            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Stage();

            await Assert.ThrowsAsync<InvalidOperationException>(() => LinuxSelfUpdater.RunSwapAndAwaitReadyAsync(
                swap,
                () =>
                {
                    Assert.Equal(BuildElf(), File.ReadAllBytes(current));
                    throw timeout ? new TimeoutException("simulated 20 second readiness timeout") : new IOException("simulated process launch failure");
                },
                childExitConfirmed: () => true));

            Assert.Equal("old-bundle", File.ReadAllText(current));
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(staged));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void BackupCleanupRequiresTheOriginalHashAndIdentityProof()
    {
        var directory = NewTempDir();
        try
        {
            var current = Path.Combine(directory, "OpenCodeAccountManager");
            var download = Path.Combine(directory, "verified-download");
            var staged = Path.Combine(directory, ".OpenCodeAccountManager.new-0123456789abcdef0123456789abcdef");
            var old = Path.Combine(directory, "OpenCodeAccountManager.old-0123456789abcdef0123456789abcdef");
            File.WriteAllText(current, "old-bundle");
            File.WriteAllBytes(download, BuildElf());
            var swap = new LinuxUpdateSwap(download, current, staged, old);
            swap.Execute();

            File.WriteAllText(old, "same-name-but-not-the-original-backup");

            Assert.False(LinuxUpdateSwap.TryCleanupOwnedBackup(old, swap.BackupProof));
            Assert.True(File.Exists(old));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FakeProbeProcess : ILinuxUpdateProbeProcess
    {
        public Func<int, CancellationToken, Task<LinuxProbeOutput>> ReadOutput { get; init; }
            = (_, _) => Task.FromResult(new LinuxProbeOutput("{}", string.Empty));
        public Func<CancellationToken, Task<int>> WaitForExit { get; init; }
            = _ => Task.FromResult(0);
        public Func<Task<bool>> Stop { get; init; } = () => Task.FromResult(true);
        public Task<LinuxProbeOutput>? OutputTask { get; private set; }
        public bool StopCalled { get; private set; }
        public bool Disposed { get; private set; }

        public Task<LinuxProbeOutput> StartReadingOutputAsync(int maximumCharacters, CancellationToken cancellationToken)
        {
            OutputTask = ReadOutput(maximumCharacters, cancellationToken);
            return OutputTask;
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => WaitForExit(cancellationToken);

        public Task<bool> StopAndConfirmExitAsync()
        {
            StopCalled = true;
            return Stop();
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class ControlledTextReader : TextReader
    {
        private readonly string _content;
        private readonly Exception? _failure;
        private readonly bool _pending;
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;

        public ControlledTextReader(string content) => _content = content;

        public ControlledTextReader(Exception failure) : this(string.Empty) => _failure = failure;

        private ControlledTextReader() : this(string.Empty) => _pending = true;

        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public static ControlledTextReader Pending() => new();

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);
            try
            {
                if (_pending) { await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                if (_failure is not null) { throw _failure; }
                var count = Math.Min(buffer.Length, _content.Length - _offset);
                if (count == 0) { return 0; }
                _content.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            finally { ReadCompleted.TrySetResult(true); }
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _release.TrySetResult(true);
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingWriteStream(bool blockWrite = true, bool blockFlush = false) : Stream
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FlushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseWrite() => _release.TrySetResult(true);
        public void ReleaseFlush() => _releaseFlush.TrySetResult(true);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!blockFlush) { return; }
            FlushStarted.TrySetResult(true);
            await _releaseFlush.Task.WaitAsync(cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult(true);
            if (blockWrite) { await _release.Task.WaitAsync(cancellationToken); }
        }
    }

    private sealed class LateEofStream(byte[] bytes, Action onEof) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count == 0) { onEof(); }
            return count;
        }
    }
}

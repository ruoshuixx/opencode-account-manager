using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// Covers the pure, side-effect-free parts of the update hand-over: argument classification,
/// path constraints, version/PE checks and file-swap compensation. No real executable,
/// account config, log or backup is touched; every file lives in a throwaway temp directory.
/// </summary>
public class SelfUpdaterTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "am-selfupdater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Hex32() => Guid.NewGuid().ToString("N");

    // Protocol literals, intentionally duplicated from the implementation so a silent rename
    // cannot make the handshake pass unnoticed.
    private static string[] ValidArgs(int pid, long startTicks, string eventName, string oldFileName) => new[]
    {
        "--wait-for-pid", pid.ToString(CultureInfo.InvariantCulture),
        "--wait-for-start", startTicks.ToString(CultureInfo.InvariantCulture),
        "--update-ready-event", eventName,
        "--cleanup-old", oldFileName,
    };

    private static byte[] BuildSyntheticPe(ushort machine)
    {
        var bytes = new byte[0x100];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80u).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    [Fact]
    public void IsSupported_IsFalseForTheTestHostWhichIsNotAPortableSingleFile()
    {
        // xunit runs under testhost/apphost, never the self-contained single-file build.
        Assert.False(SelfUpdater.IsSupported);
    }

    [Fact]
    public async Task PrepareAndLaunchAsync_RefusesToRunWhenUnsupported()
    {
        // Must not start a second UI or touch anything when the build is not the portable exe.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SelfUpdater.PrepareAndLaunchAsync(
            Path.Combine(Path.GetTempPath(), "does-not-matter.exe"), new Version(1, 0, 0)));
        var expectedGuidance = OperatingSystem.IsLinux() ? "Linux 自动升级" : "自更新仅支持";
        Assert.Contains(expectedGuidance, ex.Message);
    }

    [Fact]
    public void ClassifyUpdateArgs_EmptyOrUnrelatedArgumentsAreNormalStartup()
    {
        Assert.Equal(SelfUpdater.UpdateLaunchKind.NotUpdate, SelfUpdater.ClassifyUpdateArgs(Array.Empty<string>(), out var parsed));
        Assert.Null(parsed);
        Assert.Equal(SelfUpdater.UpdateLaunchKind.NotUpdate, SelfUpdater.ClassifyUpdateArgs(null, out _));
        Assert.Equal(SelfUpdater.UpdateLaunchKind.NotUpdate, SelfUpdater.ClassifyUpdateArgs(new[] { "some-file.txt", "-v" }, out _));
    }

    [Fact]
    public void ClassifyUpdateArgs_ValidHandshakeParsesEveryField()
    {
        var evt = SelfUpdater.ReadyEventPrefix + Hex32();
        var oldName = "OpenCodeAccountManager.old-" + Hex32() + ".exe";

        var kind = SelfUpdater.ClassifyUpdateArgs(ValidArgs(4321, 638000000000000000L, evt, oldName), out var parsed);

        Assert.Equal(SelfUpdater.UpdateLaunchKind.Valid, kind);
        Assert.NotNull(parsed);
        Assert.Equal(4321, parsed!.ParentPid);
        Assert.Equal(638000000000000000L, parsed.ParentStartUtcTicks);
        Assert.Equal(evt, parsed.ReadyEventName);
        Assert.Equal(oldName, parsed.CleanupOldFileName);
    }

    [Fact]
    public void ClassifyUpdateArgs_MissingAnyFlagIsInvalid()
    {
        var full = ValidArgs(1, 2, SelfUpdater.ReadyEventPrefix + Hex32(), "App.old-" + Hex32() + ".exe");

        for (var skip = 0; skip < full.Length; skip += 2)
        {
            var partial = new List<string>(full);
            partial.RemoveRange(skip, 2);
            Assert.Equal(SelfUpdater.UpdateLaunchKind.Invalid, SelfUpdater.ClassifyUpdateArgs(partial.ToArray(), out _));
        }

        Assert.Equal(SelfUpdater.UpdateLaunchKind.Invalid, SelfUpdater.ClassifyUpdateArgs(new[] { "--wait-for-pid" }, out _));
    }

    [Fact]
    public void ClassifyUpdateArgs_DuplicateUnknownAndBadValuesAreInvalid()
    {
        var evt = SelfUpdater.ReadyEventPrefix + Hex32();
        var good = "App.old-" + Hex32() + ".exe";

        var duplicate = new List<string>(ValidArgs(1, 2, evt, good)) { "--wait-for-start", "3" };
        Assert.Equal(SelfUpdater.UpdateLaunchKind.Invalid, SelfUpdater.ClassifyUpdateArgs(duplicate.ToArray(), out _));

        var unknown = new List<string>(ValidArgs(1, 2, evt, good)) { "--unexpected", "x" };
        Assert.Equal(SelfUpdater.UpdateLaunchKind.Invalid, SelfUpdater.ClassifyUpdateArgs(unknown.ToArray(), out _));

        Assert.Equal(SelfUpdater.UpdateLaunchKind.Invalid, SelfUpdater.ClassifyUpdateArgs(ValidArgs(0, 2, evt, good), out _));
        Assert.Equal(SelfUpdater.UpdateLaunchKind.Invalid, SelfUpdater.ClassifyUpdateArgs(ValidArgs(1, 0, evt, good), out _));
        Assert.Equal(SelfUpdater.UpdateLaunchKind.Invalid, SelfUpdater.ClassifyUpdateArgs(ValidArgs(1, 2, "Local\\not-ours", good), out _));
    }

    [Fact]
    public void IsValidReadyEventName_OnlyAcceptsTheGeneratedShape()
    {
        Assert.True(SelfUpdater.IsValidReadyEventName(SelfUpdater.ReadyEventPrefix + Hex32()));
        Assert.False(SelfUpdater.IsValidReadyEventName("Local\\SomethingElse"));
        Assert.False(SelfUpdater.IsValidReadyEventName(SelfUpdater.ReadyEventPrefix + "xyz"));
        Assert.False(SelfUpdater.IsValidReadyEventName(SelfUpdater.ReadyEventPrefix + Hex32() + "0"));
        Assert.False(SelfUpdater.IsValidReadyEventName(SelfUpdater.ReadyEventPrefix + new string('z', 32)));
        Assert.False(SelfUpdater.IsValidReadyEventName(null));
    }

    [Fact]
    public void IsValidCleanupOldFileName_RequiresOwnBareBackupName()
    {
        var dir = NewTempDir();
        try
        {
            var own = Path.Combine(dir, "OpenCodeAccountManager.exe");
            var valid = "OpenCodeAccountManager.old-" + Hex32() + ".exe";

            Assert.True(SelfUpdater.IsValidCleanupOldFileName(valid, own));

            // Wrong base name, wrong extension, non-hex or short id are all refused.
            Assert.False(SelfUpdater.IsValidCleanupOldFileName("OtherApp.old-" + Hex32() + ".exe", own));
            Assert.False(SelfUpdater.IsValidCleanupOldFileName("OpenCodeAccountManager.old-" + Hex32() + ".dll", own));
            Assert.False(SelfUpdater.IsValidCleanupOldFileName(
                "OpenCodeAccountManager.old-" + new string('z', 32) + ".exe", own));
            Assert.False(SelfUpdater.IsValidCleanupOldFileName(
                "OpenCodeAccountManager.old-" + Hex32().Substring(0, 31) + ".exe", own));

            // Arbitrary paths are refused: relative, absolute, traversal.
            Assert.False(SelfUpdater.IsValidCleanupOldFileName("sub\\" + valid, own));
            Assert.False(SelfUpdater.IsValidCleanupOldFileName(Path.Combine(dir, valid), own));
            Assert.False(SelfUpdater.IsValidCleanupOldFileName("..\\" + valid, own));
            Assert.False(SelfUpdater.IsValidCleanupOldFileName(null, own));
            Assert.False(SelfUpdater.IsValidCleanupOldFileName(string.Empty, own));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TryCleanupBackup_DeletesOnlyTheSpecificBackup()
    {
        var dir = NewTempDir();
        try
        {
            var own = Path.Combine(dir, "OpenCodeAccountManager.exe");
            File.WriteAllText(own, "host");

            var mine = "OpenCodeAccountManager.old-" + Hex32() + ".exe";
            var other = "SomeOther.old-" + Hex32() + ".exe";
            File.WriteAllText(Path.Combine(dir, mine), "old-mine");
            File.WriteAllText(Path.Combine(dir, other), "old-other");

            Assert.True(SelfUpdater.TryCleanupBackup(mine, own));
            Assert.False(File.Exists(Path.Combine(dir, mine)));
            Assert.True(File.Exists(Path.Combine(dir, other))); // never touched

            // A traversal name is refused and deletes nothing.
            Assert.False(SelfUpdater.TryCleanupBackup("..\\" + mine, own));

            // A normal launch has nothing pending: CompleteStartup is a no-op, never deletes.
            SelfUpdater.CompleteStartup();
            Assert.True(File.Exists(Path.Combine(dir, other)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void VersionMatches_IgnoresComponentsTheExpectedVersionDoesNotSpecify()
    {
        Assert.True(SelfUpdater.VersionMatches("1.2.3", new Version(1, 2, 3)));
        Assert.True(SelfUpdater.VersionMatches("1.2.3.0", new Version(1, 2, 3)));
        Assert.True(SelfUpdater.VersionMatches("1.2.3.4", new Version(1, 2, 3, 4)));
        Assert.False(SelfUpdater.VersionMatches("1.2.4.0", new Version(1, 2, 3)));
        Assert.False(SelfUpdater.VersionMatches("2.2.3", new Version(1, 2, 3)));
        Assert.False(SelfUpdater.VersionMatches("1.2.3.5", new Version(1, 2, 3, 4)));
        Assert.False(SelfUpdater.VersionMatches(null, new Version(1, 2, 3)));
        Assert.False(SelfUpdater.VersionMatches("not-a-version", new Version(1, 2, 3)));
    }

    [Fact]
    public void TryReadPeMachine_ReadsMachineAndRejectsNonPeFiles()
    {
        var dir = NewTempDir();
        try
        {
            var x64 = Path.Combine(dir, "x64.exe");
            File.WriteAllBytes(x64, BuildSyntheticPe(SelfUpdater.PeMachineAmd64));
            Assert.True(SelfUpdater.TryReadPeMachine(x64, out var machine));
            Assert.Equal(SelfUpdater.PeMachineAmd64, machine);

            var x86 = Path.Combine(dir, "x86.exe");
            File.WriteAllBytes(x86, BuildSyntheticPe(0x014C));
            Assert.True(SelfUpdater.TryReadPeMachine(x86, out var x86Machine));
            Assert.Equal((ushort)0x014C, x86Machine);

            var junk = Path.Combine(dir, "junk.bin");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });
            Assert.False(SelfUpdater.TryReadPeMachine(junk, out _));

            Assert.False(SelfUpdater.TryReadPeMachine(Path.Combine(dir, "missing.exe"), out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExeSwapTransaction_ExecuteReplacesExeAndKeepsOldBackup()
    {
        var dir = NewTempDir();
        try
        {
            var current = Path.Combine(dir, "App.exe");
            var download = Path.Combine(dir, "download.exe");
            var newTemp = Path.Combine(dir, "App.new-" + Hex32() + ".exe");
            var old = Path.Combine(dir, "App.old-" + Hex32() + ".exe");
            File.WriteAllText(current, "OLD");
            File.WriteAllText(download, "NEW");

            var swap = new SelfUpdater.ExeSwapTransaction(download, current, newTemp, old);
            swap.Execute();

            Assert.True(swap.OldMoved);
            Assert.True(swap.NewMovedIntoPlace);
            Assert.Equal("NEW", File.ReadAllText(current));
            Assert.Equal("OLD", File.ReadAllText(old));
            Assert.False(File.Exists(newTemp));
            Assert.True(File.Exists(download)); // the caller's verified download is never consumed
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExeSwapTransaction_FaultAfterRenameRollbackRestoresOldAndCleansStagedFile()
    {
        var dir = NewTempDir();
        try
        {
            var current = Path.Combine(dir, "App.exe");
            var download = Path.Combine(dir, "download.exe");
            var newTemp = Path.Combine(dir, "App.new-" + Hex32() + ".exe");
            var old = Path.Combine(dir, "App.old-" + Hex32() + ".exe");
            File.WriteAllText(current, "OLD");
            File.WriteAllText(download, "NEW");

            var swap = new SelfUpdater.ExeSwapTransaction(download, current, newTemp, old);
            Assert.Throws<InvalidOperationException>(() => swap.Execute(
                () => throw new InvalidOperationException("simulated swap failure")));
            Assert.True(swap.OldMoved);
            Assert.False(swap.NewMovedIntoPlace);

            var error = swap.Rollback(process: null);

            Assert.Null(error);
            Assert.Equal("OLD", File.ReadAllText(current));
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(newTemp));
            Assert.True(File.Exists(download));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExeSwapTransaction_RollbackAfterLaunchFailureRestoresOldExe()
    {
        var dir = NewTempDir();
        try
        {
            var current = Path.Combine(dir, "App.exe");
            var download = Path.Combine(dir, "download.exe");
            var newTemp = Path.Combine(dir, "App.new-" + Hex32() + ".exe");
            var old = Path.Combine(dir, "App.old-" + Hex32() + ".exe");
            File.WriteAllText(current, "OLD");
            File.WriteAllText(download, "NEW-BROKEN");

            var swap = new SelfUpdater.ExeSwapTransaction(download, current, newTemp, old);
            swap.Execute(); // current = NEW-BROKEN, old = OLD

            var error = swap.Rollback(process: null);

            Assert.Null(error);
            Assert.Equal("OLD", File.ReadAllText(current));
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(newTemp));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExeSwapTransaction_RollbackBeforeRenameOnlyRemovesStagedFile()
    {
        var dir = NewTempDir();
        try
        {
            var current = Path.Combine(dir, "App.exe"); // deliberately absent: the rename step fails
            var download = Path.Combine(dir, "download.exe");
            var newTemp = Path.Combine(dir, "App.new-" + Hex32() + ".exe");
            var old = Path.Combine(dir, "App.old-" + Hex32() + ".exe");
            File.WriteAllText(download, "NEW");

            var swap = new SelfUpdater.ExeSwapTransaction(download, current, newTemp, old);
            Assert.ThrowsAny<IOException>(() => swap.Execute());
            Assert.False(swap.OldMoved);
            Assert.False(swap.NewMovedIntoPlace);

            var error = swap.Rollback(process: null);

            Assert.Null(error);
            Assert.False(File.Exists(newTemp));
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(current));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExeSwapTransaction_RollbackKeepsBackupWhenTargetIsNotAttributable()
    {
        var dir = NewTempDir();
        try
        {
            var current = Path.Combine(dir, "App.exe");
            var download = Path.Combine(dir, "download.exe");
            var newTemp = Path.Combine(dir, "App.new-" + Hex32() + ".exe");
            var old = Path.Combine(dir, "App.old-" + Hex32() + ".exe");
            File.WriteAllText(current, "OLD");
            File.WriteAllText(download, "NEW");

            var swap = new SelfUpdater.ExeSwapTransaction(download, current, newTemp, old);
            // An unattributable file appears at the target, so the final move must fail.
            Assert.ThrowsAny<IOException>(() => swap.Execute(() => File.WriteAllText(current, "INTRUDER")));

            var error = swap.Rollback(process: null);

            Assert.NotNull(error);
            Assert.Equal("INTRUDER", File.ReadAllText(current)); // never overwritten
            Assert.Equal("OLD", File.ReadAllText(old));          // backup kept for manual recovery
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExeSwapTransaction_RollbackKeepsEverythingWhenChildExitCannotBeConfirmed()
    {
        var dir = NewTempDir();
        try
        {
            var current = Path.Combine(dir, "App.exe");
            var download = Path.Combine(dir, "download.exe");
            var newTemp = Path.Combine(dir, "App.new-" + Hex32() + ".exe");
            var old = Path.Combine(dir, "App.old-" + Hex32() + ".exe");
            File.WriteAllText(current, "OLD");
            File.WriteAllText(download, "NEW");

            var swap = new SelfUpdater.ExeSwapTransaction(download, current, newTemp, old);
            swap.Execute(); // current = NEW, old = OLD

            // A disposed Process cannot be verified or controlled: rollback must abort and keep files.
            var process = new Process();
            process.Dispose();

            var error = swap.Rollback(process);

            Assert.NotNull(error);
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(old));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void HandleStartup_NoUpdateArgumentsContinueNormalStartup()
    {
        Assert.True(SelfUpdater.HandleStartup(Array.Empty<string>()));
        Assert.True(SelfUpdater.HandleStartup(new[] { "unrelated-argument" }));
    }

    [Fact]
    public void HandleStartup_MalformedUpdateArgumentsAbortWithoutStartingUi()
    {
        Assert.False(SelfUpdater.HandleStartup(new[] { "--wait-for-pid", "123" }));
        Assert.False(SelfUpdater.HandleStartup(new[] { "--update-ready-event", "Local\\nope" }));
        Assert.False(SelfUpdater.HandleStartup(new[] { "--cleanup-old", "App.old-" + Hex32() + ".exe" }));
    }

    [Theory]
    [InlineData("--wait-for-pid-extra")]
    [InlineData("--update-ready-event-extra")]
    [InlineData("--cleanup-old-extra")]
    [InlineData("--update-probe-extra")]
    public void HandleStartup_RejectsUnknownReservedUpdateArguments(string reservedArgument)
        => Assert.False(SelfUpdater.HandleStartup(new[] { reservedArgument, "value" }));

    [Fact]
    public void HandleStartup_ValidShapeOnUnsupportedBuildAborts()
    {
        // Syntactically valid handshake, but this test host is not the portable single-file build,
        // so the managed entry must refuse to continue (and never open an arbitrary parent/event).
        var ownExe = Environment.ProcessPath ?? "App.exe";
        var baseName = Path.GetFileNameWithoutExtension(ownExe);
        if (string.IsNullOrEmpty(baseName)) { baseName = "App"; }

        var args = ValidArgs(
            int.MaxValue,
            1,
            SelfUpdater.ReadyEventPrefix + Hex32(),
            baseName + ".old-" + Hex32() + ".exe");

        Assert.False(SelfUpdater.HandleStartup(args));
    }
}

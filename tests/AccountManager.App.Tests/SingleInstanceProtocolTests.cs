using System.Text;
using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// The Linux recovery channel accepts one exact, bounded message and only from the same effective
/// user. These tests pin the literal framing and UID decisions; the Windows facade lifecycle tests
/// below use the existing named event with an injected synchronous dispatcher, so no Avalonia
/// dispatcher or real window is needed. No socket, tray or account data is touched.
/// </summary>
public class SingleInstanceProtocolTests
{
    private static readonly byte[] ShowBytes = { (byte)'s', (byte)'h', (byte)'o', (byte)'w' };

    [Fact]
    public void OnlyTheExactFourAsciiShowBytesAreAValidActivation()
    {
        Assert.True(SingleInstanceProtocol.IsValidActivation(ShowBytes));
        Assert.Equal(ShowBytes, SingleInstanceProtocol.ShowMessage.ToArray());
    }

    [Fact]
    public void OnlyTheExactPositiveShownAckIsAccepted()
    {
        Assert.Equal(new byte[] { (byte)'s', (byte)'h', (byte)'o', (byte)'w', (byte)'n' }, SingleInstanceProtocol.AckMessage.ToArray());
        Assert.True(SingleInstanceProtocol.IsValidAck(SingleInstanceProtocol.AckMessage));

        // EOF with no ack, the request bytes, a case variant and any trailing byte are all failures.
        Assert.False(SingleInstanceProtocol.IsValidAck(ReadOnlySpan<byte>.Empty));
        Assert.False(SingleInstanceProtocol.IsValidAck(ShowBytes));
        Assert.False(SingleInstanceProtocol.IsValidAck(Encoding.ASCII.GetBytes("SHOWN")));
        Assert.False(SingleInstanceProtocol.IsValidAck(Encoding.ASCII.GetBytes("shown\n")));
    }

    [Fact]
    public void ShowFollowedByAnyExtraByteIsRejected()
    {
        // Complete framing: a prefix "show" plus arbitrary trailing bytes is not the fixed request.
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[] { (byte)'s', (byte)'h', (byte)'o', (byte)'w', (byte)'\n' }));
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[] { (byte)'s', (byte)'h', (byte)'o', (byte)'w', (byte)' ' }));
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[] { (byte)'s', (byte)'h', (byte)'o', (byte)'w', 0 }));
        // A duplicate launch message is "showshow": still just a prefix with trailing bytes.
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[] { (byte)'s', (byte)'h', (byte)'o', (byte)'w', (byte)'s', (byte)'h', (byte)'o', (byte)'w' }));
    }

    [Fact]
    public void EmptyTruncatedWrongCaseAndNonAsciiPayloadsAreRejected()
    {
        Assert.False(SingleInstanceProtocol.IsValidActivation(ReadOnlySpan<byte>.Empty));
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[] { (byte)'s', (byte)'h', (byte)'o' }));
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[] { (byte)'S', (byte)'H', (byte)'O', (byte)'W' }));
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[] { (byte)'S', (byte)'h', (byte)'o', (byte)'w' }));
        // Cyrillic 'о' is two UTF-8 bytes, so this is not the four ASCII bytes.
        Assert.False(SingleInstanceProtocol.IsValidActivation(Encoding.UTF8.GetBytes("sh\u043ew")));
    }

    [Fact]
    public void PayloadsLongerThanTheFourByteRequestAreRejected()
    {
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[128]));
        Assert.False(SingleInstanceProtocol.IsValidActivation(new byte[129]));

        var showPlusPadding = new byte[128];
        ShowBytes.CopyTo(showPlusPadding, 0);
        Assert.False(SingleInstanceProtocol.IsValidActivation(showPlusPadding));
    }

    [Theory]
    [InlineData(1000L, 1000L, true)]
    [InlineData(0L, 0L, true)]
    [InlineData(65534L, 65534L, true)]
    [InlineData(1000L, 1001L, false)]
    [InlineData(0L, 1000L, false)]
    [InlineData(1000L, 0L, false)]
    [InlineData(-1L, 1000L, false)]
    [InlineData(1000L, -1L, false)]
    [InlineData(-1L, -1L, false)]
    public void MatchingKnownEffectiveUidsAreRequired(long serverUid, long peerUid, bool expected)
        => Assert.Equal(expected, SingleInstanceProtocol.IsSameEffectiveUser(serverUid, peerUid));

    [Theory]
    [InlineData(true, 1000L, 1000L, true)]
    [InlineData(true, 1000L, 0L, false)]
    [InlineData(false, 1000L, 1000L, false)]
    [InlineData(false, -1L, -1L, false)]
    public void ActivationNeedsBothTheExactShowAndTheSameEffectiveUid(bool exactShow, long serverUid, long peerUid, bool expected)
    {
        var payload = exactShow
            ? ShowBytes
            : new byte[] { (byte)'s', (byte)'h', (byte)'o', (byte)'w', (byte)'x' };
        Assert.Equal(expected, SingleInstanceProtocol.CanAcceptActivation(payload, serverUid, peerUid));
    }

    [Fact]
    public void SocketPathLimitCountsUtf8BytesNotCharacters()
    {
        var asciiOk = "/" + new string('a', 106);        // 107 UTF-8 bytes
        var asciiTooLong = "/" + new string('a', 107);   // 108 UTF-8 bytes
        Assert.Equal(107, Encoding.UTF8.GetByteCount(asciiOk));
        Assert.True(SingleInstanceProtocol.IsSocketPathWithinLimit(asciiOk));
        Assert.False(SingleInstanceProtocol.IsSocketPathWithinLimit(asciiTooLong));

        var multibyteOk = "/\u76ee" + new string('a', 103);       // 1 + 3 + 103 = 107 bytes
        var multibyteTooLong = "/\u76ee" + new string('a', 104);  // 108 bytes
        Assert.Equal(107, Encoding.UTF8.GetByteCount(multibyteOk));
        Assert.True(SingleInstanceProtocol.IsSocketPathWithinLimit(multibyteOk));
        Assert.False(SingleInstanceProtocol.IsSocketPathWithinLimit(multibyteTooLong));
    }

    [Fact]
    public void NullOrEmptySocketPathsAreRejected()
    {
        Assert.False(SingleInstanceProtocol.IsSocketPathWithinLimit(null));
        Assert.False(SingleInstanceProtocol.IsSocketPathWithinLimit(string.Empty));
    }

    // ── Windows facade: the existing named event is preserved, readiness is only true once the
    //    listener is actually running, and disposal clears it. The synchronous dispatcher keeps the
    //    activation callback on the listener thread and avoids any Avalonia dispatcher. ──────────

    [WindowsFact]
    public void CanActivateIsFalseUntilListenStartsTheRecoveryChannel()
    {
        using var instance = new SingleInstance("am-test-" + Guid.NewGuid().ToString("N"), a => a());
        Assert.True(instance.IsFirstInstance);
        Assert.False(instance.CanActivate);
        Assert.False(instance.ActivationFailed);

        instance.Listen(() => { });

        Assert.True(instance.CanActivate);
    }

    [WindowsFact]
    public void DisposalClearsRecoveryReadiness()
    {
        var instance = new SingleInstance("am-test-" + Guid.NewGuid().ToString("N"), a => a());
        instance.Listen(() => { });
        Assert.True(instance.CanActivate);

        instance.Dispose();

        Assert.False(instance.CanActivate);
    }

    [WindowsFact]
    public void ListenAfterDisposeDoesNotRecreateTheRecoveryChannel()
    {
        var instance = new SingleInstance("am-test-" + Guid.NewGuid().ToString("N"), a => a());
        instance.Dispose();

        instance.Listen(() => { });

        Assert.False(instance.CanActivate);
    }

    [WindowsFact]
    public void WindowsNamedEventStillActivatesTheRunningInstance()
    {
        var name = "am-test-" + Guid.NewGuid().ToString("N");
        // Construction, the bounded wait and disposal all stay on this one owner thread: the named
        // mutex is owned here and must be released here, and no await may migrate the continuation.
        using var activated = new ManualResetEventSlim(false);
        using var first = new SingleInstance(name, a => a());
        first.Listen(() => activated.Set());

        using var second = new SingleInstance(name, a => a());
        Assert.False(second.IsFirstInstance);
        Assert.False(second.CanActivate);
        // The hand-off itself must have been established, independent of the callback: disposal's own
        // Set() would also raise the callback, so the callback alone proves nothing.
        Assert.False(second.ActivationFailed);

        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(first.CanActivate);
    }
}

/// <summary>Runs only on Windows; elsewhere xUnit marks it skipped so a Linux run never fake-passes named-event semantics.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows named-event semantics.";
        }
    }
}

/// <summary>Runs only on Linux; elsewhere xUnit marks it skipped so a Windows run never fake-passes Unix socket/lock semantics.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires Linux Unix-domain-socket, flock and peer-credential semantics.";
        }
    }
}

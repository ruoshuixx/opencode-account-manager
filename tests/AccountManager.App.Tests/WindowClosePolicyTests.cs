using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// The hide-on-close decision is pure, and the asynchronous re-entrancy gate is exercised through the
/// same coordinator the window Closing handler will use. No tray, DBus, AppServices or account scan
/// is touched: the tray probe is an injected in-memory function.
/// </summary>
public class WindowClosePolicyTests
{
    // Full 12-case truth table: exiting × tray availability × recovery readiness.
    [Theory]
    [InlineData(false, TrayAvailability.Available, true, true)]
    [InlineData(false, TrayAvailability.Available, false, false)]
    [InlineData(false, TrayAvailability.Unavailable, true, false)]
    [InlineData(false, TrayAvailability.Unavailable, false, false)]
    [InlineData(false, TrayAvailability.Unknown, true, false)]
    [InlineData(false, TrayAvailability.Unknown, false, false)]
    [InlineData(true, TrayAvailability.Available, true, false)]
    [InlineData(true, TrayAvailability.Available, false, false)]
    [InlineData(true, TrayAvailability.Unavailable, true, false)]
    [InlineData(true, TrayAvailability.Unavailable, false, false)]
    [InlineData(true, TrayAvailability.Unknown, true, false)]
    [InlineData(true, TrayAvailability.Unknown, false, false)]
    public void HideRequiresLiveTrayAndRecoveryChannel(bool exiting, TrayAvailability tray, bool recovery, bool expected)
        => Assert.Equal(expected, WindowClosePolicy.ShouldHide(exiting, tray, recovery));

    [Fact]
    public async Task AnExplicitExitClosesWithoutProbingTheTray()
    {
        var probes = 0;
        var policy = new WindowClosePolicy(_ => { probes++; return Task.FromResult(TrayAvailability.Available); });

        var decision = await policy.DecideOnCloseAsync(exiting: true, recoveryReady: true);

        Assert.Equal(WindowCloseDecision.Close, decision);
        Assert.Equal(0, probes);
        Assert.False(policy.IsCheckPending);
    }

    [Fact]
    public async Task AHiddenCloseNeedsAFreshAvailableProbeAndReadyRecovery()
    {
        var probes = 0;
        var policy = new WindowClosePolicy(_ => { probes++; return Task.FromResult(TrayAvailability.Available); });

        var decision = await policy.DecideOnCloseAsync(exiting: false, recoveryReady: true);

        Assert.Equal(WindowCloseDecision.Hide, decision);
        Assert.Equal(1, probes);
        Assert.False(policy.IsCheckPending);
    }

    [Theory]
    [InlineData(TrayAvailability.Unavailable)]
    [InlineData(TrayAvailability.Unknown)]
    public async Task AnUnavailableOrUnknownTrayClosesForReal(TrayAvailability tray)
    {
        var policy = new WindowClosePolicy(_ => Task.FromResult(tray));

        var decision = await policy.DecideOnCloseAsync(exiting: false, recoveryReady: true);

        Assert.Equal(WindowCloseDecision.Close, decision);
    }

    [Fact]
    public async Task AnAvailableTrayWithoutAReadyRecoveryChannelClosesForReal()
    {
        var policy = new WindowClosePolicy(_ => Task.FromResult(TrayAvailability.Available));

        var decision = await policy.DecideOnCloseAsync(exiting: false, recoveryReady: false);

        Assert.Equal(WindowCloseDecision.Close, decision);
    }

    [Fact]
    public async Task AProbeFailureIsTreatedAsUnknownAndClosesForReal()
    {
        var policy = new WindowClosePolicy(_ => throw new InvalidOperationException("tray probe failed"));

        var decision = await policy.DecideOnCloseAsync(exiting: false, recoveryReady: true);

        Assert.Equal(WindowCloseDecision.Close, decision);
    }

    [Fact]
    public async Task ASecondCloseWhileACheckIsPendingStaysVisibleWithoutASecondProbe()
    {
        var probes = 0;
        var release = new TaskCompletionSource<TrayAvailability>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new WindowClosePolicy(_ => { probes++; return release.Task; });

        var first = policy.DecideOnCloseAsync(exiting: false, recoveryReady: true);
        Assert.True(policy.IsCheckPending);

        var second = await policy.DecideOnCloseAsync(exiting: false, recoveryReady: true);

        Assert.Equal(WindowCloseDecision.KeepVisible, second);
        Assert.Equal(1, probes);

        release.SetResult(TrayAvailability.Available);
        Assert.Equal(WindowCloseDecision.Hide, await first);
        Assert.False(policy.IsCheckPending);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task EachCompletedClosePerformsItsOwnFreshProbe()
    {
        var probes = 0;
        var policy = new WindowClosePolicy(_ => { probes++; return Task.FromResult(TrayAvailability.Unavailable); });

        Assert.Equal(WindowCloseDecision.Close, await policy.DecideOnCloseAsync(false, true));
        Assert.Equal(WindowCloseDecision.Close, await policy.DecideOnCloseAsync(false, true));

        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task CancellingAPendingCheckKeepsTheWindowVisible()
    {
        var release = new TaskCompletionSource<TrayAvailability>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new WindowClosePolicy(_ => release.Task);
        using var cts = new CancellationTokenSource();

        var pending = policy.DecideOnCloseAsync(false, true, cts.Token);
        Assert.True(policy.IsCheckPending);

        cts.Cancel();

        Assert.Equal(WindowCloseDecision.KeepVisible, await pending);
        Assert.False(policy.IsCheckPending);
        release.TrySetResult(TrayAvailability.Available); // let the abandoned probe finish
    }

    [Fact]
    public async Task APreCancelledCloseDoesNotProbeAndKeepsTheWindowVisible()
    {
        // Regression for the current bug: an already-cancelled close must not invoke the probe at all,
        // and an already-completed Available task must not be accepted despite the cancellation.
        var probes = 0;
        var policy = new WindowClosePolicy(_ => { probes++; return Task.FromResult(TrayAvailability.Available); });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var decision = await policy.DecideOnCloseAsync(exiting: false, recoveryReady: true, cts.Token);

        Assert.Equal(WindowCloseDecision.KeepVisible, decision);
        Assert.Equal(0, probes);
    }

    // ── Hidden-window host monitor: a lost host or a lost recovery channel must surface the window
    //    again instead of leaving an invisible, unrecoverable instance. ──────────────────────────

    [Fact]
    public async Task ALostHostOrRecoveryWhileHiddenSurfacesTheWindow()
    {
        var recovered = 0;
        var monitor = new TrayHostMonitor(_ => Task.FromResult(false), () => recovered++);

        Assert.False(await monitor.CheckOnceAsync(default));
        Assert.Equal(1, recovered);
    }

    [Fact]
    public async Task AHealthyTrayKeepsTheWindowHidden()
    {
        var recovered = 0;
        var monitor = new TrayHostMonitor(_ => Task.FromResult(true), () => recovered++);

        Assert.True(await monitor.CheckOnceAsync(default));
        Assert.Equal(0, recovered);
    }

    [Fact]
    public async Task AnUnknownOrFailedProbeWhileHiddenSurfacesTheWindow()
    {
        var recovered = 0;
        var monitor = new TrayHostMonitor(_ => throw new InvalidOperationException("probe failed"), () => recovered++);

        Assert.False(await monitor.CheckOnceAsync(default));
        Assert.Equal(1, recovered);
    }

    [Fact]
    public async Task TheWindowIsSurfacedAtMostOncePerHiddenPeriod()
    {
        var recovered = 0;
        var monitor = new TrayHostMonitor(_ => Task.FromResult(false), () => recovered++);

        await monitor.CheckOnceAsync(default);
        await monitor.CheckOnceAsync(default);

        Assert.Equal(1, recovered);
    }

    // ── Contract: WindowClosePolicy.RevalidateDecision(decision, alive, exiting, trayUsable,
    //    recoveryReady, cancelled). App calls this immediately after its awaited policy result, before
    //    any UI action. Absent/wrong implementation fails these: a provisional Hide must be downgraded
    //    whenever the current tray/recovery/lifecycle state no longer supports hiding. ───────────────

    [Fact]
    public void RevalidateOnlyHidesWithACurrentlyUsableTrayAndLiveRecovery()
    {
        Assert.Equal(WindowCloseDecision.Hide, WindowClosePolicy.RevalidateDecision(
            WindowCloseDecision.Hide, alive: true, exiting: false, trayUsable: true, recoveryReady: true, cancelled: false));
        Assert.Equal(WindowCloseDecision.Close, WindowClosePolicy.RevalidateDecision(
            WindowCloseDecision.Hide, alive: true, exiting: false, trayUsable: false, recoveryReady: true, cancelled: false));
        Assert.Equal(WindowCloseDecision.Close, WindowClosePolicy.RevalidateDecision(
            WindowCloseDecision.Hide, alive: true, exiting: false, trayUsable: true, recoveryReady: false, cancelled: false));
    }

    [Fact]
    public void RevalidateTurnsADeadOrExitingOrCancelledDecisionIntoNoAction()
    {
        Assert.Equal(WindowCloseDecision.KeepVisible, WindowClosePolicy.RevalidateDecision(
            WindowCloseDecision.Hide, alive: false, exiting: false, trayUsable: true, recoveryReady: true, cancelled: false));
        Assert.Equal(WindowCloseDecision.KeepVisible, WindowClosePolicy.RevalidateDecision(
            WindowCloseDecision.Close, alive: true, exiting: true, trayUsable: true, recoveryReady: true, cancelled: false));
        Assert.Equal(WindowCloseDecision.KeepVisible, WindowClosePolicy.RevalidateDecision(
            WindowCloseDecision.Hide, alive: true, exiting: false, trayUsable: true, recoveryReady: true, cancelled: true));
    }

    [Fact]
    public void RevalidateKeepsAnAlreadyCloseOrKeepVisibleDecisionForALiveWindow()
    {
        Assert.Equal(WindowCloseDecision.Close, WindowClosePolicy.RevalidateDecision(
            WindowCloseDecision.Close, alive: true, exiting: false, trayUsable: true, recoveryReady: true, cancelled: false));
        Assert.Equal(WindowCloseDecision.KeepVisible, WindowClosePolicy.RevalidateDecision(
            WindowCloseDecision.KeepVisible, alive: true, exiting: false, trayUsable: true, recoveryReady: true, cancelled: false));
    }

    [Fact]
    public async Task CapabilitiesLostDuringAnOutstandingProbeAreCaughtByRevalidation()
    {
        // A Hide decision is provisionally computed while the probe is outstanding; capabilities are
        // lost before the App can apply it. Revalidation must not hide under the stale result.
        var release = new TaskCompletionSource<TrayAvailability>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new WindowClosePolicy(_ => release.Task);

        var pending = policy.DecideOnCloseAsync(exiting: false, recoveryReady: true);
        Assert.True(policy.IsCheckPending);

        release.SetResult(TrayAvailability.Available);
        var provisional = await pending;
        Assert.Equal(WindowCloseDecision.Hide, provisional);

        Assert.Equal(WindowCloseDecision.Close, WindowClosePolicy.RevalidateDecision(
            provisional, alive: true, exiting: false, trayUsable: true, recoveryReady: false, cancelled: false));
        Assert.Equal(WindowCloseDecision.Close, WindowClosePolicy.RevalidateDecision(
            provisional, alive: true, exiting: false, trayUsable: false, recoveryReady: true, cancelled: false));
        Assert.Equal(WindowCloseDecision.KeepVisible, WindowClosePolicy.RevalidateDecision(
            provisional, alive: false, exiting: false, trayUsable: true, recoveryReady: true, cancelled: false));
        Assert.Equal(WindowCloseDecision.Hide, WindowClosePolicy.RevalidateDecision(
            provisional, alive: true, exiting: false, trayUsable: true, recoveryReady: true, cancelled: false));
    }
}

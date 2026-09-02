using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Citadel.Setting.Components;

namespace Module.Mangareader;

public sealed class ReaderPolicyTests
{
    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(-4, 0.5)]
    [InlineData(0.94, 0.9)]
    [InlineData(3.8, 3)]
    public void ZoomNormalization_IsFiniteClampedAndStepped(double value, double expected) =>
        Assert.Equal(expected, ReaderValuePolicy.NormalizeZoom(value), 3);

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(-4, 0)]
    [InlineData(2.5, 5)]
    [InlineData(77, 75)]
    [InlineData(90, 80)]
    public void DimNormalization_UsesLockedRangeAndFivePercentSteps(double value, double expected) =>
        Assert.Equal(expected, ReaderValuePolicy.NormalizeDim(value), 3);

    [Theory]
    [InlineData(double.NaN, 5)]
    [InlineData(-4, 1)]
    [InlineData(5.6, 6)]
    [InlineData(90, 30)]
    public void AutoScrollNormalization_UsesLockedRange(double value, double expected) =>
        Assert.Equal(expected, ReaderValuePolicy.NormalizeAutoScroll(value), 3);

    [Fact]
    public void ReaderState_StartsWithSessionOnlyDefaults()
    {
        var state = new ReaderSessionState();

        Assert.Equal(1, state.ZoomScale);
        Assert.Equal(0, state.DimPercent);
        Assert.Equal(5, state.AutoScrollSecondsPerViewport);
        Assert.False(state.IsFullscreen);
        Assert.False(state.IsDrawerOpen);
        Assert.False(state.IsDrawerPinned);
        Assert.False(state.IsAutoScrollRunning);
        Assert.True(state.IsLoading);
    }

    [Fact]
    public void EscapePriority_IsFullscreenThenDrawerThenReader()
    {
        var state = new ReaderSessionState();
        state.SetDrawerOpen(true);
        state.SetFullscreen(true);
        Assert.Equal(ReaderEscapeAction.ExitFullscreen, ReaderInputPolicy.ResolveEscape(state));

        state.SetFullscreen(false);
        Assert.Equal(ReaderEscapeAction.CloseDrawer, ReaderInputPolicy.ResolveEscape(state));

        state.SetDrawerOpen(false);
        Assert.Equal(ReaderEscapeAction.CloseReader, ReaderInputPolicy.ResolveEscape(state));
    }

    [Theory]
    [InlineData(Key.F11, ModifierKeys.None, ReaderKeyAction.ToggleFullscreen)]
    [InlineData(Key.Up, ModifierKeys.Alt, ReaderKeyAction.DimLighter)]
    [InlineData(Key.Down, ModifierKeys.Alt, ReaderKeyAction.DimDarker)]
    [InlineData(Key.D0, ModifierKeys.Alt, ReaderKeyAction.ResetDim)]
    [InlineData(Key.NumPad0, ModifierKeys.Alt, ReaderKeyAction.ResetDim)]
    [InlineData(Key.D0, ModifierKeys.Control, ReaderKeyAction.ResetZoom)]
    [InlineData(Key.NumPad0, ModifierKeys.Control, ReaderKeyAction.ResetZoom)]
    [InlineData(Key.PageDown, ModifierKeys.None, ReaderKeyAction.ReportKeyboardScroll)]
    [InlineData(Key.Space, ModifierKeys.None, ReaderKeyAction.ReportKeyboardScroll)]
    [InlineData(Key.A, ModifierKeys.None, ReaderKeyAction.None)]
    public void KeyboardContract_MapsEveryLockedReaderShortcut(
        Key key,
        ModifierKeys modifiers,
        ReaderKeyAction expected) =>
        Assert.Equal(expected, ReaderInputPolicy.ResolveKey(key, modifiers));

    [Theory]
    [InlineData(0, ReaderOverlayZone.Previous)]
    [InlineData(299, ReaderOverlayZone.Previous)]
    [InlineData(300, ReaderOverlayZone.Menu)]
    [InlineData(599, ReaderOverlayZone.Menu)]
    [InlineData(600, ReaderOverlayZone.Next)]
    [InlineData(900, ReaderOverlayZone.Next)]
    public void OverlayZones_AreThreeEqualViewportAreas(double x, ReaderOverlayZone expected) =>
        Assert.Equal(expected, ReaderInputPolicy.ResolveOverlayZone(x, 900));

    [Fact]
    public void DragThreshold_UsesSystemStyleStrictBoundary()
    {
        Assert.False(ReaderInputPolicy.ExceedsDragThreshold(4, 4, 4, 4));
        Assert.True(ReaderInputPolicy.ExceedsDragThreshold(4.1, 0, 4, 4));
        Assert.True(ReaderInputPolicy.ExceedsDragThreshold(0, -4.1, 4, 4));
    }

    [Fact]
    public void InputRouter_ExcludesEveryInteractiveControlFamily()
    {
        WpfTest.Run(() =>
        {
            Assert.True(ReaderInputRouter.IsInteractiveSource(new SettingButton()));
            Assert.True(ReaderInputRouter.IsInteractiveSource(new TextBox()));
            Assert.True(ReaderInputRouter.IsInteractiveSource(new ComboBox()));
            Assert.True(ReaderInputRouter.IsInteractiveSource(new Slider()));
            Assert.True(ReaderInputRouter.IsInteractiveSource(new Thumb()));
            Assert.True(ReaderInputRouter.IsInteractiveSource(new ScrollBar()));
            Assert.True(ReaderInputRouter.IsInteractiveSource(new SettingDrawer()));
            Assert.True(ReaderInputRouter.IsInteractiveSource(new SettingWindowChrome()));
            Assert.False(ReaderInputRouter.IsInteractiveSource(new Border()));
        });
    }

    [Fact]
    public void OverlayStep_IsNinetyPercentAndCoalescesFromMovingTarget()
    {
        var first = ReaderViewportStepPolicy.NextTarget(100, null, 500, 2000, 1);
        var second = ReaderViewportStepPolicy.NextTarget(150, first, 500, 2000, 1);

        Assert.Equal(550, first);
        Assert.Equal(1000, second);
        Assert.Equal(0, ReaderViewportStepPolicy.NextTarget(20, null, 500, 2000, -1));
        Assert.Equal(2000, ReaderViewportStepPolicy.NextTarget(1900, null, 500, 2000, 1));
    }

    [Fact]
    public void AutoScrollDistance_IsElapsedTimeBased()
    {
        Assert.Equal(
            300,
            ReaderAutoScrollPolicy.DistanceForElapsed(600, 4, TimeSpan.FromSeconds(2)),
            3);
        Assert.Equal(0, ReaderAutoScrollPolicy.DistanceForElapsed(double.NaN, 5, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void AutoScroll_StopsForEveryOriginExceptItsOwnAndLayoutRestore()
    {
        foreach (var origin in Enum.GetValues<ReaderActivityOrigin>())
        {
            var expected = origin is not ReaderActivityOrigin.AutoScroll
                and not ReaderActivityOrigin.LayoutRestore;
            Assert.Equal(expected, ReaderAutoScrollPolicy.StopsFor(origin));
        }
    }

    [Fact]
    public void DrawerPin_PreservesOrdinaryReaderActionsButNotExplicitCloseRouting()
    {
        Assert.True(ReaderDrawerPolicy.ShouldCloseForActivity(ReaderActivityOrigin.OverlayStep, false));
        Assert.True(ReaderDrawerPolicy.ShouldCloseForActivity(ReaderActivityOrigin.ChapterJump, false));
        Assert.True(ReaderDrawerPolicy.ShouldCloseForActivity(ReaderActivityOrigin.Zoom, false));
        Assert.False(ReaderDrawerPolicy.ShouldCloseForActivity(ReaderActivityOrigin.OverlayStep, true));
        Assert.False(ReaderDrawerPolicy.ShouldCloseForActivity(ReaderActivityOrigin.ManualWheel, false));
    }

    [Fact]
    public void FullscreenGeometry_UsesDeviceToDipMatrix()
    {
        var result = ReaderFullscreenGeometry.ToDipRect(
            new ReaderPixelRect(200, 100, 2200, 1300),
            new Matrix(0.5, 0, 0, 0.5, 0, 0));

        Assert.Equal(100, result.Left);
        Assert.Equal(50, result.Top);
        Assert.Equal(1000, result.Width);
        Assert.Equal(600, result.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReaderFullscreenGeometry.ToDipRect(
                new ReaderPixelRect(1, 1, 1, 2),
                Matrix.Identity));
    }

    [Fact]
    public void FullscreenNativeMonitorShapesMatchWin32Layout()
    {
        Assert.Equal(16, Marshal.SizeOf<ReaderNativeRect>());
        Assert.Equal(104, Marshal.SizeOf<ReaderMonitorInfoEx>());
        Assert.Equal(4, Marshal.OffsetOf<ReaderMonitorInfoEx>(nameof(ReaderMonitorInfoEx.Monitor)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<ReaderMonitorInfoEx>(nameof(ReaderMonitorInfoEx.WorkArea)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<ReaderMonitorInfoEx>(nameof(ReaderMonitorInfoEx.DeviceName)).ToInt32());
    }
}

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Citadel.Setting;
using Citadel.Setting.Components;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

public sealed class ReaderControllerTests
{
    [Fact]
    public void ChapterContribution_UsesWholeTitleAndTracksCommittedChapter()
    {
        WpfTest.Run(() =>
        {
            var chapters = ReaderTestContext.Title(3).Chapters;
            var chapterState = new TestChapterNavigation(chapters);
            var feature = new ReaderChapterNavigation();
            feature.Attach(ReaderTestContext.Create(chapters: chapterState));
            var card = Assert.IsType<ReaderDrawerCardContribution>(
                Assert.Single(feature.DrawerContributions)).Card;
            var picker = LogicalDescendants<ComboBox>(card).Single();
            var previous = LogicalDescendants<SettingButton>(card)
                .Single(button => AutomationProperties.GetName(button) == "Previous chapter");
            var next = LogicalDescendants<SettingButton>(card)
                .Single(button => AutomationProperties.GetName(button) == "Next chapter");

            Assert.Equal(3, picker.Items.Count);
            Assert.False(previous.IsEnabled);
            Assert.True(next.IsEnabled);

            next.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal([1], chapterState.NavigationRequests);
            Assert.Equal(1, ((ReaderChapterChoice?)picker.SelectedItem)?.Index);
            Assert.True(previous.IsEnabled);
            Assert.True(next.IsEnabled);
            feature.Dispose();
        });
    }

    [Fact]
    public void ChapterContribution_FailedRequestReconcilesToCommittedChapterAndCanRetry()
    {
        WpfTest.Run(() =>
        {
            var chapters = new TestChapterNavigation(ReaderTestContext.Title(3).Chapters)
            {
                ActiveChapterIndex = 1,
                NavigationHandler = _ => Task.FromException(
                    new InvalidDataException("chapter is damaged")),
            };
            var notifications = new ReaderNotificationHub();
            var warnings = new List<ReaderToastRequestEventArgs>();
            notifications.ToastRequested += (_, warning) => warnings.Add(warning);
            using var feature = new ReaderChapterNavigation();
            feature.Attach(ReaderTestContext.Create(
                chapters: chapters,
                notifications: notifications));
            var card = Assert.IsType<ReaderDrawerCardContribution>(
                Assert.Single(feature.DrawerContributions)).Card;
            var picker = LogicalDescendants<ComboBox>(card).Single();

            picker.SelectedItem = picker.Items.Cast<ReaderChapterChoice>()
                .Single(choice => choice.Index == 2);
            WpfTest.PumpFor(TimeSpan.FromMilliseconds(40));

            Assert.Equal(1, ((ReaderChapterChoice?)picker.SelectedItem)?.Index);
            Assert.Contains("chapter is damaged", Assert.Single(warnings).Message, StringComparison.Ordinal);

            chapters.NavigationHandler = null;
            picker.SelectedItem = picker.Items.Cast<ReaderChapterChoice>()
                .Single(choice => choice.Index == 0);
            WpfTest.PumpUntil(() => chapters.ActiveChapterIndex == 0);

            Assert.Equal(0, ((ReaderChapterChoice?)picker.SelectedItem)?.Index);
            Assert.Equal([2, 0], chapters.NavigationRequests);
        });
    }

    [Fact]
    public void ReaderDrawer_MaterializationAndOpenDoNotRequestAnotherChapter()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            var activity = new ReaderActivityHub();
            var chapters = new TestChapterNavigation(ReaderTestContext.Title(3).Chapters)
            {
                ActiveChapterIndex = 1,
            };
            var context = ReaderTestContext.Create(
                state,
                commands,
                chapters: chapters,
                activity: activity);
            using var navigation = new ReaderChapterNavigation();
            using var drawer = new ReaderDrawer(state, commands, activity);
            drawer.Resources.MergedDictionaries.Add(new SettingResources());
            navigation.Attach(context);
            drawer.Attach(context);
            drawer.SetContributions(navigation.DrawerContributions);
            var window = new Window
            {
                Width = 800,
                Height = 600,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = drawer,
            };
            window.Resources.MergedDictionaries.Add(new SettingResources());

            try
            {
                window.Show();
                WpfTest.PumpFor(TimeSpan.FromMilliseconds(250));
                commands.ToggleDrawer();
                WpfTest.PumpFor(TimeSpan.FromMilliseconds(300));

                var card = Assert.IsType<ReaderDrawerCardContribution>(
                    Assert.Single(navigation.DrawerContributions)).Card;
                var picker = Descendants<ComboBox>(card).Single();
                Assert.Equal(1, ((ReaderChapterChoice?)picker.SelectedItem)?.Index);
                Assert.Equal(1, chapters.ActiveChapterIndex);
                Assert.Empty(chapters.NavigationRequests);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ReaderDrawer_SliderLabelAndValueDoNotOverlapAtMinimumWidth()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            var activity = new ReaderActivityHub();
            var context = ReaderTestContext.Create(state, commands, activity: activity);
            using var autoScroll = new ReaderAutoScrollController(state, commands);
            using var drawer = new ReaderDrawer(state, commands, activity);
            drawer.Resources.MergedDictionaries.Add(new SettingResources());
            autoScroll.Attach(context);
            drawer.Attach(context);
            drawer.SetContributions(autoScroll.DrawerContributions);
            var window = new Window
            {
                Width = 640,
                Height = 480,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = drawer,
            };

            try
            {
                window.Show();
                commands.ToggleDrawer();
                WpfTest.PumpFor(TimeSpan.FromMilliseconds(300));

                var label = Descendants<TextBlock>(drawer)
                    .Single(text => text.Text == "Speed");
                var value = Descendants<TextBlock>(drawer)
                    .Single(text => text.Text == "5 s / screen");
                var labelBounds = label.TransformToAncestor(drawer)
                    .TransformBounds(new Rect(label.RenderSize));
                var valueBounds = value.TransformToAncestor(drawer)
                    .TransformBounds(new Rect(value.RenderSize));

                Assert.False(labelBounds.IntersectsWith(valueBounds));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ReaderDrawer_ChapterPickerOwnsFirstRowAndButtonsBalanceBelow()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            var activity = new ReaderActivityHub();
            var chapters = new TestChapterNavigation(ReaderTestContext.Title(3).Chapters)
            {
                ActiveChapterIndex = 1,
            };
            var context = ReaderTestContext.Create(
                state,
                commands,
                chapters: chapters,
                activity: activity);
            using var navigation = new ReaderChapterNavigation();
            using var drawer = new ReaderDrawer(state, commands, activity);
            drawer.Resources.MergedDictionaries.Add(new SettingResources());
            navigation.Attach(context);
            drawer.Attach(context);
            drawer.SetContributions(navigation.DrawerContributions);
            var window = new Window
            {
                Width = 640,
                Height = 480,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = drawer,
            };

            try
            {
                window.Show();
                commands.ToggleDrawer();
                WpfTest.PumpFor(TimeSpan.FromMilliseconds(300));

                var panel = Descendants<SettingDrawer>(drawer).Single();
                var picker = Descendants<ComboBox>(drawer).Single();
                var previous = Descendants<SettingButton>(drawer)
                    .Single(button => AutomationProperties.GetName(button) == "Previous chapter");
                var next = Descendants<SettingButton>(drawer)
                    .Single(button => AutomationProperties.GetName(button) == "Next chapter");
                var previousBounds = previous.TransformToAncestor(drawer)
                    .TransformBounds(new Rect(previous.RenderSize));
                var pickerBounds = picker.TransformToAncestor(drawer)
                    .TransformBounds(new Rect(picker.RenderSize));
                var nextBounds = next.TransformToAncestor(drawer)
                    .TransformBounds(new Rect(next.RenderSize));
                var selectedLabel = Descendants<TextBlock>(picker)
                    .Single(text => text.Text == "Chapter 2");
                var selectedLabelBounds = selectedLabel.TransformToAncestor(drawer)
                    .TransformBounds(new Rect(selectedLabel.RenderSize));
                var readableLabelWidth = Math.Max(
                    0,
                    Math.Min(selectedLabelBounds.Right, pickerBounds.Right - 18)
                        - Math.Max(selectedLabelBounds.Left, pickerBounds.Left));

                Assert.True(pickerBounds.Bottom <= previousBounds.Top + 0.75);
                Assert.True(pickerBounds.Bottom <= nextBounds.Top + 0.75);
                Assert.InRange(Math.Abs(previousBounds.Width - nextBounds.Width), 0, 1.5);
                Assert.True(previousBounds.Right <= nextBounds.Left + 0.75);
                Assert.InRange(pickerBounds.Width, 72, panel.PanelWidth);
                Assert.InRange(readableLabelWidth, 40, pickerBounds.Width);
                Assert.InRange(previousBounds.Left, -0.75, panel.PanelWidth);
                Assert.InRange(nextBounds.Right, 0, panel.PanelWidth + 0.75);
                Assert.InRange(
                    Math.Abs(nextBounds.Top - previousBounds.Top),
                    0,
                    1.5);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ChromeTitleTracksTheCommittedChapterThroughItsContract()
    {
        WpfTest.Run(() =>
        {
            var chapters = new TestChapterNavigation(ReaderTestContext.Title(2).Chapters);
            using var chrome = new ReaderChromeController();
            chrome.Attach(ReaderTestContext.Create(chapters: chapters));
            var view = Assert.IsType<Citadel.Setting.Components.SettingWindowChrome>(
                Assert.Single(chrome.Visuals).View);
            Assert.Equal("Test title — Chapter 1 — Manga Reader", view.Title);

            chapters.NavigateToChapterAsync(1).GetAwaiter().GetResult();

            Assert.Equal("Test title — Chapter 2 — Manga Reader", view.Title);
        });
    }

    [Fact]
    public void ToastLatestRequestOwnsDurationAndDisposeClosesItsTimer()
    {
        WpfTest.Run(() =>
        {
            var notifications = new ReaderNotificationHub();
            using var toast = new ReaderToast();
            toast.Attach(ReaderTestContext.Create(notifications: notifications));

            notifications.ShowToast("first", TimeSpan.FromMilliseconds(40));
            Assert.Equal(Visibility.Visible, toast.Visibility);
            WpfTest.PumpFor(TimeSpan.FromMilliseconds(20));
            notifications.ShowToast("second", TimeSpan.FromMilliseconds(120));
            WpfTest.PumpFor(TimeSpan.FromMilliseconds(60));
            Assert.Equal(Visibility.Visible, toast.Visibility);
            Assert.Equal("second", ((TextBlock)toast.FindName("Message")).Text);

            WpfTest.PumpFor(TimeSpan.FromMilliseconds(90));
            Assert.Equal(Visibility.Collapsed, toast.Visibility);

            notifications.ShowToast("third", TimeSpan.FromSeconds(1));
            toast.Dispose();
            Assert.Equal(Visibility.Collapsed, toast.Visibility);
        });
    }

    [Fact]
    public void ZoomCommands_UseOneStateAndPreservePointerAnchor()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var commands = new ReaderCommandHub();
            var viewport = new TestViewport
            {
                ScrollableWidth = 1200,
                ScrollableHeight = 2400,
            };
            viewport.ScrollToHorizontalOffset(100, ReaderActivityOrigin.LayoutRestore);
            viewport.ScrollToVerticalOffset(200, ReaderActivityOrigin.LayoutRestore);
            var chapters = new TestChapterNavigation();
            var activity = new ReaderActivityHub();
            var context = ReaderTestContext.Create(
                state,
                commands,
                viewport,
                chapters,
                activity: activity);
            using var zoom = new ReaderZoomController(state, commands);
            zoom.Attach(context);

            commands.ChangeZoom(1);
            WpfTest.PumpUntil(() => Math.Abs(state.ZoomScale - 1.1) < 0.001);

            Assert.Equal(150, viewport.HorizontalOffset, 3);
            Assert.Equal(250, viewport.VerticalOffset, 3);
            Assert.Equal(1, chapters.ZoomNotifications);
            Assert.Equal(ReaderActivityOrigin.Zoom, activity.LastOrigin);
            var card = Assert.IsType<ReaderDrawerCardContribution>(
                Assert.Single(zoom.DrawerContributions)).Card;
            Assert.Contains(
                LogicalDescendants<TextBlock>(card),
                text => text.Text == "110%");
        });
    }

    [Fact]
    public void AutoScroll_UsesReversedSpeedSliderAndStopsOnManualIntent()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var commands = new ReaderCommandHub();
            var activity = new ReaderActivityHub();
            var context = ReaderTestContext.Create(state, commands, activity: activity);
            using var feature = new ReaderAutoScrollController(state, commands);
            feature.Attach(context);
            var card = Assert.IsType<ReaderDrawerCardContribution>(
                Assert.Single(feature.DrawerContributions)).Card;
            var slider = LogicalDescendants<SettingSlider>(card).Single();

            Assert.True(slider.IsDirectionReversed);
            Assert.Equal(1, slider.Minimum);
            Assert.Equal(30, slider.Maximum);
            commands.StartAutoScroll();
            Assert.True(state.IsAutoScrollRunning);

            activity.Report(ReaderActivityOrigin.LayoutRestore);
            Assert.True(state.IsAutoScrollRunning);
            activity.Report(ReaderActivityOrigin.ManualWheel);
            Assert.False(state.IsAutoScrollRunning);

            state.SetDrawerOpen(true);
            commands.StartAutoScroll();
            Assert.True(state.IsAutoScrollRunning);
            commands.StopAutoScroll();

            state.SetDrawerOpen(false);
            commands.StartAutoScroll();
            Assert.True(state.IsAutoScrollRunning);
            state.SetDrawerOpen(true);
            Assert.False(state.IsAutoScrollRunning);
        });
    }

    [Fact]
    public void AutoScroll_AdvancesOnRenderFramesInsteadOfBackgroundTimerBursts()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            state.SetAutoScrollSecondsPerViewport(1);
            var commands = new ReaderCommandHub();
            var viewport = new TestViewport();
            var context = ReaderTestContext.Create(state, commands, viewport);
            using var feature = new ReaderAutoScrollController(state, commands);
            feature.Attach(context);
            var window = new Window
            {
                Width = 320,
                Height = 240,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = viewport.InputElement,
            };

            try
            {
                window.Show();
                commands.StartAutoScroll();
                WpfTest.PumpFor(TimeSpan.FromMilliseconds(140));
                commands.StopAutoScroll();

                Assert.True(viewport.VerticalScrolls.Count >= 3);
                Assert.All(
                    viewport.VerticalScrolls,
                    scroll => Assert.Equal(ReaderActivityOrigin.AutoScroll, scroll.Origin));
                Assert.True(viewport.VerticalScrolls.Zip(viewport.VerticalScrolls.Skip(1))
                    .All(pair => pair.First.Offset < pair.Second.Offset));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AutoScrollCard_UsesSeparateStartAndStopButtons()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var commands = new ReaderCommandHub();
            using var feature = new ReaderAutoScrollController(state, commands);
            feature.Attach(ReaderTestContext.Create(state, commands));
            var card = Assert.IsType<ReaderDrawerCardContribution>(
                Assert.Single(feature.DrawerContributions)).Card;
            var buttons = LogicalDescendants<SettingButton>(card).ToArray();
            var start = buttons.Single(button =>
                AutomationProperties.GetAutomationId(button) == "ReaderAutoScrollStart");
            var stop = buttons.Single(button =>
                AutomationProperties.GetAutomationId(button) == "ReaderAutoScrollStop");

            Assert.True(start.IsEnabled);
            Assert.False(stop.IsEnabled);
            start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(state.IsAutoScrollRunning);
            Assert.False(start.IsEnabled);
            Assert.True(stop.IsEnabled);
            stop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(state.IsAutoScrollRunning);
            Assert.True(start.IsEnabled);
            Assert.False(stop.IsEnabled);
        });
    }

    [Fact]
    public void AutoScrollControls_DoNotReenterThroughManualReaderActivity()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            var commands = new ReaderCommandHub();
            var activity = new ReaderActivityHub();
            var viewport = new TestViewport();
            using var feature = new ReaderAutoScrollController(state, commands);
            feature.Attach(ReaderTestContext.Create(
                state,
                commands,
                viewport,
                activity: activity));
            var card = Assert.IsType<ReaderDrawerCardContribution>(
                Assert.Single(feature.DrawerContributions)).Card;
            card.Resources.MergedDictionaries.Add(new SettingResources());
            var start = LogicalDescendants<SettingButton>(card).Single(button =>
                AutomationProperties.GetAutomationId(button) == "ReaderAutoScrollStart");
            var stop = LogicalDescendants<SettingButton>(card).Single(button =>
                AutomationProperties.GetAutomationId(button) == "ReaderAutoScrollStop");
            var slider = LogicalDescendants<SettingSlider>(card).Single();
            var window = new Window
            {
                Width = 360,
                Height = 240,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = card,
            };
            using var router = new ReaderInputRouter(window, viewport, state, commands, activity);

            try
            {
                window.Show();
                start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(state.IsAutoScrollRunning);

                RaisePreviewMouseDown(slider);
                slider.Value = 10;
                Assert.True(state.IsAutoScrollRunning);
                Assert.Equal(10, state.AutoScrollSecondsPerViewport);

                RaisePreviewMouseDown(stop);
                Assert.True(state.IsAutoScrollRunning);
                stop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.False(state.IsAutoScrollRunning);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ZoomCard_BalancesSharedButtonsAndCenteredValue()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            using var feature = new ReaderZoomController(state, commands);
            feature.Attach(ReaderTestContext.Create(state, commands));
            var card = Assert.IsType<ReaderDrawerCardContribution>(
                Assert.Single(feature.DrawerContributions)).Card;
            card.Resources.MergedDictionaries.Add(new SettingResources());
            var window = new Window
            {
                Width = 420,
                Height = 180,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = card,
            };

            try
            {
                window.Show();
                WpfTest.PumpFor(TimeSpan.FromMilliseconds(200));
                var decrease = Descendants<SettingButton>(card)
                    .Single(button => AutomationProperties.GetName(button) == "Zoom out");
                var increase = Descendants<SettingButton>(card)
                    .Single(button => AutomationProperties.GetName(button) == "Zoom in");
                var value = Descendants<TextBlock>(card).Single(text => text.Text == "100%");
                var controls = Assert.IsType<Grid>(LogicalTreeHelper.GetParent(decrease));

                Assert.InRange(Math.Abs(decrease.ActualWidth - increase.ActualWidth), 0, 1.5);
                Assert.InRange(
                    controls.ColumnDefinitions.Max(column => column.ActualWidth)
                        - controls.ColumnDefinitions.Min(column => column.ActualWidth),
                    0,
                    1.5);
                Assert.Equal(HorizontalAlignment.Center, value.HorizontalAlignment);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PinFeature_UsesOneSharedCardWithCompactIconAction()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            using var feature = new ReaderPinController(state, commands);
            feature.Attach(ReaderTestContext.Create(state, commands));
            var contribution = Assert.IsType<ReaderDrawerCardContribution>(
                Assert.Single(feature.DrawerContributions));
            contribution.Card.Resources.MergedDictionaries.Add(new SettingResources());
            var window = new Window
            {
                Width = 360,
                Height = 120,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = contribution.Card,
            };

            try
            {
                window.Show();
                WpfTest.PumpFor(TimeSpan.FromMilliseconds(150));
                var action = Descendants<SettingButton>(contribution.Card).Single();

                Assert.InRange(action.Width, 28, 36);
                Assert.IsType<System.Windows.Shapes.Path>(action.Content);
                Assert.Equal("Pin Drawer", AutomationProperties.GetName(action));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DimLayer_IsHitTestFreeAndTracksOnlyViewportDimensions()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            var commands = new ReaderCommandHub();
            var viewport = new TestViewport { ViewportWidth = 720, ViewportHeight = 480 };
            var context = ReaderTestContext.Create(state, commands, viewport);
            using var dim = new ReaderDimController(state, commands);
            dim.Attach(context);
            var layer = Assert.IsType<System.Windows.Controls.Border>(
                Assert.Single(dim.Visuals).View);

            commands.SetDim(43);

            Assert.Equal(45, state.DimPercent);
            Assert.Equal(0.45, layer.Opacity, 3);
            Assert.Equal(720, layer.Width);
            Assert.Equal(480, layer.Height);
            Assert.False(layer.IsHitTestVisible);
            Assert.Equal(System.Windows.Visibility.Visible, layer.Visibility);

            viewport.ViewportWidth = 500;
            viewport.ViewportHeight = 320;
            viewport.RaiseSizeChanged();
            Assert.Equal(500, layer.Width);
            Assert.Equal(320, layer.Height);

            commands.ResetDim();
            Assert.Equal(System.Windows.Visibility.Collapsed, layer.Visibility);
        });
    }

    [Fact]
    public void GlobalReset_ChangesExactDomainsAndPreservesReaderPositionAndSurfaces()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState(40, 17);
            state.SetLoading(false);
            state.SetZoomScale(2);
            state.SetDrawerOpen(true);
            state.SetDrawerPinned(true);
            state.SetFullscreen(true);
            state.SetAutoScrollRunning(true);
            var commands = new ReaderCommandHub();
            var viewport = new TestViewport();
            viewport.ScrollToVerticalOffset(320, ReaderActivityOrigin.LayoutRestore);
            var readingAnchorBefore = (viewport.VerticalOffset + (viewport.ViewportHeight / 2))
                / state.ZoomScale;
            var chapters = new TestChapterNavigation(ReaderTestContext.Title(2).Chapters)
            {
                ActiveChapterIndex = 1,
            };
            var activity = new ReaderActivityHub();
            var context = ReaderTestContext.Create(
                state,
                commands,
                viewport,
                chapters,
                activity: activity);

            using var auto = new ReaderAutoScrollController(state, commands);
            using var pin = new ReaderPinController(state, commands);
            using var zoom = new ReaderZoomController(state, commands);
            using var dim = new ReaderDimController(state, commands);
            using var reset = new ReaderResetController(state, commands);
            auto.Attach(context);
            pin.Attach(context);
            zoom.Attach(context);
            dim.Attach(context);
            reset.Attach(context);

            commands.ResetAll();

            Assert.Equal(1, state.ZoomScale);
            Assert.Equal(0, state.DimPercent);
            Assert.Equal(5, state.AutoScrollSecondsPerViewport);
            Assert.False(state.IsAutoScrollRunning);
            Assert.False(state.IsDrawerPinned);
            Assert.True(state.IsDrawerOpen);
            Assert.True(state.IsFullscreen);
            Assert.Equal(1, chapters.ActiveChapterIndex);
            var readingAnchorAfter = viewport.VerticalOffset + (viewport.ViewportHeight / 2);
            Assert.Equal(readingAnchorBefore, readingAnchorAfter, 3);
        });
    }

    [Fact]
    public void GlobalReset_PreservesAnOpenUnpinnedDrawer()
    {
        WpfTest.Run(() =>
        {
            var state = new ReaderSessionState();
            state.SetLoading(false);
            state.SetZoomScale(2);
            state.SetDrawerOpen(true);
            var commands = new ReaderCommandHub();
            var activity = new ReaderActivityHub();
            var context = ReaderTestContext.Create(
                state,
                commands,
                activity: activity);
            using var drawer = new ReaderDrawer(state, commands, activity);
            using var zoom = new ReaderZoomController(state, commands);
            using var reset = new ReaderResetController(state, commands);
            drawer.Attach(context);
            zoom.Attach(context);
            reset.Attach(context);

            commands.ResetAll();

            Assert.Equal(1, state.ZoomScale);
            Assert.True(state.IsDrawerOpen);
            Assert.False(state.IsDrawerPinned);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void RaisePreviewMouseDown(UIElement source) =>
        source.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in LogicalDescendants<T>(child)) yield return nested;
        }
    }
}

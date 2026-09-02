using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Citadel.Setting;
using Citadel.Setting.Components;

namespace Citadel.Uia;

/// <summary>
/// Pins shared presentation behaviour that feature screens must not restate.
/// These tests inspect the rendered template, not a screen-specific wrapper.
/// </summary>
public class SharedComponentBehaviorTests
{
    [Fact]
    public void Field_TextAndPlaceholder_AreLeftAndVerticallyCentered()
    {
        Sta.Run(() =>
        {
            var field = Arrange(new SettingField
            {
                Placeholder = "Search",
                Width = 240,
                Height = 40,
            });

            var editor = Descendant<TextBox>(field);
            var placeholder = Descendants<TextBlock>(field)
                .Single(block => block.Text == "Search");

            Assert.Equal(HorizontalAlignment.Left, editor.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, editor.VerticalContentAlignment);
            Assert.Equal(HorizontalAlignment.Left, placeholder.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, placeholder.VerticalAlignment);
        });
    }

    [Fact]
    public void PasswordField_FillsAvailableWidthAndAcceptsLeftCenteredInput()
    {
        Sta.Run(() =>
        {
            var field = Arrange(new SettingPasswordField
            {
                Width = 240,
                Height = 40,
            });

            var surface = Descendants<Border>(field)
                .Single(border => border.Name == "Surface");
            var editor = Descendant<PasswordBox>(field);

            Assert.Equal(240, surface.ActualWidth);
            Assert.Equal(40, surface.ActualHeight);
            Assert.Equal(HorizontalAlignment.Left, editor.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, editor.VerticalContentAlignment);

            editor.Password = "local-test-password";
            Assert.Equal("local-test-password", field.Password);
        });
    }

    [Fact]
    public void Table_DefaultsToEqualStarColumnsAndCenteredHeaders()
    {
        Sta.Run(() =>
        {
            var table = Arrange(new SettingTable { Width = 640, Height = 240 });
            table.SetColumns(["One", "Two", "Three"]);
            table.SetRows([["a", "b", "c"]]);
            Arrange(table);

            var grid = Descendant<DataGrid>(table);

            Assert.Equal(DataGridLengthUnitType.Star, grid.ColumnWidth.UnitType);
            Assert.All(grid.Columns, column =>
            {
                Assert.Equal(DataGridLengthUnitType.Star, column.Width.UnitType);
                Assert.Equal(1, column.Width.Value);
            });

            var headerStyle = Assert.IsType<Style>(
                grid.Resources[typeof(DataGridColumnHeader)]);
            var alignment = headerStyle.Setters
                .OfType<Setter>()
                .Single(setter => setter.Property == Control.HorizontalContentAlignmentProperty);
            Assert.Equal(HorizontalAlignment.Center, alignment.Value);
        });
    }

    [Fact]
    public void ScrollBar_SharedTemplateActuallyUsed()
    {
        Sta.Run(() =>
        {
            var scrollViewer = WithResources(new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Width = 500, Height = 1000 }
            });
            scrollViewer.Style = (Style)scrollViewer.FindResource("SettingScrollViewerStyle");
            Arrange(scrollViewer);

            var vertical = Descendants<ScrollBar>(scrollViewer)
                .Single(bar => bar.Name == "PART_VerticalScrollBar");
            Assert.Equal("VerticalScrollBar", AutomationProperties.GetAutomationId(vertical));
            Assert.Equal(Orientation.Vertical, vertical.Orientation);
            Assert.Equal(10, vertical.ActualWidth);
        });
    }

    [Fact]
    public void ScrollBar_VerticalAndHorizontalOrientationCorrect()
    {
        Sta.Run(() =>
        {
            var scrollViewer = WithResources(new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Width = 500, Height = 1000 }
            });
            scrollViewer.Style = (Style)scrollViewer.FindResource("SettingScrollViewerStyle");
            Arrange(scrollViewer);

            var vertical = Descendants<ScrollBar>(scrollViewer)
                .Single(bar => bar.Name == "PART_VerticalScrollBar");
            var horizontal = Descendants<ScrollBar>(scrollViewer)
                .Single(bar => bar.Name == "PART_HorizontalScrollBar");

            Assert.Equal(Orientation.Vertical, vertical.Orientation);
            Assert.Equal(Orientation.Horizontal, horizontal.Orientation);
            Assert.Equal(10, vertical.ActualWidth);
            Assert.Equal(10, horizontal.ActualHeight);

            var verticalDecrease = Descendants<RepeatButton>(vertical)
                .Single(button => button.Name == "PART_DecreaseButton");
            var verticalIncrease = Descendants<RepeatButton>(vertical)
                .Single(button => button.Name == "PART_IncreaseButton");
            var horizontalDecrease = Descendants<RepeatButton>(horizontal)
                .Single(button => button.Name == "PART_DecreaseButton");
            var horizontalIncrease = Descendants<RepeatButton>(horizontal)
                .Single(button => button.Name == "PART_IncreaseButton");

            Assert.Same(ScrollBar.PageUpCommand, verticalDecrease.Command);
            Assert.Same(ScrollBar.PageDownCommand, verticalIncrease.Command);
            Assert.Same(ScrollBar.PageLeftCommand, horizontalDecrease.Command);
            Assert.Same(ScrollBar.PageRightCommand, horizontalIncrease.Command);
        });
    }

    [Fact]
    public void ScrollBar_SessionDetachesAndReattachesAcrossLifecycle()
    {
        Sta.Run(() =>
        {
            var scrollViewer = WithResources(new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Width = 180, Height = 1000 }
            });
            scrollViewer.Style = (Style)scrollViewer.FindResource("SettingScrollViewerStyle");
            Arrange(scrollViewer);

            scrollViewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(ScrollBarAutoFade.HasActiveSession(scrollViewer));

            scrollViewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.False(ScrollBarAutoFade.HasActiveSession(scrollViewer));

            scrollViewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(ScrollBarAutoFade.HasActiveSession(scrollViewer));

            ScrollBarAutoFade.SetIsEnabled(scrollViewer, false);
            Assert.False(ScrollBarAutoFade.HasActiveSession(scrollViewer));
        });
    }

    [Fact]
    public void ScrollBar_LayoutWidthStableDuringOpacityChange()
    {
        Sta.Run(() =>
        {
            var scrollViewer = WithResources(new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Width = 180, Height = 1000 }
            });
            scrollViewer.Style = (Style)scrollViewer.FindResource("SettingScrollViewerStyle");
            Arrange(scrollViewer);

            var presenter = Descendants<ScrollContentPresenter>(scrollViewer).Single();
            var vertical = Descendants<ScrollBar>(scrollViewer)
                .Single(bar => bar.Name == "PART_VerticalScrollBar");
            var baselineWidth = presenter.ActualWidth;

            vertical.Opacity = 1.0;
            Arrange(scrollViewer);
            Assert.Equal(baselineWidth, presenter.ActualWidth);

            vertical.Opacity = 0.0;
            Arrange(scrollViewer);
            Assert.Equal(baselineWidth, presenter.ActualWidth);
        });
    }

    [Fact]
    public void SharedResourceKeys_RemainBackwardCompatible()
    {
        Sta.Run(() =>
        {
            var resources = new SettingResources();

            Assert.IsType<Style>(resources["SettingScrollBarStyle"]);
            Assert.IsType<Style>(resources["SettingScrollViewerStyle"]);
            Assert.IsType<Style>(resources["SettingComboBoxItemStyle"]);
            Assert.IsType<Style>(resources["SettingComboBoxStyle"]);
            Assert.IsType<Style>(resources["SettingListStyle"]);
        });
    }

    [Fact]
    public void ComboBox_SelectedObjectHonorsDisplayMemberPath()
    {
        Sta.Run(() =>
        {
            var choice = new DisplayChoice("Chapter 1");
            var comboBox = WithResources(new ComboBox
            {
                Width = 240,
                ItemsSource = new[] { choice },
                DisplayMemberPath = nameof(DisplayChoice.Title),
                SelectedItem = choice,
            });
            comboBox.Style = (Style)comboBox.FindResource("SettingComboBoxStyle");
            Arrange(comboBox);

            Assert.Contains(
                Descendants<TextBlock>(comboBox),
                text => text.Text == choice.Title);
            Assert.DoesNotContain(
                Descendants<TextBlock>(comboBox),
                text => text.Text?.Contains(nameof(DisplayChoice), StringComparison.Ordinal) == true);
        });
    }

    [Fact]
    public void ComboBox_SelectedPresenterHonorsPerInstancePadding()
    {
        Sta.Run(() =>
        {
            var expected = new Thickness(4, 6, 20, 6);
            var comboBox = WithResources(new ComboBox
            {
                Width = 120,
                Padding = expected,
                ItemsSource = new[] { "Chapter 2" },
                SelectedIndex = 0,
            });
            comboBox.Style = (Style)comboBox.FindResource("SettingComboBoxStyle");
            Arrange(comboBox);
            var selectedPresenter = Descendants<ContentPresenter>(comboBox)
                .Single(presenter => Equals(presenter.Content, comboBox.SelectionBoxItem));

            Assert.Equal(expected, selectedPresenter.Margin);
        });
    }

    [Fact]
    public void Slider_TemplateCarriesDirectionRangeAndValueToItsTrack()
    {
        Sta.Run(() =>
        {
            var slider = WithResources(new SettingSlider
            {
                Width = 240,
                Minimum = 1,
                Maximum = 60,
                Value = 5,
                IsDirectionReversed = true,
            });
            Arrange(slider);
            var track = Descendant<Track>(slider);

            Assert.True(track.IsDirectionReversed);
            Assert.Equal(1, track.Minimum);
            Assert.Equal(60, track.Maximum);
            Assert.Equal(5, track.Value);
        });
    }

    [Fact]
    public void Drawer_IsClosedByDefault()
    {
        Sta.Run(() =>
        {
            var drawer = new SettingDrawer();
            Assert.False(drawer.IsOpen);
        });
    }

    [Fact]
    public void Drawer_OpensAndClosesViaProperty()
    {
        Sta.Run(() =>
        {
            // IsOpen=true before Arrange → OnApplyTemplate snaps to X=0.
            var drawer = WithResources(new SettingDrawer { Width = 800, Height = 400, IsOpen = true });
            Arrange(drawer);

            var transform = FindSlideTransform(drawer);
            var surface = Descendants<Border>(drawer)
                .Single(border => border.Name == "PART_DrawerSurface");
            Assert.Equal(0.0, transform.X);
            Assert.Equal(200, drawer.PanelWidth);
            Assert.Equal(200, surface.ActualWidth);
            Assert.True(drawer.IsOpen);

            // Close: DP changes and animation / snap is initiated.
            drawer.IsOpen = false;
            Assert.False(drawer.IsOpen);
            PumpFor(TimeSpan.FromMilliseconds(260));
            Assert.Equal(-200, transform.X, 3);
            Assert.False(transform.HasAnimatedProperties);

            drawer.IsOpen = true;
            PumpFor(TimeSpan.FromMilliseconds(260));
            Assert.Equal(0, transform.X, 3);
            Assert.False(transform.HasAnimatedProperties);
        });
    }

    [Fact]
    public void Drawer_OutsidePanelHasNoInputShieldOrBuiltInBackdrop()
    {
        Sta.Run(() =>
        {
            var drawer = WithResources(new SettingDrawer
            {
                Width = 800,
                Height = 400,
                IsOpen = true,
                Content = new Border { Background = Brushes.Red },
            });
            Arrange(drawer);

            var root = Descendants<Grid>(drawer).Single(grid => grid.Name == "PART_Root");
            Assert.Null(root.Background);
            Assert.DoesNotContain(
                Descendants<Border>(drawer),
                border => border.Name.Contains("Backdrop", StringComparison.Ordinal));
            Assert.NotNull(VisualTreeHelper.HitTest(drawer, new Point(100, 200)));
            Assert.Null(VisualTreeHelper.HitTest(drawer, new Point(700, 200)));
        });
    }

    [Fact]
    public void Drawer_UnloadCancelsAnimationAndReloadSnapsToCurrentState()
    {
        Sta.Run(() =>
        {
            var drawer = WithResources(new SettingDrawer { Width = 800, Height = 400 });
            Arrange(drawer);
            drawer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            drawer.IsOpen = true;
            drawer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

            var transform = FindSlideTransform(drawer);
            Assert.False(transform.HasAnimatedProperties);

            drawer.IsOpen = false;
            drawer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(-200, transform.X);
        });
    }

    [Fact]
    public void WindowChrome_TitleBindingWorks()
    {
        Sta.Run(() =>
        {
            var chrome = new SettingWindowChrome { Title = "Test Title", Width = 400, Height = 36 };
            Arrange(chrome);

            var titleBlock = Descendants<TextBlock>(chrome)
                .Single(tb => tb.Name == "TitleText");
            Assert.Equal("Test Title", titleBlock.Text);
        });
    }

    [Theory]
    [InlineData(105, 100, 0, false, 1, true)]
    [InlineData(107, 100, 0, false, 1, false)]
    [InlineData(0, -7, 0, true, 1, true)]
    [InlineData(6, -7, 0, true, 1, true)]
    [InlineData(7, -7, 0, true, 1, false)]
    [InlineData(109, 100, 0, false, 1.5, true)]
    [InlineData(110, 100, 0, false, 1.5, false)]
    public void WindowChrome_TopTriggerUsesVisibleMonitorEdgeAndDpi(
        double pointerY,
        double contentTop,
        double monitorTop,
        bool maximized,
        double dpiScale,
        bool expected)
    {
        Assert.Equal(
            expected,
            SettingWindowChrome.IsWithinVisibleTopTrigger(
                pointerY,
                contentTop,
                monitorTop,
                maximized,
                dpiScale));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void WindowChrome_HoldsWhilePointerFocusCaptureOrSystemActionIsActive(
        bool mouseOver,
        bool keyboardFocus,
        bool mouseCapture,
        bool systemAction)
    {
        Assert.True(SettingWindowChrome.ShouldHoldVisibility(
            mouseOver,
            keyboardFocus,
            mouseCapture,
            systemAction));
        Assert.False(SettingWindowChrome.ShouldHoldVisibility(false, false, false, false));
    }

    [Theory]
    [InlineData(ResizeMode.NoResize, false)]
    [InlineData(ResizeMode.CanMinimize, false)]
    [InlineData(ResizeMode.CanResize, true)]
    [InlineData(ResizeMode.CanResizeWithGrip, true)]
    public void WindowChrome_UsesOneResizePolicyForButtonsAndSurfaceGestures(
        ResizeMode resizeMode,
        bool expected)
    {
        Assert.Equal(expected, SettingWindowChrome.CanResize(resizeMode));
    }

    [Fact]
    public void WindowChrome_RejectedNativeDragIsNonFatal()
    {
        Assert.False(SettingWindowChrome.TryDragMove(
            () => throw new InvalidOperationException("native drag rejected")));
        Assert.False(SettingWindowChrome.TryDragMove(
            () => throw new System.ComponentModel.Win32Exception("native drag failed")));
    }

    [Fact]
    public void WindowChrome_InitialRevealFadeAndReloadLifecycleAreClosed()
    {
        Sta.Run(() =>
        {
            var chrome = WithResources(new SettingWindowChrome { Title = "Reader" });
            var window = new Window
            {
                Width = 400,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = chrome,
            };

            try
            {
                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(40));
                Assert.True(chrome.HasActiveSession);
                Assert.True(chrome.IsChromeVisible);
                Assert.True(chrome.IsHitTestVisible);
                Assert.Equal(1, chrome.Opacity, 3);
                Assert.NotNull(WindowChrome.GetWindowChrome(window));

                PumpFor(TimeSpan.FromMilliseconds(760));
                Assert.False(chrome.IsChromeVisible);
                Assert.False(chrome.IsHitTestVisible);
                Assert.Equal(0, chrome.Opacity, 2);

                chrome.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.False(chrome.HasActiveSession);
                Assert.Null(WindowChrome.GetWindowChrome(window));

                chrome.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.True(chrome.HasActiveSession);
                Assert.True(chrome.IsChromeVisible);
                Assert.True(chrome.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WindowChrome_MaxRestoreActionUsesOwningWindow()
    {
        Sta.Run(() =>
        {
            var chrome = WithResources(new SettingWindowChrome());
            var window = new Window
            {
                Width = 400,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = chrome,
            };

            try
            {
                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(40));
                var button = Descendants<ButtonBase>(chrome)
                    .Single(candidate => candidate.Name == "MaxRestoreButton");
                var minimise = Descendants<ButtonBase>(chrome)
                    .Single(candidate => candidate.Name == "MinimizeButton");
                var close = Descendants<ButtonBase>(chrome)
                    .Single(candidate => candidate.Name == "CloseButton");

                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(WindowState.Maximized, window.WindowState);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(WindowState.Normal, window.WindowState);

                window.ResizeMode = ResizeMode.NoResize;
                Assert.False(button.IsEnabled);
                window.ResizeMode = ResizeMode.CanResize;
                Assert.True(button.IsEnabled);

                minimise.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(WindowState.Minimized, window.WindowState);
                window.WindowState = WindowState.Normal;

                var closeRequested = false;
                System.ComponentModel.CancelEventHandler cancelClose = (_, args) =>
                {
                    closeRequested = true;
                    args.Cancel = true;
                };
                window.Closing += cancelClose;
                close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.Closing -= cancelClose;
                Assert.True(closeRequested);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static System.Windows.Media.TranslateTransform FindSlideTransform(SettingDrawer drawer)
    {
        var surface = Descendants<Border>(drawer).Single(b => b.Name == "PART_DrawerSurface");
        return (System.Windows.Media.TranslateTransform)surface.RenderTransform;
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static T WithResources<T>(T element) where T : FrameworkElement
    {
        element.Resources.MergedDictionaries.Add(new SettingResources());
        return element;
    }

    private static T Arrange<T>(T element) where T : FrameworkElement
    {
        var width = double.IsNaN(element.Width) ? 640 : element.Width;
        var height = double.IsNaN(element.Height) ? 240 : element.Height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.ApplyTemplate();
        element.UpdateLayout();
        return element;
    }

    private static T Descendant<T>(DependencyObject root) where T : DependencyObject =>
        Descendants<T>(root).Single();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed record DisplayChoice(string Title);
}

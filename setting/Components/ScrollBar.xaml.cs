using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Citadel.Setting.Components;

/// <summary>Shared scrollbar presentation dictionary.</summary>
public partial class SettingScrollBar : ResourceDictionary
{
    public SettingScrollBar() => InitializeComponent();
}

/// <summary>
/// Owns the reveal, idle-fade, and cleanup lifecycle for a shared ScrollViewer.
/// One session coordinates both axes so reloads cannot duplicate subscriptions.
/// </summary>
public static class ScrollBarAutoFade
{
    private static readonly ConditionalWeakTable<ScrollViewer, FadeSession> Sessions = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ScrollBarAutoFade),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(IsEnabledProperty, value);

    internal static bool HasActiveSession(ScrollViewer viewer) =>
        Sessions.TryGetValue(viewer, out _);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        viewer.Loaded -= OnViewerLoaded;
        viewer.Unloaded -= OnViewerUnloaded;

        if ((bool)args.NewValue)
        {
            viewer.Loaded += OnViewerLoaded;
            viewer.Unloaded += OnViewerUnloaded;
            if (viewer.IsLoaded)
            {
                Attach(viewer);
            }
        }
        else
        {
            Detach(viewer);
        }
    }

    private static void OnViewerLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ScrollViewer viewer && GetIsEnabled(viewer))
        {
            Attach(viewer);
        }
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is ScrollViewer viewer)
        {
            Detach(viewer);
        }
    }

    private static void Attach(ScrollViewer viewer)
    {
        if (Sessions.TryGetValue(viewer, out _))
        {
            return;
        }

        viewer.ApplyTemplate();
        var vertical = FindScrollBar(viewer, Orientation.Vertical);
        var horizontal = FindScrollBar(viewer, Orientation.Horizontal);
        var session = new FadeSession(viewer, vertical, horizontal);
        Sessions.Add(viewer, session);
        session.Attach();
    }

    private static void Detach(ScrollViewer viewer)
    {
        if (!Sessions.TryGetValue(viewer, out var session))
        {
            return;
        }

        session.Dispose();
        Sessions.Remove(viewer);
    }

    private static ScrollBar? FindScrollBar(ScrollViewer viewer, Orientation orientation)
    {
        var partName = orientation == Orientation.Vertical
            ? "PART_VerticalScrollBar"
            : "PART_HorizontalScrollBar";
        return viewer.Template?.FindName(partName, viewer) as ScrollBar;
    }

    private sealed class FadeSession : IDisposable
    {
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(250);

        private readonly ScrollViewer _viewer;
        private readonly ScrollBar? _vertical;
        private readonly ScrollBar? _horizontal;
        private readonly Thumb? _verticalThumb;
        private readonly Thumb? _horizontalThumb;
        private readonly DispatcherTimer _idleTimer;
        private bool _isDragging;
        private bool _disposed;

        public FadeSession(ScrollViewer viewer, ScrollBar? vertical, ScrollBar? horizontal)
        {
            _viewer = viewer;
            _vertical = vertical;
            _horizontal = horizontal;
            _verticalThumb = vertical?.Track?.Thumb;
            _horizontalThumb = horizontal?.Track?.Thumb;
            _idleTimer = new DispatcherTimer(DispatcherPriority.Background, viewer.Dispatcher)
            {
                Interval = IdleTimeout,
            };
        }

        public void Attach()
        {
            _viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            _viewer.PreviewKeyDown += OnPreviewKeyDown;
            _viewer.ScrollChanged += OnScrollChanged;
            _idleTimer.Tick += OnIdleTimerTick;
            AttachBar(_vertical);
            AttachBar(_horizontal);
            AttachThumb(_verticalThumb);
            AttachThumb(_horizontalThumb);
            HideImmediately();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _idleTimer.Stop();
            _idleTimer.Tick -= OnIdleTimerTick;
            _viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            _viewer.PreviewKeyDown -= OnPreviewKeyDown;
            _viewer.ScrollChanged -= OnScrollChanged;
            DetachThumb(_verticalThumb);
            DetachThumb(_horizontalThumb);
            DetachBar(_vertical);
            DetachBar(_horizontal);
            ClearAnimation(_vertical);
            ClearAnimation(_horizontal);
        }

        private void AttachBar(ScrollBar? bar)
        {
            if (bar is null)
            {
                return;
            }

            bar.MouseEnter += OnBarMouseEnter;
            bar.MouseLeave += OnBarMouseLeave;
            bar.GotKeyboardFocus += OnBarGotKeyboardFocus;
            bar.LostKeyboardFocus += OnBarLostKeyboardFocus;
        }

        private void DetachBar(ScrollBar? bar)
        {
            if (bar is null)
            {
                return;
            }

            bar.MouseEnter -= OnBarMouseEnter;
            bar.MouseLeave -= OnBarMouseLeave;
            bar.GotKeyboardFocus -= OnBarGotKeyboardFocus;
            bar.LostKeyboardFocus -= OnBarLostKeyboardFocus;
        }

        private void AttachThumb(Thumb? thumb)
        {
            if (thumb is null)
            {
                return;
            }

            thumb.DragStarted += OnThumbDragStarted;
            thumb.DragCompleted += OnThumbDragCompleted;
        }

        private void DetachThumb(Thumb? thumb)
        {
            if (thumb is null)
            {
                return;
            }

            thumb.DragStarted -= OnThumbDragStarted;
            thumb.DragCompleted -= OnThumbDragCompleted;
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args) => Reveal();

        private void OnPreviewKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key is Key.Up or Key.Down or Key.Left or Key.Right
                or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            {
                Reveal();
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (args.VerticalChange != 0 || args.HorizontalChange != 0)
            {
                Reveal();
            }
        }

        private void OnBarMouseEnter(object sender, MouseEventArgs args)
        {
            Reveal();
            _idleTimer.Stop();
        }

        private void OnBarMouseLeave(object sender, MouseEventArgs args) => ScheduleHide();

        private void OnBarGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
        {
            Reveal();
            _idleTimer.Stop();
        }

        private void OnBarLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args) => ScheduleHide();

        private void OnThumbDragStarted(object sender, DragStartedEventArgs args)
        {
            _isDragging = true;
            Reveal();
            _idleTimer.Stop();
        }

        private void OnThumbDragCompleted(object sender, DragCompletedEventArgs args)
        {
            _isDragging = false;
            ScheduleHide();
        }

        private void OnIdleTimerTick(object? sender, EventArgs args)
        {
            _idleTimer.Stop();
            if (!IsVisibilityHeld())
            {
                AnimateTo(0, HideDuration);
            }
        }

        private void Reveal()
        {
            if (_disposed || !HasOverflow())
            {
                return;
            }

            AnimateTo(1, RevealDuration);
            if (IsVisibilityHeld())
            {
                _idleTimer.Stop();
            }
            else
            {
                RestartIdleTimer();
            }
        }

        private void ScheduleHide()
        {
            if (!_disposed && HasOverflow() && !IsVisibilityHeld())
            {
                RestartIdleTimer();
            }
        }

        private void RestartIdleTimer()
        {
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        private bool HasOverflow() => IsScrollable(_vertical) || IsScrollable(_horizontal);

        private bool IsVisibilityHeld() =>
            _isDragging
            || (_vertical?.IsMouseOver ?? false)
            || (_horizontal?.IsMouseOver ?? false)
            || (_vertical?.IsKeyboardFocusWithin ?? false)
            || (_horizontal?.IsKeyboardFocusWithin ?? false);

        private static bool IsScrollable(ScrollBar? bar) =>
            bar is { Visibility: Visibility.Visible, Maximum: > 0 };

        private void HideImmediately()
        {
            _idleTimer.Stop();
            SetBaseOpacity(_vertical, 0);
            SetBaseOpacity(_horizontal, 0);
        }

        private void AnimateTo(double target, TimeSpan duration)
        {
            AnimateBar(_vertical, target, duration);
            AnimateBar(_horizontal, target, duration);
        }

        private static void AnimateBar(ScrollBar? bar, double target, TimeSpan duration)
        {
            if (bar is null || bar.Visibility != Visibility.Visible || bar.Maximum <= 0)
            {
                return;
            }

            var current = bar.Opacity;
            bar.BeginAnimation(UIElement.OpacityProperty, null);
            bar.Opacity = current;
            if (!SystemParameters.ClientAreaAnimation)
            {
                bar.Opacity = target;
                return;
            }

            bar.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(current, target, duration)
                {
                    FillBehavior = FillBehavior.HoldEnd,
                });
        }

        private static void ClearAnimation(ScrollBar? bar)
        {
            if (bar is not null)
            {
                bar.BeginAnimation(UIElement.OpacityProperty, null);
            }
        }

        private static void SetBaseOpacity(ScrollBar? bar, double opacity)
        {
            if (bar is null)
            {
                return;
            }

            bar.BeginAnimation(UIElement.OpacityProperty, null);
            bar.Opacity = opacity;
        }
    }
}

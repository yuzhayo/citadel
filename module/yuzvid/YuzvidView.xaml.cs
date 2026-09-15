using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Citadel.Core.Modules;
using Module.Yuzvid.Features.Browser;

namespace Module.Yuzvid;

/// <summary>
/// Yuzvid presentation shell. Browser tab hosts WebView2; Queue and Settings tabs are placeholders.
/// Implements IRetainedViewModule so the browser state survives navigation away.
/// </summary>
public partial class YuzvidView : UserControl, IRetainedViewModule
{
    private bool _navigating;
    private readonly IYuzvidBrowserController _browser;
    private readonly FrameworkElement _settingsView;
    private bool _settingsMounted;
    private readonly Dictionary<string, string> _urlLookup = new(); // display → full URL
    private const int MaxDisplayUrl = 80;

    // Bookmark storage
    private readonly List<BookmarkItem> _bookmarks = new();
    private static readonly string BookmarksPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel", "Yuzvid", "bookmarks.json");

    private sealed class BookmarkItem
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string DisplayUrl { get; set; } = "";
        public DateTime AddedAt { get; set; }
    }

    /// <summary>
    /// Presentation shell. Owns tabs, bookmarks, and toolbar state only.
    /// The Browser (view + proxy + pool) is owned by the injected
    /// <see cref="IYuzvidBrowserController"/> — created and activated once
    /// by <see cref="YuzvidModule"/>; this shell only forwards commands.
    /// </summary>
    public YuzvidView(IYuzvidBrowserController browser, FrameworkElement settingsView)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _settingsView = settingsView ?? throw new ArgumentNullException(nameof(settingsView));
        InitializeComponent();
        LoadBookmarks();

        // Mount the controller-owned Browser view. The controller was already
        // activated by the composition root (port pinned before WebView2 load).
        BrowserHost.Content = _browser.View;

        // Wire browser events to toolbar
        _browser.Navigated += Browser_Navigated;
        _browser.NavigationStateChanged += Browser_NavigationStateChanged;
        _browser.NavigationFailed += Browser_NavigationFailed;
        _browser.NavigationQueued += Browser_NavigationQueued;
        _browser.StateChanged += Browser_StateChanged;

        // Start with buttons disabled (browser initializing)
        UpdateToolbarButtons(false);
        DnsModePicker.SelectedIndex = 0;
    }

    // ─── Toolbar: Navigation ──────────────────────────────────────────────

    private void UrlField_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateFromAddress();
        }
    }

    private void GoButton_Click(object sender, RoutedEventArgs e) => NavigateFromAddress();

    private void NavigateFromAddress()
    {
        NavigationStatus.Visibility = Visibility.Collapsed;
        _browser.Navigate(UrlField.Text);
    }

    private void UrlField_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UrlField.SelectedItem is string selected && selected.Length > 0)
        {
            // Resolve full URL from lookup if it was truncated
            var fullUrl = _urlLookup.TryGetValue(selected, out var url) ? url : selected;
            UrlField.Text = fullUrl;
            NavigateFromAddress();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _browser.GoBack();
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => _browser.GoForward();
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _browser.Refresh();

    private void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        // Phase 3: will trigger url_extractor on current page
        var currentUrl = _browser.CurrentUrl;
        if (string.IsNullOrEmpty(currentUrl)) return;

        System.Diagnostics.Debug.WriteLine($"[Yuzvid] Extract requested for: {currentUrl}");
        // TODO Phase 3: open link drawer
    }

    private void ProxyToggle_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = ProxyToggle.IsChecked == true;
        _browser.SetProxyEnabled(enabled);

        // Outcome (including pool-failure fallback) comes from the controller snapshot.
        var runtime = _browser.Runtime;
        var notice = runtime.Notice;
        if (enabled && !runtime.ProxyEnabled)
        {
            // Pool failure — controller already fell back to direct; reflect in toggle.
            // This re-fires the handler with enabled=false (idempotent); restore message after.
            ProxyToggle.IsChecked = false;
        }
        if (!string.IsNullOrEmpty(notice))
        {
            NavigationStatus.Text = notice;
            NavigationStatus.Visibility = Visibility.Visible;
        }
    }

    private void DnsModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DnsModePicker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (!Enum.TryParse<YuzvidDnsMode>(tag, out var mode)) return;

        _browser.SetDnsMode(mode);
        NavigationStatus.Text = mode == YuzvidDnsMode.System
            ? "Browser memakai DNS sistem/network."
            : "Browser memakai " + mode + " Secure DNS untuk koneksi langsung.";
        NavigationStatus.Visibility = Visibility.Visible;
    }

    // ─── Browser Events ───────────────────────────────────────────────────

    private void Browser_StateChanged(object? sender, BrowserState state)
    {
        Dispatcher.Invoke(() =>
        {
            var ready = state == BrowserState.Ready;
            UpdateToolbarButtons(ready);

        });
    }

    private void Browser_Navigated(object? sender, string url)
    {
        _navigating = false;

        Dispatcher.Invoke(() =>
        {
            // Sync URL field (only if user isn't typing)
            if (!UrlField.IsKeyboardFocused)
            {
                UrlField.Text = url;
            }

            // Add to URL history dropdown (truncated display, full URL in lookup)
            if (!string.IsNullOrEmpty(url) && !_urlLookup.ContainsValue(url))
            {
                var display = url.Length > MaxDisplayUrl
                    ? url[..MaxDisplayUrl] + "..."
                    : url;
                // Dedup display text by appending counter if needed
                var key = display;
                var counter = 1;
                while (_urlLookup.ContainsKey(key))
                {
                    key = display + $" ({++counter})";
                }
                _urlLookup[key] = url;
                UrlField.Items.Insert(0, key);
                if (UrlField.Items.Count > 50)
                {
                    var removed = UrlField.Items[^1];
                    UrlField.Items.RemoveAt(UrlField.Items.Count - 1);
                    if (removed is string removedKey)
                        _urlLookup.Remove(removedKey);
                }
            }

            // Update navigation button states
            BackButton.IsEnabled = _browser.CanGoBack;
            ForwardButton.IsEnabled = _browser.CanGoForward;
            ExtractButton.IsEnabled = !string.IsNullOrEmpty(url);
        });
    }

    private void Browser_NavigationStateChanged(object? sender, bool isNavigating)
    {
        _navigating = isNavigating;
        if (isNavigating)
        {
            NavigationStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void Browser_NavigationFailed(object? sender, string message)
    {
        Dispatcher.Invoke(() =>
        {
            NavigationStatus.Text = message;
            NavigationStatus.Visibility = Visibility.Visible;
        });
    }

    private void Browser_NavigationQueued(object? sender, string message)
    {
        Dispatcher.Invoke(() =>
        {
            NavigationStatus.Text = message;
            NavigationStatus.Visibility = Visibility.Visible;
        });
    }

    // ─── Settings Tab ─────────────────────────────────────────────────────

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Tab index 2 = Settings. The view is pre-built by the composition root;
        // mount it lazily — its first refresh runs on its own Loaded event.
        if (WorkspaceTabs.SelectedIndex == 2 && !_settingsMounted)
        {
            _settingsMounted = true;
            SettingsWorkspace.Children.Add(_settingsView);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private void UpdateToolbarButtons(bool browserReady)
    {
        GoButton.IsEnabled = browserReady;
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
        RefreshButton.IsEnabled = browserReady;
        ExtractButton.IsEnabled = false;
    }

    // ─── Bookmarks ────────────────────────────────────────────────────────

    private void BookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        BookmarkPopup.IsOpen = !BookmarkPopup.IsOpen;
        if (BookmarkPopup.IsOpen)
        {
            RefreshBookmarkList();
        }
    }

    private void BookmarkAddCurrent_Click(object sender, RoutedEventArgs e)
    {
        AddCurrentToBookmarks();
        RefreshBookmarkList();
    }

    private void BookmarkItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem item)
        {
            _browser.Navigate(item.Url);
            BookmarkPopup.IsOpen = false;
        }
    }

    private void BookmarkNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
        {
            _browser.Navigate(url);
            BookmarkPopup.IsOpen = false;
        }
    }

    private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
        {
            _bookmarks.RemoveAll(b => b.Url == url);
            SaveBookmarks();
            RefreshBookmarkList();
        }
    }

    private void AddCurrentToBookmarks()
    {
        var url = _browser.CurrentUrl;
        var title = _browser.CurrentTitle;
        if (string.IsNullOrEmpty(url) || url == "about:blank") return;

        // Dedup
        if (_bookmarks.Exists(b => b.Url == url)) return;

        _bookmarks.Insert(0, new BookmarkItem
        {
            Title = string.IsNullOrEmpty(title) ? url : title,
            Url = url,
            DisplayUrl = url.Length > MaxDisplayUrl ? url[..MaxDisplayUrl] + "..." : url,
            AddedAt = DateTime.UtcNow
        });
        SaveBookmarks();
    }

    private void RefreshBookmarkList()
    {
        BookmarkList.ItemsSource = null;
        BookmarkList.ItemsSource = _bookmarks;
        BookmarkEmpty.Visibility = _bookmarks.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        BookmarkCount.Text = $"{_bookmarks.Count} bookmark{(_bookmarks.Count != 1 ? "s" : "")}";
    }

    private void LoadBookmarks()
    {
        try
        {
            if (File.Exists(BookmarksPath))
            {
                var json = File.ReadAllText(BookmarksPath);
                var items = JsonSerializer.Deserialize<List<BookmarkItem>>(json);
                if (items is not null)
                    _bookmarks.AddRange(items);
            }
        }
        catch { /* corrupt file — start fresh */ }
    }

    private void SaveBookmarks()
    {
        try
        {
            var dir = Path.GetDirectoryName(BookmarksPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_bookmarks, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(BookmarksPath, json);
        }
        catch { /* best effort */ }
    }

    // ─── IRetainedViewModule ─────────────────────────────────────────────

    /// <summary>
    /// Called when the Router detaches this view (user navigated away).
    /// WebView2 stays alive — just hidden from the visual tree.
    /// </summary>
    public void OnViewDetached()
    {
        // No-op: the browser engine stays warm in the background.
        // Future: pause video playback, reduce timer frequency, etc.
    }

    /// <summary>
    /// Called when the Router re-attaches this view (user navigated back).
    /// The browser state is fully preserved.
    /// </summary>
    public void OnViewAttached()
    {
        // No-op: the browser picks up exactly where it left off.
        // Future: resume playback, refresh stale data, etc.
    }
}

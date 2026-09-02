using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Tokens;
using Citadel.Setting.Components;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>Stable composition root; feature behavior lives behind catalog contracts.</summary>
public partial class ReaderWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ReaderSessionState _state;
    private readonly ReaderCommandHub _commands;
    private readonly ReaderNotificationHub _notifications;
    private readonly ReaderPreferencesStore _preferences;
    private readonly ReaderActivityHub _activity;
    private readonly FrameContentHost _viewport;
    private readonly ReaderChapterCoordinator _coordinator;
    private readonly ReaderInputRouter _input;
    private readonly ReaderFeatureHost _featureHost;
    private bool _loadStarted;
    private bool _closed;

    public ReaderWindow(MangaTitle title, ChapterInfo chapter)
        : this(
            title,
            chapter,
            new ReaderPreferencesStore(),
            new CbzReaderChapterLoader())
    {
    }

    internal ReaderWindow(
        MangaTitle title,
        ChapterInfo chapter,
        ReaderPreferencesStore preferences,
        IReaderChapterLoader chapterLoader)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(chapterLoader);
        InitializeComponent();

        _preferences = preferences;
        _state = new ReaderSessionState(
            _preferences.Current.DimPercent,
            _preferences.Current.AutoScrollSecondsPerViewport);
        DataContext = _state;

        _commands = new ReaderCommandHub();
        _notifications = new ReaderNotificationHub();
        _activity = new ReaderActivityHub();
        _viewport = new FrameContentHost(ReaderScroller, ChapterList, _activity);
        var status = new ReaderStatusHost(
            ReaderScroller,
            LoadingPanel,
            LoadingTitle,
            LoadingDetail,
            LoadingProgress,
            CloseAfterErrorButton,
            _notifications);
        _coordinator = new ReaderChapterCoordinator(
            title,
            chapter,
            _viewport,
            status,
            _state,
            _activity,
            chapterLoader);
        ChapterList.ItemsSource = _coordinator.Surfaces;

        _input = new ReaderInputRouter(this, _viewport, _state, _commands, _activity);
        var context = new ReaderFeatureContext(
            _state,
            _commands,
            _viewport,
            _coordinator,
            _input,
            _activity,
            _notifications,
            _lifetime.Token);
        var hosts = new Dictionary<ReaderLayer, ContentControl>
        {
            [ReaderLayer.Dim] = DimLayer,
            [ReaderLayer.DrawerBackdrop] = DrawerBackdropLayer,
            [ReaderLayer.Overlay] = OverlayLayer,
            [ReaderLayer.Chrome] = ChromeLayer,
            [ReaderLayer.Drawer] = DrawerLayer,
            [ReaderLayer.Toast] = ToastLayer,
        };
        _featureHost = new ReaderFeatureHost(
            context,
            hosts,
            ReaderDefaultFeatureCatalog.Create(this, _state, _commands, _activity));

        _commands.CloseReaderRequested += Close;
        _coordinator.ActiveChapterChanged += OnActiveChapterChanged;
        _preferences.WarningRaised += OnPreferenceWarning;
        _preferences.Bind(_state);
        Loaded += OnLoaded;
        Closed += OnClosed;
        UpdateWindowTitle();
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowChromeBehavior.Apply(
            this,
            Defaults.All["BgRail"].Argb,
            Defaults.All["Fg"].Argb);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadStarted || _closed) return;
        _loadStarted = true;
        await _coordinator.StartLoadAsync();
        if (!_closed && !_state.HasError) ReaderScroller.Focus();
        if (!string.IsNullOrWhiteSpace(_preferences.LastWarning))
            _notifications.ShowToast(_preferences.LastWarning!, TimeSpan.FromSeconds(4));
    }

    private void OnActiveChapterChanged(object? sender, OpenChapterRequestedEventArgs e)
    {
        UpdateWindowTitle();
        ActiveChapterChanged?.Invoke(this, e);
    }

    private void UpdateWindowTitle() =>
        Title = $"{_coordinator.MangaTitle} — {_coordinator.ActiveChapter.Title} — Manga Reader";

    private void OnPreferenceWarning(object? sender, string warning) =>
        Dispatcher.BeginInvoke(() => _notifications.ShowToast(warning, TimeSpan.FromSeconds(4)));

    private void CloseAfterErrorButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        _commands.CloseReaderRequested -= Close;
        _coordinator.ActiveChapterChanged -= OnActiveChapterChanged;
        _preferences.WarningRaised -= OnPreferenceWarning;
        _lifetime.Cancel();
        _input.Dispose();
        _featureHost.Dispose();
        _preferences.Flush();
        _preferences.Dispose();
        _coordinator.Dispose();
        _viewport.Dispose();
        ChapterList.ItemsSource = null;
        DataContext = null;
        _lifetime.Dispose();
    }
}

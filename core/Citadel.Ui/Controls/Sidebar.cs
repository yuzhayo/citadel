using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Ui.Animations;
using Citadel.Ui.Theme;

namespace Citadel.Ui.Controls;

/// <summary>
/// Core-owned navigation chrome. Rows use one token-driven two-column geometry,
/// so icon and title positions cannot drift between main and pinned entries.
///
/// Full width is a pure calculation over entries, token values, and the exact
/// typography used to render them. It is invoked for collection changes,
/// TokensChanged, and typography changes; no stage-4 registry is invented here.
/// Both FormattedText and the templates pin TextFormattingMode.Display.
/// </summary>
[TemplatePart(Name = NavListPart, Type = typeof(ListBox))]
[TemplatePart(Name = PinListPart, Type = typeof(ListBox))]
public sealed class Sidebar : Control
{
    internal const string NavListPart = "PART_NavList";
    internal const string PinListPart = "PART_PinList";

    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(180);

    private static readonly DependencyPropertyKey CurrentWidthPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(CurrentWidth),
            typeof(double),
            typeof(Sidebar),
            new FrameworkPropertyMetadata(Defaults.All["FullDefault"].Number));

    public static readonly DependencyProperty CurrentWidthProperty =
        CurrentWidthPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey FullWidthPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(FullWidth),
            typeof(double),
            typeof(Sidebar),
            new FrameworkPropertyMetadata(Defaults.All["FullDefault"].Number));

    public static readonly DependencyProperty FullWidthProperty =
        FullWidthPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey TitleOpacityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(TitleOpacity),
            typeof(double),
            typeof(Sidebar),
            new FrameworkPropertyMetadata(1d));

    public static readonly DependencyProperty TitleOpacityProperty =
        TitleOpacityPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsCollapsedProperty =
        DependencyProperty.Register(
            nameof(IsCollapsed),
            typeof(bool),
            typeof(Sidebar),
            new FrameworkPropertyMetadata(false, OnIsCollapsedChanged));

    public static readonly DependencyProperty SelectedRouteProperty =
        DependencyProperty.Register(
            nameof(SelectedRoute),
            typeof(string),
            typeof(Sidebar),
            new FrameworkPropertyMetadata(NavEntry.Settings.Route));

    public static readonly DependencyProperty TitleFontFamilyProperty =
        DependencyProperty.Register(
            nameof(TitleFontFamily),
            typeof(FontFamily),
            typeof(Sidebar),
            new FrameworkPropertyMetadata(
                new FontFamily("Segoe UI"),
                FrameworkPropertyMetadataOptions.AffectsMeasure,
                OnTypographyChanged));

    public static readonly DependencyProperty TitleFontSizeProperty =
        DependencyProperty.Register(
            nameof(TitleFontSize),
            typeof(double),
            typeof(Sidebar),
            new FrameworkPropertyMetadata(
                13.5d,
                FrameworkPropertyMetadataOptions.AffectsMeasure,
                OnTypographyChanged),
            value => value is double size && double.IsFinite(size) && size > 0);

    private readonly ObservableCollection<NavEntry> _entries = [];
    private readonly ObservableCollection<NavEntry> _mainEntries = [];
    private readonly ObservableCollection<NavEntry> _pinnedEntries = [];
    private readonly ReadOnlyObservableCollection<NavEntry> _readonlyMainEntries;
    private readonly ReadOnlyObservableCollection<NavEntry> _readonlyPinnedEntries;
    private Tokens? _tokens;
    private AnimationManager? _animations;
    private Lifetime? _ownerLifetime;
    private Lifetime? _transitionLifetime;
    private ThemeResources? _themeResources;
    private ListBox? _navList;
    private ListBox? _pinList;
    private bool _animateNextChange = true;

    static Sidebar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(Sidebar),
            new FrameworkPropertyMetadata(typeof(Sidebar)));
    }

    public Sidebar()
    {
        _readonlyMainEntries = new(_mainEntries);
        _readonlyPinnedEntries = new(_pinnedEntries);
        _entries.CollectionChanged += OnEntriesChanged;
        RebuildViews();
    }

    /// <summary>Registered entries only; Settings is supplied by the control.</summary>
    public ObservableCollection<NavEntry> Entries => _entries;

    public ReadOnlyObservableCollection<NavEntry> MainEntries => _readonlyMainEntries;

    public ReadOnlyObservableCollection<NavEntry> PinnedEntries => _readonlyPinnedEntries;

    public double CurrentWidth => (double)GetValue(CurrentWidthProperty);

    public double FullWidth => (double)GetValue(FullWidthProperty);

    public double TitleOpacity => (double)GetValue(TitleOpacityProperty);

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    public string? SelectedRoute
    {
        get => (string?)GetValue(SelectedRouteProperty);
        set => SetValue(SelectedRouteProperty, value);
    }

    public FontFamily TitleFontFamily
    {
        get => (FontFamily)GetValue(TitleFontFamilyProperty);
        set => SetValue(TitleFontFamilyProperty, value);
    }

    public double TitleFontSize
    {
        get => (double)GetValue(TitleFontSizeProperty);
        set => SetValue(TitleFontSizeProperty, value);
    }

    /// <summary>Raised only for a user/list selection; setting SelectedRoute is silent.</summary>
    public event Action<string>? RouteSelected;

    public void Attach(Tokens tokens, AnimationManager animations, Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (_tokens is not null) throw new InvalidOperationException("sidebar is already attached");

        _tokens = tokens;
        _animations = animations;
        _ownerLifetime = lifetime;

        var resources = new ThemeResources();
        resources.Bind(tokens, lifetime);
        Resources.MergedDictionaries.Insert(0, resources);
        _themeResources = resources;

        tokens.TokensChanged += OnTokensChanged;
        lifetime.Add(() =>
        {
            tokens.TokensChanged -= OnTokensChanged;
            _transitionLifetime?.Destroy();
            _transitionLifetime = null;
            if (ReferenceEquals(_themeResources, resources))
            {
                Resources.MergedDictionaries.Remove(resources);
                _themeResources = null;
            }
            _tokens = null;
            _animations = null;
            _ownerLifetime = null;
        });

        RecomputeFullWidth();
        SnapToTarget();
    }

    public void SetCollapsed(bool collapsed, bool animate = true)
    {
        if (IsCollapsed == collapsed)
        {
            if (!animate) SnapToTarget();
            return;
        }

        _animateNextChange = animate;
        SetCurrentValue(IsCollapsedProperty, collapsed);
    }

    public override void OnApplyTemplate()
    {
        if (_navList is not null) _navList.SelectionChanged -= OnSelectionChanged;
        if (_pinList is not null) _pinList.SelectionChanged -= OnSelectionChanged;

        base.OnApplyTemplate();

        _navList = GetTemplateChild(NavListPart) as ListBox;
        _pinList = GetTemplateChild(PinListPart) as ListBox;

        if (_navList is not null) _navList.SelectionChanged += OnSelectionChanged;
        if (_pinList is not null) _pinList.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>
    /// Pure width derivation. The right-side breathing room is the same
    /// token-derived gap between the icon's right edge and TitleX; there is no
    /// v0-style chrome/slack constant.
    /// </summary>
    public static double CalculateFullWidth(
        IEnumerable<NavEntry> entries,
        Tokens tokens,
        FontFamily fontFamily,
        double fontSize,
        double pixelsPerDip = 1d)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(fontFamily);
        if (!double.IsFinite(fontSize) || fontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }
        if (!double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
        }

        var typeface = new Typeface(
            fontFamily,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        var widest = 0d;
        foreach (var entry in entries)
        {
            var text = entry.Title ?? string.Empty;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.Black,
                numberSubstitution: null,
                textFormattingMode: TextFormattingMode.Display,
                pixelsPerDip: pixelsPerDip);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }

        var rail = tokens.Number("Rail");
        var iconSlot = tokens.Number("IconSlot");
        var titleX = tokens.Number("TitleX");
        var iconRight = (rail + iconSlot) / 2d;
        var titleGap = Math.Max(0d, titleX - iconRight);
        var measured = titleX + Math.Ceiling(widest) + titleGap;
        var candidate = Math.Max(tokens.Number("FullDefault"), measured);
        return Math.Clamp(candidate, tokens.Number("FullMin"), tokens.Number("FullMax"));
    }

    private static void OnIsCollapsedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var sidebar = (Sidebar)sender;
        var animate = sidebar._animateNextChange;
        sidebar._animateNextChange = true;
        sidebar.TransitionToTarget(animate);
    }

    private static void OnTypographyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((Sidebar)sender).RecomputeFullWidth();

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RebuildViews();
        RecomputeFullWidth();
    }

    private void RebuildViews()
    {
        _mainEntries.Clear();
        _pinnedEntries.Clear();
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Route, NavEntry.Settings.Route, StringComparison.Ordinal)) continue;
            (entry.Pinned ? _pinnedEntries : _mainEntries).Add(entry);
        }
        _pinnedEntries.Add(NavEntry.Settings);
    }

    private void OnTokensChanged()
    {
        RecomputeFullWidth();
        SnapToTarget();
    }

    private void RecomputeFullWidth()
    {
        if (_tokens is null) return;

        var allEntries = _mainEntries.Concat(_pinnedEntries);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = CalculateFullWidth(allEntries, _tokens, TitleFontFamily, TitleFontSize, pixelsPerDip);
        SetValue(FullWidthPropertyKey, width);
        if (!IsCollapsed) SnapToTarget();
    }

    private void TransitionToTarget(bool animate)
    {
        var targetWidth = TargetWidth();
        var targetOpacity = IsCollapsed ? 0d : 1d;
        _transitionLifetime?.Destroy();
        _transitionLifetime = null;

        if (!animate
            || _animations is null
            || _ownerLifetime is null
            || !_ownerLifetime.Alive
            || (Math.Abs(CurrentWidth - targetWidth) < 0.001
                && Math.Abs(TitleOpacity - targetOpacity) < 0.001))
        {
            SetCurrent(targetWidth, targetOpacity);
            return;
        }

        var startWidth = CurrentWidth;
        var startOpacity = TitleOpacity;
        var transition = new Lifetime();
        _transitionLifetime = transition;

        var animation = _animations.Create(
            CollapseDuration,
            progress => SetCurrent(
                Lerp(startWidth, targetWidth, progress),
                Lerp(startOpacity, targetOpacity, progress)),
            Easings.EaseOutCubic,
            () =>
            {
                SetCurrent(targetWidth, targetOpacity);
                if (ReferenceEquals(_transitionLifetime, transition)) _transitionLifetime = null;
                transition.Destroy();
            });

        if (!animation.Start(transition))
        {
            if (ReferenceEquals(_transitionLifetime, transition)) _transitionLifetime = null;
            transition.Destroy();
            SetCurrent(targetWidth, targetOpacity);
        }
    }

    private void SnapToTarget()
    {
        _transitionLifetime?.Destroy();
        _transitionLifetime = null;
        SetCurrent(TargetWidth(), IsCollapsed ? 0d : 1d);
    }

    private double TargetWidth() => IsCollapsed
        ? _tokens?.Number("Rail") ?? Defaults.All["Rail"].Number
        : FullWidth;

    private void SetCurrent(double width, double opacity)
    {
        SetValue(CurrentWidthPropertyKey, width);
        SetValue(TitleOpacityPropertyKey, opacity);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count == 0 || args.AddedItems[0] is not NavEntry entry) return;

        SetCurrentValue(SelectedRouteProperty, entry.Route);
        if (ReferenceEquals(sender, _navList) && _pinList is not null) _pinList.SelectedIndex = -1;
        if (ReferenceEquals(sender, _pinList) && _navList is not null) _navList.SelectedIndex = -1;
        RouteSelected?.Invoke(entry.Route);
    }

    private static double Lerp(double from, double to, double progress) =>
        from + ((to - from) * progress);
}

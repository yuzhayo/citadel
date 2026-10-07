using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Tokens;
using Citadel.Setting.Components;

namespace Module.Yuzvid.Features.Extraction;

/// <summary>
/// Floating extraction window (feature-owned UI). Reuses the shared
/// <see cref="SettingTable"/> + <see cref="SettingButton"/> and the MangaReader
/// queue-style grouped projection (tri-state headers, expand/collapse) instead
/// of a hand-rolled Popup. Owned by <see cref="ExtractionFeature"/>; the parent
/// shell never constructs or references this type (guard YUZ-2).
/// </summary>
public partial class ExtractionWindow : Window
{
    private readonly ExtractionGroupProjection _projection = new();

    public event EventHandler<VideoLink>? CopyRequested;
    public event EventHandler<VideoLink>? QueueRequested;
    public event EventHandler<IReadOnlyList<VideoLink>>? QueueSelectionRequested;
    public event EventHandler? RescanRequested;

    /// <summary>Manual page URL submitted for navigation + extraction (P2).</summary>
    public event EventHandler<string>? ExtractUrlRequested;

    public ExtractionWindow()
    {
        InitializeComponent();
        LinkTable.ItemsSource = _projection.Visible;
        _projection.SelectionChanged += RefreshFooter;
        RefreshFooter();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowChromeBehavior.Apply(
            this,
            Defaults.All["BgRail"].Argb,
            Defaults.All["Fg"].Argb);
    }

    /// <summary>Populate the table from one extraction pass.</summary>
    public void ShowResults(ExtractionResult result)
    {
        _projection.Update(result.Links);
        var total = _projection.Visible.Count(r => !r.IsGroup);
        var empty = total == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // R2 fix: a pass with no links must say WHY (page empty / browser not
        // ready) instead of silently emptying the table and looking "broken".
        StatusText.Text = result.StaticSkipped
            ? result.StaticSkipReason ?? "Tidak ada link."
            : empty
                ? "Tidak ada link ditemukan di halaman ini."
                : $"{total} link · {result.PageUrl}";
        RefreshFooter();
    }

    private void RefreshFooter()
    {
        var selected = _projection.SelectedLinks.Count;
        var total = _projection.Visible.Count(r => !r.IsGroup);
        FooterText.Text = $"{total} link{(total != 1 ? "s" : "")} \u00b7 {selected} dipilih";
        QueueSelectedButton.Content = $"Queue selected ({selected})";
        QueueSelectedButton.IsEnabled = selected > 0;
        CopySelectedButton.IsEnabled = selected > 0;
    }

    // ─── Toolbar ──────────────────────────────────────────────────────────
    private void QueueSelected_Click(object sender, RoutedEventArgs e)
    {
        var links = _projection.SelectedLinks;
        if (links.Count == 0) return;
        QueueSelectionRequested?.Invoke(this, links);
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var links = _projection.SelectedLinks;
        if (links.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, links.Select(l => l.Full))); }
        catch { /* clipboard busy — best effort */ }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _projection.SelectAll(true);

    private void Clear_Click(object sender, RoutedEventArgs e) => _projection.SelectAll(false);

    private void RescanButton_Click(object sender, RoutedEventArgs e)
        => RescanRequested?.Invoke(this, EventArgs.Empty);

    // ─── Manual URL (P2) ─────────────────────────────────────────────────

    private void ManualUrlBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            SubmitManualUrl();
        }
    }

    private void ManualUrlGo_Click(object sender, RoutedEventArgs e) => SubmitManualUrl();

    private void SubmitManualUrl()
    {
        var url = ManualUrlBox.Text?.Trim() ?? string.Empty;
        if (url.Length == 0) return;
        ManualUrlBox.Text = string.Empty;
        ExtractUrlRequested?.Invoke(this, url);
    }

    // ─── Table row actions ────────────────────────────────────────────────
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ExtractionRow row) _projection.Toggle(row);
    }

    private void RowCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ExtractionRow { Link: { } link })
            CopyRequested?.Invoke(this, link);
    }

    private void RowQueue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ExtractionRow { Link: { } link })
            QueueRequested?.Invoke(this, link);
    }
}
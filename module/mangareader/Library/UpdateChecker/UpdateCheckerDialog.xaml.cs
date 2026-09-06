using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Citadel.Setting.Components;

namespace Module.Mangareader.Library.UpdateChecker;

/// <summary>
/// Update Checker's own popup surface. It renders the feature's state and
/// forwards exactly two commands: confirming one candidate match, and handing
/// the explicitly selected missing chapters to the queue route the composition
/// root supplied.
///
/// It owns no matching rule, no provider call and no persistence. It never
/// downloads directly, never refreshes or scans the Library, never fetches a
/// cover, and never runs after a chapter is published.
/// </summary>
public partial class UpdateCheckerDialog : SettingDialog
{
    private readonly UpdateCheckerFeature _feature;
    private readonly ObservableCollection<SelectableChapter> _rows = [];
    private CancellationTokenSource? _check;
    private bool _detached;

    public UpdateCheckerDialog(UpdateCheckerFeature feature, string localFolderName)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolderName);
        _feature = feature;

        InitializeComponent();

        TitleText.Text = localFolderName;
        ChapterTable.ItemsSource = _rows;

        // The header checkbox is declared in XAML without a name or a handler,
        // because a named or event-wired element inside another UserControl's
        // property cannot compile in this namescope. It is found and wired here.
        if (HeaderCheckBox is { } header) header.Click += HeaderCheck_Click;

        _feature.Changed += Feature_Changed;
        Closed += Dialog_Closed;
    }

    /// <summary>
    /// Starts the one manual check. Nothing was requested from a provider before
    /// this call, and nothing is requested after it except an explicit confirm.
    /// </summary>
    public void BeginCheck(string localFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolderName);
        Render();
        var cancellation = new CancellationTokenSource();
        _check = cancellation;
        _ = _feature.CheckAsync(localFolderName, cancellation.Token);
    }

    private CheckBox? HeaderCheckBox =>
        ChapterTable.InteractiveColumns
            .OfType<DataGridTemplateColumn>()
            .FirstOrDefault()?.Header as CheckBox;

    private void Feature_Changed(object? sender, EventArgs e)
    {
        if (_detached) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Render);
            return;
        }

        Render();
    }

    private void Render()
    {
        if (_detached) return;

        var state = _feature.State;
        StatusText.Text = state switch
        {
            UpdateCheckState.Loading => "Memeriksa chapter yang belum ada di folder lokal…",
            UpdateCheckState.Error => "Error: " + (_feature.Message ?? "Pemeriksaan gagal."),
            _ => _feature.Message ?? string.Empty,
        };
        StatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            state == UpdateCheckState.Error ? "Dim" : "Body");

        var choosing = state == UpdateCheckState.NeedsChoice;
        CandidateList.ItemsSource = choosing ? _feature.Candidates : null;
        CandidateList.Visibility = choosing ? Visibility.Visible : Visibility.Collapsed;

        var missing = _feature.MissingChapters;
        RebuildRows(missing);

        var ready = state == UpdateCheckState.Ready;
        ChapterTable.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionUi();
    }

    private void RebuildRows(IReadOnlyList<MissingChapter> missing)
    {
        foreach (var row in _rows) row.SelectedChanged -= Row_SelectedChanged;
        _rows.Clear();

        foreach (var chapter in missing)
        {
            var row = new SelectableChapter(chapter);
            row.SelectedChanged += Row_SelectedChanged;
            _rows.Add(row);
        }
    }

    private void Row_SelectedChanged(object? sender, EventArgs e) => UpdateSelectionUi();

    /// <summary>
    /// The header checkbox drives every row currently in the table, which is the
    /// whole visible set: this table lists only missing chapters and applies no
    /// filter of its own. Sorting may reorder the rows but never changes which
    /// ones the header reaches.
    /// </summary>
    private void HeaderCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;

        var select = box.IsChecked == true;
        foreach (var row in _rows) row.IsSelected = select;
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        var selected = _rows.Count(row => row.IsSelected);
        if (HeaderCheckBox is { } box)
        {
            box.IsChecked = selected == 0
                ? false
                : selected == _rows.Count
                    ? true
                    : null;
        }

        DownloadButton.IsEnabled = _feature.State == UpdateCheckState.Ready && selected > 0;
        ResultText.Text = _rows.Count == 0
            ? string.Empty
            : $"{selected} dari {_rows.Count} chapter dipilih.";
    }

    private void Candidate_ConfirmClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UpdateMatchCandidate candidate) return;

        _check?.Cancel();
        var cancellation = new CancellationTokenSource();
        _check = cancellation;
        _ = _feature.ConfirmCandidateAsync(candidate, cancellation.Token);
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows
            .Where(row => row.IsSelected)
            .Select(row => row.Chapter.ChapterId)
            .ToList();

        var result = _feature.EnqueueSelected(selected);
        ResultText.Text = result.Succeeded
            ? result.Skipped > 0
                ? $"{result.Queued} chapter di-queue; {result.Skipped} sudah pernah dipublikasikan."
                : $"{result.Queued} chapter di-queue ke Download List."
            : "Error: " + result.Blocked;

        if (!result.Succeeded) return;

        // The queue now owns those chapters; the dialog keeps no selection state.
        foreach (var row in _rows) row.IsSelected = false;
        UpdateSelectionUi();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Dialog_Closed(object? sender, EventArgs e)
    {
        if (_detached) return;
        _detached = true;

        _feature.Changed -= Feature_Changed;

        var check = _check;
        _check = null;
        check?.Cancel();
        check?.Dispose();

        foreach (var row in _rows) row.SelectedChanged -= Row_SelectedChanged;
        if (HeaderCheckBox is { } header) header.Click -= HeaderCheck_Click;
        _rows.Clear();
        CandidateList.ItemsSource = null;
        ChapterTable.ItemsSource = null;
    }

    /// <summary>
    /// One selectable missing chapter. Session-only: it is rebuilt on every
    /// render and never persisted.
    /// </summary>
    private sealed class SelectableChapter : INotifyPropertyChanged
    {
        private bool _isSelected;

        public SelectableChapter(MissingChapter chapter) => Chapter = chapter;

        public MissingChapter Chapter { get; }

        public string DisplayName => Chapter.DisplayName;

        public string ChapterNumber => Chapter.ChapterNumber;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
                SelectedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? SelectedChanged;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// The Update Checker's entry point into Library's local title detail. It owns
/// the `Check Updates` action and the popup; the caller only supplies the
/// reserved slot and a pull of the currently active local title, so no update
/// rule reaches the Library screen.
/// </summary>
public sealed class UpdateCheckerEntry
{
    private readonly UpdateCheckerFeature _feature;

    public UpdateCheckerEntry(UpdateCheckerFeature feature) =>
        _feature = feature ?? throw new ArgumentNullException(nameof(feature));

    public void Install(Panel slot, Func<string?> activeTitleFolderName)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(activeTitleFolderName);

        var button = new SettingButton
        {
            Content = "Check Updates",
            MinWidth = 124,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, "Check this title for missing chapters");
        button.Click += (_, _) => Open(activeTitleFolderName(), Window.GetWindow(slot));
        slot.Children.Add(button);
    }

    private void Open(string? localFolderName, Window? owner)
    {
        if (string.IsNullOrWhiteSpace(localFolderName)) return;

        var dialog = new UpdateCheckerDialog(_feature, localFolderName);
        if (owner is not null) dialog.Owner = owner;
        dialog.BeginCheck(localFolderName);
        dialog.ShowDialog();
    }
}

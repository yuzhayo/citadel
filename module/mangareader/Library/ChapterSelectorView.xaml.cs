using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Module.Mangareader.Library;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// Library's local title detail plus its chapter list. The title header is the
/// shared manga-detail composition fed by a Library-owned adapter, so this view
/// holds local data only and never a Catalog or provider model.
/// </summary>
public partial class ChapterSelectorView : UserControl
{
    /// <summary>
    /// Reserved action regions, built here rather than named in XAML: the shared
    /// detail composition is a UserControl with its own namescope, so a named
    /// child placed into one of its properties cannot compile.
    /// </summary>
    private readonly StackPanel _coverActions = new();
    private readonly StackPanel _detailActions = new() { Orientation = Orientation.Horizontal };

    private MangaTitleCardModel? _title;
    private LocalTitleDetailAdapter? _detail;

    public ChapterSelectorView()
    {
        InitializeComponent();
        Detail.CoverActions = _coverActions;
        Detail.DetailActions = _detailActions;
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? ChapterSelected;

    public event EventHandler? Dismissed;

    /// <summary>
    /// Reserved region below the cover for Library's title-level cover actions.
    /// The feature that owns an action places it here; this view places nothing
    /// speculatively.
    /// </summary>
    internal Panel CoverActions => _coverActions;

    /// <summary>Reserved region in the detail area, above Grouping.</summary>
    internal Panel DetailActions => _detailActions;

    /// <summary>
    /// The folder name of the title currently open, or null when the detail is
    /// dismissed. Grouping pulls this instead of receiving the card model, so
    /// no card type crosses the feature boundary.
    /// </summary>
    internal string? ActiveTitleFolderName => _title?.Manga.Title;

    public void ShowTitle(MangaTitleCardModel title, ChapterInfo? selected = null)
    {
        ArgumentNullException.ThrowIfNull(title);

        _detail?.Dispose();
        _title = title;
        _detail = new LocalTitleDetailAdapter(title);

        DataContext = _detail.Presentation;
        ChapterList.ItemsSource = title.Manga.Chapters;
        ChapterList.SelectedItem = selected ?? title.Manga.Chapters.FirstOrDefault();
        OpenButton.IsEnabled = ChapterList.SelectedItem is ChapterInfo;

        Visibility = Visibility.Visible;
        Focus();
        ChapterList.Focus();
    }

    public void Dismiss()
    {
        _detail?.Dispose();
        _detail = null;
        _title = null;
        DataContext = null;
        ChapterList.ItemsSource = null;
        Visibility = Visibility.Collapsed;
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenButton.IsEnabled = ChapterList.SelectedItem is ChapterInfo;
        e.Handled = true;
    }

    private void ChapterList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        AcceptSelection();

    private void OpenButton_Click(object sender, RoutedEventArgs e) =>
        AcceptSelection();

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        CloseSelector();

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSelector();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            AcceptSelection();
            e.Handled = true;
        }
    }

    private void AcceptSelection()
    {
        if (_title is null || ChapterList.SelectedItem is not ChapterInfo chapter) return;
        ChapterSelected?.Invoke(
            this,
            new OpenChapterRequestedEventArgs(_title.Manga, chapter));
    }

    private void CloseSelector()
    {
        Dismiss();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}

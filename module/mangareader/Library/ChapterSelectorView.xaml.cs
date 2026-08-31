using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class ChapterSelectorView : UserControl
{
    private MangaTitleCardModel? _title;

    public ChapterSelectorView() => InitializeComponent();

    public event EventHandler<OpenChapterRequestedEventArgs>? ChapterSelected;

    public event EventHandler? Dismissed;

    public void ShowTitle(MangaTitleCardModel title, ChapterInfo? selected = null)
    {
        _title = title ?? throw new ArgumentNullException(nameof(title));
        DataContext = title;
        ChapterList.SelectedItem = selected ?? title.Manga.Chapters.FirstOrDefault();
        Visibility = Visibility.Visible;
        Focus();
        ChapterList.Focus();
    }

    public void Dismiss()
    {
        _title = null;
        DataContext = null;
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

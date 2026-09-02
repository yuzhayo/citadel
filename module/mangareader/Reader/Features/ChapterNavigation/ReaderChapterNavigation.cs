using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Setting.Components;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public sealed record ReaderChapterChoice(int Index, string Title);

/// <summary>Feature-owned chapter card backed only by the coordinator contract.</summary>
public sealed class ReaderChapterNavigation : IReaderFeature, IReaderDrawerContributionProvider
{
    private ReaderFeatureContext? _context;
    private ReaderDrawerCardContribution? _contribution;
    private ComboBox? _picker;
    private SettingButton? _previous;
    private SettingButton? _next;
    private IReadOnlyList<ReaderChapterChoice> _choices = [];
    private bool _suppressSelection;

    public string FeatureName => "ChapterNavigation";

    public IReadOnlyList<ReaderDrawerContribution> DrawerContributions =>
        _contribution is null ? [] : [_contribution];

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        _choices = context.Chapters.Chapters
            .Select((chapter, index) => new ReaderChapterChoice(index, chapter.Title))
            .ToArray();

        _picker = new ComboBox
        {
            MinWidth = 0,
            Padding = new Thickness(8, 6, 24, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(ReaderChapterChoice.Title),
            ItemsSource = _choices,
            SelectedItem = ChoiceFor(context.Chapters.ActiveChapterIndex),
        };
        _picker.SetResourceReference(FrameworkElement.StyleProperty, "SettingComboBoxStyle");
        AutomationProperties.SetName(_picker, "Chapter");
        AutomationProperties.SetAutomationId(_picker, "ReaderChapterPicker");

        _previous = new SettingButton
        {
            Content = "Previous",
            Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_previous, "Previous chapter");
        AutomationProperties.SetAutomationId(_previous, "ReaderPreviousChapter");

        _next = new SettingButton
        {
            Content = "Next",
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_next, "Next chapter");
        AutomationProperties.SetAutomationId(_next, "ReaderNextChapter");

        var actions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_previous, 0);
        Grid.SetColumn(_next, 1);
        actions.Children.Add(_previous);
        actions.Children.Add(_next);

        var content = new StackPanel();
        content.Children.Add(_picker);
        content.Children.Add(actions);
        _contribution = new ReaderDrawerCardContribution(
            "chapter-navigation",
            100,
            ReaderDrawerCards.Create(content, "ReaderChapterCard"));

        _picker.SelectionChanged += OnSelectionChanged;
        _previous.Click += OnPreviousClick;
        _next.Click += OnNextClick;
        UpdateControls();
        context.Chapters.ActiveChapterChanged += OnActiveChapterChanged;
    }

    private ReaderChapterChoice? ChoiceFor(int index) =>
        _choices.FirstOrDefault(choice => choice.Index == index);

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (_context?.Chapters.CanNavigatePrevious == true)
            RequestNavigation(_context.Chapters.ActiveChapterIndex - 1);
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_context?.Chapters.CanNavigateNext == true)
            RequestNavigation(_context.Chapters.ActiveChapterIndex + 1);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _context is null || _picker?.SelectedItem is not ReaderChapterChoice choice)
            return;
        if (choice.Index != _context.Chapters.ActiveChapterIndex)
            RequestNavigation(choice.Index);
    }

    private void RequestNavigation(int index) => _ = NavigateAndReconcileAsync(index);

    private async Task NavigateAndReconcileAsync(int index)
    {
        var context = _context;
        if (context is null) return;

        try
        {
            await context.Chapters.NavigateToChapterAsync(index);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            context.Notifications.ShowToast(
                $"Chapter could not be opened: {exception.GetBaseException().Message}",
                TimeSpan.FromSeconds(4));
        }
        finally
        {
            if (ReferenceEquals(_context, context)) UpdateControls();
        }
    }

    private void OnActiveChapterChanged(object? sender, OpenChapterRequestedEventArgs e) =>
        UpdateControls();

    private void UpdateControls()
    {
        if (_context is null || _picker is null || _previous is null || _next is null) return;
        _suppressSelection = true;
        try
        {
            _picker.SelectedItem = ChoiceFor(_context.Chapters.ActiveChapterIndex);
        }
        finally
        {
            _suppressSelection = false;
        }

        _previous.IsEnabled = _context.Chapters.CanNavigatePrevious;
        _next.IsEnabled = _context.Chapters.CanNavigateNext;
    }

    public void Dispose()
    {
        if (_context is not null)
            _context.Chapters.ActiveChapterChanged -= OnActiveChapterChanged;
        if (_picker is not null) _picker.SelectionChanged -= OnSelectionChanged;
        if (_previous is not null) _previous.Click -= OnPreviousClick;
        if (_next is not null) _next.Click -= OnNextClick;
        _context = null;
        _contribution = null;
        _picker = null;
        _previous = null;
        _next = null;
        _choices = [];
    }
}

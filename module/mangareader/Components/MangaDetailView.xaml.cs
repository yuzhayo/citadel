using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Components;

/// <summary>
/// The shared manga-detail composition. It renders caller-supplied typed display
/// data and hosts caller-supplied action content; it owns no domain rule, no
/// data access and no feature state.
///
/// Every text slot resolves an absent value to the shared fallback instead of
/// rendering nothing, so a missing field cannot collapse the layout around it.
/// </summary>
public partial class MangaDetailView : UserControl
{
    public static readonly DependencyProperty CoverProperty =
        DependencyProperty.Register(
            nameof(Cover),
            typeof(ImageSource),
            typeof(MangaDetailView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(MangaDetailView),
            new FrameworkPropertyMetadata(
                MangaDetailPresentation.MissingValue,
                null,
                CoerceText));

    public static readonly DependencyProperty SynopsisProperty =
        DependencyProperty.Register(
            nameof(Synopsis),
            typeof(string),
            typeof(MangaDetailView),
            new FrameworkPropertyMetadata(
                MangaDetailPresentation.MissingValue,
                null,
                CoerceText));

    public static readonly DependencyProperty MetadataProperty =
        DependencyProperty.Register(
            nameof(Metadata),
            typeof(IEnumerable),
            typeof(MangaDetailView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CoverActionsProperty =
        DependencyProperty.Register(
            nameof(CoverActions),
            typeof(object),
            typeof(MangaDetailView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DetailActionsProperty =
        DependencyProperty.Register(
            nameof(DetailActions),
            typeof(object),
            typeof(MangaDetailView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderLeadingContentProperty =
        DependencyProperty.Register(
            nameof(HeaderLeadingContent),
            typeof(object),
            typeof(MangaDetailView),
            new PropertyMetadata(null));

    public MangaDetailView() => InitializeComponent();

    /// <summary>The decoded cover, or null while it is still loading.</summary>
    public ImageSource? Cover
    {
        get => (ImageSource?)GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Synopsis
    {
        get => (string)GetValue(SynopsisProperty);
        set => SetValue(SynopsisProperty, value);
    }

    /// <summary>Labelled rows, each already carrying its own fallback value.</summary>
    public IEnumerable? Metadata
    {
        get => (IEnumerable?)GetValue(MetadataProperty);
        set => SetValue(MetadataProperty, value);
    }

    /// <summary>Action content rendered below the cover.</summary>
    public object? CoverActions
    {
        get => GetValue(CoverActionsProperty);
        set => SetValue(CoverActionsProperty, value);
    }

    /// <summary>Action content rendered in the detail area, before Grouping.</summary>
    public object? DetailActions
    {
        get => GetValue(DetailActionsProperty);
        set => SetValue(DetailActionsProperty, value);
    }

    /// <summary>Caller-owned navigation content aligned left of the title.</summary>
    public object? HeaderLeadingContent
    {
        get => GetValue(HeaderLeadingContentProperty);
        set => SetValue(HeaderLeadingContentProperty, value);
    }

    private static object CoerceText(DependencyObject sender, object value) =>
        value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : MangaDetailPresentation.MissingValue;
}

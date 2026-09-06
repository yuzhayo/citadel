using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// One labelled detail value. An absent value keeps its row and renders the
/// shared fallback, so a missing field never collapses the rows around it.
/// </summary>
public sealed record MangaDetailMetadataRow(string Label, string? Value)
{
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public string DisplayValue => HasValue ? Value! : MangaDetailPresentation.MissingValue;
}

/// <summary>
/// The typed display data behind the shared manga-detail composition. Callers
/// build one from their own domain data — a local title or a remote provider
/// detail — and the composition renders it. It carries no folder scan, provider
/// response, queue record or store handle, and it performs no data access.
///
/// Only <see cref="Cover"/> changes after construction, because both callers
/// decode their cover asynchronously and must not rebuild the whole detail to
/// paint it. A cover that never arrives leaves the placeholder glyph in place
/// and cannot remove the text detail.
/// </summary>
public sealed class MangaDetailPresentation : INotifyPropertyChanged
{
    /// <summary>Rendered for every absent title-level value.</summary>
    public const string MissingValue = "—";

    private readonly string? _synopsis;
    private ImageSource? _cover;

    public MangaDetailPresentation(
        string? title,
        string? synopsis,
        IReadOnlyList<MangaDetailMetadataRow>? metadata)
    {
        Title = string.IsNullOrWhiteSpace(title) ? MissingValue : title;
        _synopsis = synopsis;
        Metadata = metadata ?? [];
    }

    public string Title { get; }

    public string SynopsisText => string.IsNullOrWhiteSpace(_synopsis) ? MissingValue : _synopsis!;

    public IReadOnlyList<MangaDetailMetadataRow> Metadata { get; }

    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value)) return;
            _cover = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

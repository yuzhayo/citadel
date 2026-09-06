using System.ComponentModel;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library;

/// <summary>
/// Maps the active local title onto the shared manga-detail presentation.
/// Library owns this mapping and its lifetime; the shared composition only ever
/// sees typed display data and never learns what a local title is.
///
/// The cover is the one value that can arrive after the detail is already on
/// screen, so this adapter forwards the card's later cover and releases that
/// subscription when the detail closes.
/// </summary>
public sealed class LocalTitleDetailAdapter : IDisposable
{
    private readonly MangaTitleCardModel _card;
    private bool _disposed;

    public LocalTitleDetailAdapter(MangaTitleCardModel card)
    {
        _card = card ?? throw new ArgumentNullException(nameof(card));
        Presentation = new MangaDetailPresentation(
            card.Title,
            synopsis: null,
            [
                new MangaDetailMetadataRow("Local folder", card.FolderPath),
                new MangaDetailMetadataRow("Chapters", card.ChapterSummary),
            ])
        {
            Cover = card.Cover,
        };

        _card.PropertyChanged += Card_PropertyChanged;
    }

    public MangaDetailPresentation Presentation { get; }

    private void Card_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(MangaTitleCardModel.Cover)) return;
        Presentation.Cover = _card.Cover;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _card.PropertyChanged -= Card_PropertyChanged;
    }
}

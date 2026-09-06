using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.Catalog;

/// <summary>
/// Maps one remote provider detail onto the shared manga-detail presentation.
/// Catalog owns this mapping and the shared composition only ever sees typed
/// display data, so the same layout serves Library's local detail without either
/// side learning the other's model.
///
/// The cover is deliberately not part of the mapping: it is fetched separately
/// and painted onto the presentation, so a cover failure can never remove the
/// title, the synopsis or the metadata rows.
/// </summary>
public sealed class RemoteTitleDetailAdapter
{
    public RemoteTitleDetailAdapter(RemoteTitleDetail detail)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Presentation = new MangaDetailPresentation(
            detail.Summary.DisplayName,
            detail.Description,
            Rows(detail));
    }

    public RemoteTitleDetail Detail { get; }

    public MangaDetailPresentation Presentation { get; }

    private static IReadOnlyList<MangaDetailMetadataRow> Rows(RemoteTitleDetail detail)
    {
        var rows = new List<MangaDetailMetadataRow>
        {
            new(
                "Genres",
                detail.Genres.Count == 0
                    ? null
                    : string.Join(", ", detail.Genres.Select(genre => genre.DisplayName))),
        };

        foreach (var metadata in detail.Metadata)
        {
            // The provider parser carries the human label in DisplayName and the
            // captured value in Key, which is the shape the detail has always
            // rendered. A value the provider did not return is absent, and the
            // shared composition renders its own fallback for it.
            rows.Add(new MangaDetailMetadataRow(metadata.DisplayName, metadata.Key));
        }

        return rows;
    }
}

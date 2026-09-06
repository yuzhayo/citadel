using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.FilterSearch;

/// <summary>
/// Owns the provider-specific filter contribution and creates the immutable
/// keyword/filter snapshot consumed by Catalog. It never fetches results and
/// never interprets provider wire keys.
/// </summary>
public sealed class FilterSearchFeature
{
    private IRemoteFilterContribution? _contribution;

    public IRemoteFilterContribution? Contribution => _contribution;

    public void SelectSource(MangaSourceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _contribution = registration.CreateFilters();
    }

    public void Clear() => _contribution = null;

    public string? ValidationMessage =>
        _contribution?.State.HasBlockingError == true
            ? _contribution.State.ValidationMessage
            : null;

    public RemoteBrowseRequest Snapshot(string? keyword, int page = 1)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var normalized = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        return new RemoteBrowseRequest(
            normalized,
            page,
            _contribution?.State.Snapshot(normalized));
    }
}

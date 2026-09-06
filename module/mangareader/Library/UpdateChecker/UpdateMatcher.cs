using System.IO;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library.UpdateChecker;

/// <summary>
/// The neutral view of one confirmed folder mapping held by the Downloader's
/// source index. Update Checker reads mappings through this shape, supplied by
/// the composition root, so a Library feature never imports an index, a queue or
/// a screen from another feature.
/// </summary>
public sealed record ConfirmedTitleMapping(
    string ProviderId,
    string RemoteTitleId,
    string RemoteTitleHid,
    string LocalFolderName);

public enum UpdateMatchKind
{
    /// <summary>No local chapters, no folder, or nothing comparable was found.</summary>
    NotFound,

    /// <summary>Exactly one safe title/group match; it may be bound.</summary>
    Unique,

    /// <summary>More than one plausible match; the user must confirm one.</summary>
    Ambiguous,
}

/// <summary>
/// One possible title/group binding with the evidence behind it. Chapter overlap
/// is display and ranking evidence only — identity is always the provider's
/// title key plus its group.
/// </summary>
public sealed record UpdateMatchCandidate(
    string ProviderId,
    RemoteTitleIdentity Title,
    string TitleDisplayName,
    RemoteGroupIdentity Group,
    string GroupDisplayName,
    int OverlappingChapters,
    int LocalChapterCount,
    int RemoteChapterCount)
{
    public string Description =>
        $"{TitleDisplayName} · {GroupDisplayName} · {OverlappingChapters}/{LocalChapterCount} chapter cocok";
}

public sealed record UpdateMatchResult(
    UpdateMatchKind Kind,
    UpdateMatchCandidate? Match,
    IReadOnlyList<UpdateMatchCandidate> Candidates,
    string? Message);

/// <summary>
/// Resolves which remote title and which single source group a local folder
/// belongs to, in the locked order: the Citadel identity embedded in the actual
/// local archives, then an already confirmed folder mapping, then a provider
/// title search compared by chapter overlap.
///
/// UI-free and read-only. It reads local files through the filesystem probe, so
/// a stale Library snapshot can never produce a binding, and it never persists
/// anything itself — it reports a decision and the feature owns the write.
/// Ambiguity is always reported rather than resolved by guessing.
/// </summary>
public sealed class UpdateMatcher
{
    /// <summary>
    /// Bound on one search: a manual check must not turn into a provider crawl.
    /// </summary>
    private const int MaximumSearchTitles = 5;

    private readonly LocalTitleProbe _probe;
    private readonly IMangaSourceDirectory _sources;
    private readonly Func<string, ConfirmedTitleMapping?> _confirmedMappings;

    public UpdateMatcher(
        LocalTitleProbe probe,
        IMangaSourceDirectory sources,
        Func<string, ConfirmedTitleMapping?> confirmedMappings)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _confirmedMappings = confirmedMappings
            ?? throw new ArgumentNullException(nameof(confirmedMappings));
    }

    public async Task<UpdateMatchResult> MatchAsync(
        string libraryRoot,
        string localFolderName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolderName);

        var probed = await _probe.ProbeTitleAsync(
            new LocalTitleProbeRequest(libraryRoot, localFolderName),
            cancellationToken).ConfigureAwait(false);

        if (!probed.TitleExists)
        {
            return Fail("Folder title ini tidak ada di library, jadi tidak ada yang bisa dibandingkan.");
        }

        var localNumbers = LocalChapterNumbers(probed);
        if (localNumbers.Count == 0)
        {
            return Fail("Folder ini tidak punya chapter bernomor yang bisa dibandingkan.");
        }

        var embedded = await MatchEmbeddedAsync(probed, localFolderName, localNumbers, cancellationToken)
            .ConfigureAwait(false);
        if (embedded is not null) return embedded;

        var mapping = _confirmedMappings(localFolderName);
        if (mapping is not null)
        {
            var source = _sources.FindSource(mapping.ProviderId);
            if (source is null)
            {
                return Fail($"Provider '{mapping.ProviderId}' dari pemetaan yang tersimpan tidak terdaftar.");
            }

            var identity = new RemoteTitleIdentity(
                mapping.ProviderId,
                mapping.RemoteTitleId,
                mapping.RemoteTitleHid,
                Slug: string.Empty);
            var displayName = await ResolveDisplayNameAsync(
                source, identity, localFolderName, cancellationToken).ConfigureAwait(false);
            var candidates = await CollectAsync(
                source, identity, displayName, localNumbers, cancellationToken).ConfigureAwait(false);
            return candidates.Count == 0
                ? Fail($"Pemetaan yang tersimpan menunjuk ke '{mapping.RemoteTitleId}' tetapi provider tidak mengembalikan group apa pun.")
                : Decide(candidates, localNumbers.Count, "pemetaan yang sudah dikonfirmasi", []);
        }

        return await SearchAsync(localFolderName, localNumbers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Step 1: the Citadel identity written inside the local archives. A single
    /// embedded title with a single group is a complete, unambiguous match that
    /// needs no URL input.
    /// </summary>
    private async Task<UpdateMatchResult?> MatchEmbeddedAsync(
        LocalTitleProbeResult probed,
        string localFolderName,
        IReadOnlyList<string> localNumbers,
        CancellationToken cancellationToken)
    {
        var embeddedChapters = probed.Chapters.Where(chapter => chapter.HasEmbeddedSource).ToList();
        if (embeddedChapters.Count == 0) return null;

        var titles = embeddedChapters
            .Select(chapter => (ProviderId: chapter.ProviderId!, TitleId: chapter.ProviderTitleId!))
            .Distinct()
            .ToList();
        if (titles.Count > 1)
        {
            return new UpdateMatchResult(
                UpdateMatchKind.Ambiguous,
                null,
                [],
                "Arsip di folder ini membawa identitas sumber dari lebih dari satu title provider; pilih satu sebelum mengikat.");
        }

        var groups = embeddedChapters
            .GroupBy(chapter => chapter.ProviderGroupId!, StringComparer.Ordinal)
            .Select(group => (
                GroupId: group.Key,
                GroupName: group
                    .Select(chapter => chapter.ProviderGroupName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Group " + group.Key,
                Count: group.Count()))
            .ToList();

        // The archive records the provider's title id but not the hid its routes
        // need, so the identity is completed from the confirmed mapping when the
        // two agree, and otherwise resolved with one bounded provider search.
        var resolved = await ResolveIdentityAsync(
            titles[0].ProviderId, titles[0].TitleId, localFolderName, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return Fail(
                $"Identitas sumber tertanam ditemukan ({titles[0].ProviderId}/{titles[0].TitleId}) "
                + "tetapi title remote-nya tidak dapat diselesaikan dari provider.");
        }

        var candidates = groups
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.GroupName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UpdateMatchCandidate(
                titles[0].ProviderId,
                resolved.Value.Identity,
                resolved.Value.DisplayName,
                new RemoteGroupIdentity(titles[0].ProviderId, group.GroupId),
                group.GroupName,
                group.Count,
                localNumbers.Count,
                group.Count))
            .ToList();

        return groups.Count == 1
            ? new UpdateMatchResult(UpdateMatchKind.Unique, candidates[0], candidates, null)
            : new UpdateMatchResult(
                UpdateMatchKind.Ambiguous,
                null,
                candidates,
                "Folder ini berisi chapter dari lebih dari satu group sumber; pilih satu group untuk diikat.");
    }

    private async Task<(RemoteTitleIdentity Identity, string DisplayName)?> ResolveIdentityAsync(
        string providerId,
        string titleId,
        string localFolderName,
        CancellationToken cancellationToken)
    {
        var mapping = _confirmedMappings(localFolderName);
        if (mapping is not null
            && string.Equals(mapping.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(mapping.RemoteTitleId, titleId, StringComparison.Ordinal))
        {
            var mappedIdentity = new RemoteTitleIdentity(
                providerId,
                titleId,
                mapping.RemoteTitleHid,
                Slug: string.Empty);
            var mappedSource = _sources.FindSource(providerId);
            var mappedName = mappedSource is null
                ? localFolderName
                : await ResolveDisplayNameAsync(
                    mappedSource, mappedIdentity, localFolderName, cancellationToken).ConfigureAwait(false);
            return (mappedIdentity, mappedName);
        }

        var source = _sources.FindSource(providerId);
        if (source is null) return null;

        var results = await SearchTitlesAsync(source, localFolderName, cancellationToken).ConfigureAwait(false);
        var match = results.FirstOrDefault(item =>
            string.Equals(item.Identity.TitleId, titleId, StringComparison.Ordinal));
        return match is null ? null : (match.Identity, match.DisplayName);
    }

    /// <summary>
    /// The provider's own title name for an identity that came from a stored
    /// mapping instead of a search result. One detail call resolves it; a failure
    /// falls back to the folder name rather than failing the match, because a name
    /// is provenance and never identity.
    /// </summary>
    private static async Task<string> ResolveDisplayNameAsync(
        IMangaSource source,
        RemoteTitleIdentity identity,
        string fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await source.GetTitleAsync(identity, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(detail.Summary.DisplayName)
                ? fallback
                : detail.Summary.DisplayName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>Step 3: one bounded provider search, compared by chapter overlap.</summary>
    private async Task<UpdateMatchResult> SearchAsync(
        string localFolderName,
        IReadOnlyList<string> localNumbers,
        CancellationToken cancellationToken)
    {
        var candidates = new List<UpdateMatchCandidate>();
        var failures = new List<string>();

        foreach (var source in _sources.AvailableSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<RemoteTitleSummary> results;
            try
            {
                results = await SearchTitlesAsync(source, localFolderName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add($"{source.DisplayName}: {exception.GetBaseException().Message}");
                continue;
            }

            foreach (var item in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    candidates.AddRange(await CollectAsync(
                        source, item.Identity, item.DisplayName, localNumbers, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // One unreadable title must not hide the other candidates.
                    failures.Add($"{item.DisplayName}: {exception.GetBaseException().Message}");
                }
            }
        }

        if (candidates.Count == 0)
        {
            return Fail(failures.Count == 0
                ? "Pencarian provider tidak menemukan title yang bisa dibandingkan."
                : "Pencarian provider gagal: " + string.Join(" ", failures));
        }

        return Decide(candidates, localNumbers.Count, "pencarian provider", failures);
    }

    private async Task<IReadOnlyList<RemoteTitleSummary>> SearchTitlesAsync(
        IMangaSource source,
        string localFolderName,
        CancellationToken cancellationToken)
    {
        var page = await source.BrowseAsync(
            new RemoteBrowseRequest(CleanTitle(localFolderName), Page: 1, Filter: null),
            cancellationToken).ConfigureAwait(false);
        return page.Items.Take(MaximumSearchTitles).ToList();
    }

    /// <summary>One candidate per group of one title, scored by chapter overlap.</summary>
    private static async Task<IReadOnlyList<UpdateMatchCandidate>> CollectAsync(
        IMangaSource source,
        RemoteTitleIdentity identity,
        string displayName,
        IReadOnlyList<string> localNumbers,
        CancellationToken cancellationToken)
    {
        var groups = await source.GetGroupsAsync(identity, cancellationToken).ConfigureAwait(false);
        var candidates = new List<UpdateMatchCandidate>(groups.Count);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapters = await source
                .GetChaptersAsync(identity, group.Identity, cancellationToken)
                .ConfigureAwait(false);

            var remoteNumbers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chapter in chapters)
            {
                if (ChapterNumberText.Normalize(chapter.Identity.ChapterNumber) is { } number)
                {
                    remoteNumbers.Add(number);
                }
            }

            candidates.Add(new UpdateMatchCandidate(
                source.Id,
                identity,
                displayName,
                group.Identity,
                group.DisplayName,
                localNumbers.Count(number => remoteNumbers.Contains(number)),
                localNumbers.Count,
                chapters.Count));
        }

        return candidates;
    }

    /// <summary>
    /// One safe winner is bound; anything else stays a choice. A tie at the top
    /// score, or no overlap at all, is ambiguity rather than a silent default.
    /// </summary>
    private static UpdateMatchResult Decide(
        IReadOnlyList<UpdateMatchCandidate> candidates,
        int localChapterCount,
        string origin,
        IReadOnlyList<string> failures)
    {
        var ordered = candidates
            .OrderByDescending(candidate => candidate.OverlappingChapters)
            .ThenBy(candidate => candidate.TitleDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.GroupDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var best = ordered[0].OverlappingChapters;
        var leaders = ordered.Where(candidate => candidate.OverlappingChapters == best).ToList();
        var note = failures.Count == 0
            ? string.Empty
            : " Sebagian sumber gagal: " + string.Join(" ", failures);

        if (best > 0 && leaders.Count == 1)
        {
            return new UpdateMatchResult(UpdateMatchKind.Unique, leaders[0], ordered, null);
        }

        return new UpdateMatchResult(
            UpdateMatchKind.Ambiguous,
            null,
            ordered,
            best == 0
                ? $"Tidak ada group dari {origin} yang chapter-nya cocok dengan {localChapterCount} chapter lokal; pilih manual bila tetap ingin mengikat.{note}"
                : $"Lebih dari satu kandidat dari {origin} punya kecocokan tertinggi ({best}/{localChapterCount}); konfirmasi satu sebelum mengikat.{note}");
    }

    private static UpdateMatchResult Fail(string message) =>
        new(UpdateMatchKind.NotFound, null, [], message);

    /// <summary>
    /// Local chapter numbers for overlap scoring. The embedded provider number
    /// wins; otherwise the last numeric run of the file name is used. This is
    /// display and ranking evidence only — it never becomes an identity.
    /// </summary>
    private static IReadOnlyList<string> LocalChapterNumbers(LocalTitleProbeResult probed)
    {
        var numbers = new List<string>();
        foreach (var chapter in probed.Chapters)
        {
            var normalized = ChapterNumberText.Normalize(chapter.ProviderChapterNumber)
                ?? ChapterNumberText.Normalize(Path.GetFileNameWithoutExtension(chapter.FileName));
            if (normalized is not null && !numbers.Contains(normalized, StringComparer.Ordinal))
            {
                numbers.Add(normalized);
            }
        }

        return numbers;
    }

    private static string CleanTitle(string folderName)
    {
        var cleaned = folderName.Replace('_', ' ');
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

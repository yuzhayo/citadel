using System.IO;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Library.UpdateChecker;

public enum UpdateCheckState
{
    /// <summary>Nothing has been asked for yet. No request has been made.</summary>
    Idle,

    Loading,

    /// <summary>Missing chapters are available for selection.</summary>
    Ready,

    NoUpdates,

    /// <summary>
    /// Matching was ambiguous, so the popup offers candidates for one-time
    /// confirmation. Nothing is bound and nothing is fetched until the user
    /// chooses.
    /// </summary>
    NeedsChoice,

    Error,
}

/// <summary>One remote chapter that is not present in local storage.</summary>
public sealed record MissingChapter(string ChapterId, string DisplayName, string ChapterNumber);

/// <summary>
/// The immutable handoff to the download queue: a bound title, its one preferred
/// group, and exactly the chapters the user selected. Update Checker never calls
/// a queue itself — the composition root supplies the route, so a Library feature
/// holds no queue type and no queue holds update-checking rules.
/// </summary>
public sealed record UpdateDownloadRequest(
    string ProviderId,
    RemoteTitleIdentity Title,
    string TitleDisplayName,
    RemoteGroupIdentity Group,
    string GroupDisplayName,
    string LocalFolderName,
    IReadOnlyList<RemoteChapterSummary> Chapters);

public sealed record UpdateHandoffResult(int Queued, int Skipped, string? Blocked)
{
    public bool Succeeded => Blocked is null;
}

public delegate UpdateHandoffResult EnqueueUpdateChapters(UpdateDownloadRequest request);

/// <summary>
/// Owns the manual update check for one local title: which binding applies, what
/// the provider currently has, which of those chapters are actually missing on
/// disk, and the one command that hands a selection to the queue.
///
/// Checking is manual. Nothing here runs at startup, polls, schedules, downloads
/// directly, refreshes the Library, or chains off a completed chapter.
/// </summary>
public sealed class UpdateCheckerFeature
{
    private readonly SourceBindingStore _bindings;
    private readonly UpdateMatcher _matcher;
    private readonly LocalTitleProbe _probe;
    private readonly IMangaSourceDirectory _sources;
    private readonly Func<string?> _libraryRoot;
    private readonly EnqueueUpdateChapters? _enqueue;
    private readonly object _gate = new();

    private UpdateCheckState _state = UpdateCheckState.Idle;
    private string? _message;
    private string? _localFolderName;
    private string? _titleDisplayName;
    private SourceBinding? _binding;
    private IReadOnlyList<UpdateMatchCandidate> _candidates = [];
    private IReadOnlyList<MissingChapter> _missing = [];
    private IReadOnlyList<RemoteChapterSummary> _remoteChapters = [];
    private RemoteGroupIdentity? _remoteGroup;
    private string _remoteGroupName = string.Empty;

    public UpdateCheckerFeature(
        SourceBindingStore bindings,
        UpdateMatcher matcher,
        LocalTitleProbe probe,
        IMangaSourceDirectory sources,
        Func<string?> libraryRoot,
        EnqueueUpdateChapters? enqueue)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _libraryRoot = libraryRoot ?? throw new ArgumentNullException(nameof(libraryRoot));
        _enqueue = enqueue;
    }

    public event EventHandler? Changed;

    public UpdateCheckState State
    {
        get { lock (_gate) return _state; }
    }

    public string? Message
    {
        get { lock (_gate) return _message; }
    }

    public SourceBinding? Binding
    {
        get { lock (_gate) return _binding; }
    }

    public IReadOnlyList<UpdateMatchCandidate> Candidates
    {
        get { lock (_gate) return _candidates; }
    }

    public IReadOnlyList<MissingChapter> MissingChapters
    {
        get { lock (_gate) return _missing; }
    }

    /// <summary>
    /// Runs one check for one local title. This is the only place a remote
    /// request is made, and it is reached only from an explicit user action.
    /// </summary>
    public async Task CheckAsync(string localFolderName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolderName);

        var root = _libraryRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            SetError("Library root belum diatur; pilih folder di tab Library lebih dulu.");
            return;
        }

        Set(UpdateCheckState.Loading, null, folderName: localFolderName);

        try
        {
            var binding = _bindings.Find(localFolderName);
            if (binding is null)
            {
                var match = await _matcher.MatchAsync(root, localFolderName, cancellationToken)
                    .ConfigureAwait(false);

                switch (match.Kind)
                {
                    case UpdateMatchKind.NotFound:
                        SetError(match.Message ?? "Title ini tidak dapat dicocokkan ke sumber mana pun.");
                        return;
                    case UpdateMatchKind.Ambiguous:
                        Set(UpdateCheckState.NeedsChoice, match.Message, candidates: match.Candidates);
                        return;
                    default:
                        binding = Persist(match.Match!, localFolderName, out var warning);
                        if (warning is not null)
                        {
                            Set(UpdateCheckState.Error, warning);
                            return;
                        }

                        break;
                }
            }

            if (binding is null)
            {
                SetError("Binding sumber tidak tersedia untuk title ini.");
                return;
            }

            await CompareAsync(binding, root, localFolderName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Set(UpdateCheckState.Idle, null);
        }
        catch (Exception exception)
        {
            // One title/provider failure stays inside this feature.
            SetError(exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Binds one user-chosen candidate and immediately continues into the same
    /// comparison, so confirming is not a second round trip for the user.
    /// </summary>
    public async Task ConfirmCandidateAsync(
        UpdateMatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var folderName = _localFolderName;
        var root = _libraryRoot();
        if (folderName is null || string.IsNullOrWhiteSpace(root))
        {
            SetError("Tidak ada title aktif untuk dikonfirmasi.");
            return;
        }

        Set(UpdateCheckState.Loading, null);
        try
        {
            var binding = Persist(candidate, folderName, out var warning);
            if (binding is null)
            {
                SetError(warning ?? "Binding tidak dapat disimpan.");
                return;
            }

            await CompareAsync(binding, root, folderName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Set(UpdateCheckState.Idle, null);
        }
        catch (Exception exception)
        {
            SetError(exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Hands exactly the selected chapters to the queue route as one immutable
    /// request and reports what the queue answered. It never downloads, never
    /// refreshes the Library and never touches another feature's completion path.
    /// </summary>
    public UpdateHandoffResult EnqueueSelected(IReadOnlyList<string> chapterIds)
    {
        ArgumentNullException.ThrowIfNull(chapterIds);

        if (_enqueue is null)
        {
            return new UpdateHandoffResult(0, 0, "Update Checker tidak punya rute ke Download Queue.");
        }

        SourceBinding? binding;
        RemoteGroupIdentity? group;
        string groupName;
        string? folderName;
        string? displayName;
        IReadOnlyList<RemoteChapterSummary> remote;
        lock (_gate)
        {
            binding = _binding;
            group = _remoteGroup;
            groupName = _remoteGroupName;
            folderName = _localFolderName;
            displayName = _titleDisplayName;
            remote = _remoteChapters;
        }

        if (binding is null || group is null || folderName is null)
        {
            return new UpdateHandoffResult(0, 0, "Tidak ada binding sumber untuk title ini.");
        }

        var selected = remote
            .Where(chapter => chapterIds.Contains(chapter.Identity.ChapterId, StringComparer.Ordinal))
            .ToList();
        if (selected.Count == 0)
        {
            return new UpdateHandoffResult(0, 0, "Tidak ada chapter yang dipilih.");
        }

        var identity = new RemoteTitleIdentity(
            binding.ProviderId,
            binding.RemoteTitleId,
            binding.RemoteTitleHid,
            binding.Slug);

        return _enqueue(new UpdateDownloadRequest(
            binding.ProviderId,
            identity,
            displayName ?? folderName,
            group,
            groupName,
            folderName,
            selected));
    }

    /// <summary>
    /// Fetches the bound preferred group only and compares it against the files
    /// that are actually on disk, so a chapter that exists locally is never
    /// offered again.
    /// </summary>
    private async Task CompareAsync(
        SourceBinding binding,
        string libraryRoot,
        string localFolderName,
        CancellationToken cancellationToken)
    {
        var source = _sources.FindSource(binding.ProviderId);
        if (source is null)
        {
            SetError($"Provider '{binding.ProviderId}' dari binding yang tersimpan tidak terdaftar.");
            return;
        }

        var identity = new RemoteTitleIdentity(
            binding.ProviderId,
            binding.RemoteTitleId,
            binding.RemoteTitleHid,
            binding.Slug);
        var group = new RemoteGroupIdentity(binding.ProviderId, binding.PreferredGroupId);

        var chapters = await source.GetChaptersAsync(identity, group, cancellationToken)
            .ConfigureAwait(false);
        var probed = await _probe.ProbeTitleAsync(
            new LocalTitleProbeRequest(libraryRoot, localFolderName, binding.ProviderId, binding.RemoteTitleId),
            cancellationToken).ConfigureAwait(false);

        var missing = new List<MissingChapter>();
        foreach (var chapter in chapters.OrderBy(chapter => chapter.OrderIndex))
        {
            if (IsPresentLocally(chapter, binding, probed)) continue;
            missing.Add(new MissingChapter(
                chapter.Identity.ChapterId,
                chapter.DisplayName,
                chapter.Identity.ChapterNumber));
        }

        lock (_gate)
        {
            _binding = binding;

            // Restored from the binding whether it was just persisted or reloaded
            // after a restart, so the queue names a job the way the provider does
            // instead of falling back to the local folder. Assigned unconditionally
            // so a previous title's name can never survive into this one.
            _titleDisplayName = string.IsNullOrWhiteSpace(binding.RemoteTitleName)
                ? null
                : binding.RemoteTitleName;

            _remoteChapters = chapters;
            _remoteGroup = group;
            _remoteGroupName = binding.PreferredGroupName;
            _candidates = [];
            _missing = missing;
            _state = missing.Count == 0 ? UpdateCheckState.NoUpdates : UpdateCheckState.Ready;
            _message = missing.Count == 0
                ? $"Tidak ada chapter baru di group '{binding.PreferredGroupName}'."
                : $"{missing.Count} chapter belum ada di folder lokal.";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A remote chapter is present when a local archive carries the same embedded
    /// provider/chapter/group identity. A local archive with embedded provenance
    /// for a different group is a distinct variant and never counts. Only a local
    /// archive with no embedded provenance falls back to its chapter number, so
    /// an older library does not get offered chapters it already has.
    /// </summary>
    private static bool IsPresentLocally(
        RemoteChapterSummary chapter,
        SourceBinding binding,
        LocalTitleProbeResult probed)
    {
        var number = ChapterNumberText.Normalize(chapter.Identity.ChapterNumber);
        foreach (var local in probed.Chapters)
        {
            if (local.IsSameChapter(binding.ProviderId, chapter.Identity.ChapterId, binding.PreferredGroupId))
            {
                return true;
            }

            if (local.HasEmbeddedSource) continue;

            if (number is not null && string.Equals(
                    ChapterNumberText.Normalize(
                        Path.GetFileNameWithoutExtension(local.FileName)),
                    number,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes one binding. A persistence failure is fatal to the check rather
    /// than silent: an unrecorded binding would re-ask the same question forever.
    /// </summary>
    private SourceBinding? Persist(
        UpdateMatchCandidate candidate,
        string localFolderName,
        out string? warning)
    {
        var binding = new SourceBinding(
            candidate.ProviderId,
            candidate.Title.TitleId,
            candidate.Title.TitleHid,
            candidate.TitleDisplayName,
            candidate.Title.Slug,
            CanonicalUrl(candidate.Title.Slug),
            localFolderName,
            candidate.Group.GroupId,
            candidate.GroupDisplayName,
            DateTimeOffset.UtcNow);

        var save = _bindings.Save(binding);
        warning = save.Saved
            ? null
            : "Binding tidak dapat disimpan, jadi pemeriksaan dibatalkan: " + save.Warning;
        lock (_gate)
        {
            _binding = save.Saved ? binding : null;
        }

        return save.Saved ? binding : null;
    }

    /// <summary>
    /// Provenance only. This feature stores the absolute url a provider adapter
    /// hands over and nothing else: turning a relative page path into a route is
    /// provider knowledge, so the adapter normalizes its own captured value and
    /// anything that still arrives unusable stays empty here rather than being
    /// guessed at.
    /// </summary>
    private static string CanonicalUrl(string slug) =>
        Uri.IsWellFormedUriString(slug, UriKind.Absolute)
        && slug.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? slug
            : string.Empty;

    private void Set(
        UpdateCheckState state,
        string? message,
        string? folderName = null,
        IReadOnlyList<UpdateMatchCandidate>? candidates = null)
    {
        lock (_gate)
        {
            _state = state;
            _message = message;
            if (folderName is not null) _localFolderName = folderName;
            if (candidates is not null) _candidates = candidates;
            if (state is not UpdateCheckState.NeedsChoice)
            {
                _candidates = [];
                _missing = [];
                _remoteChapters = [];
                _remoteGroup = null;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(string message) => Set(UpdateCheckState.Error, message);
}

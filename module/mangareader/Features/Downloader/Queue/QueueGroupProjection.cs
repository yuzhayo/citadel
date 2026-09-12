using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>Resident presentation state, never the lifetime owner of a job.</summary>
internal sealed class QueueGroupProjection
{
    private static readonly ConditionalWeakTable<DownloadQueueFeature, QueueGroupProjection> Owners = new();
    public static QueueGroupProjection For(DownloadQueueFeature queue) => Owners.GetValue(queue, _ => new());
    private readonly Dictionary<string, QueueDisplayRow> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<DownloadJobRecord> _jobs = [];
    public ObservableCollection<QueueDisplayRow> Visible { get; } = [];
    public event Action? SelectionChanged;
    public IReadOnlyList<string> SelectedIds => _selected.ToArray();

    public void Update(IReadOnlyList<DownloadJobRecord> jobs)
    {
        _jobs = jobs;
        _selected.IntersectWith(jobs.Select(job => job.JobId));
        var desired = new List<QueueDisplayRow>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in jobs.GroupBy(DownloadQueueFeature.TitleGroupKey))
        {
            var children = group.ToArray();
            var header = Row("group:" + group.Key, true);
            header.Update(children[0], group.Key, children, !_collapsed.Contains(group.Key), Selection(children));
            desired.Add(header);
            foreach (var job in children)
            {
                var row = Row(job.JobId, false);
                row.Update(job, group.Key, [job], false, _selected.Contains(job.JobId));
                if (header.Expanded) desired.Add(row);
            }
        }
        foreach (var stale in _rows.Keys.Where(key => !live.Contains(key)).ToArray()) _rows.Remove(stale);
        // Incremental structural reconciliation; progress changes only properties.
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Visible.Count && ReferenceEquals(Visible[i], desired[i])) continue;
            var previous = Visible.IndexOf(desired[i]);
            if (previous >= 0) Visible.Move(previous, i);
            else Visible.Insert(i, desired[i]);
        }
        while (Visible.Count > desired.Count) Visible.RemoveAt(Visible.Count - 1);

        QueueDisplayRow Row(string id, bool isGroup)
        {
            live.Add(id);
            if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new QueueDisplayRow(id, isGroup, Select);
            return row;
        }
    }

    public void Toggle(QueueDisplayRow row)
    {
        if (!row.IsGroup) return;
        if (!_collapsed.Add(row.GroupKey)) _collapsed.Remove(row.GroupKey);
        Update(_jobs);
    }

    public void Select(QueueDisplayRow row, bool selected)
    {
        foreach (var job in _jobs.Where(job => row.IsGroup
                     ? DownloadQueueFeature.TitleGroupKey(job) == row.GroupKey : job.JobId == row.Id))
        {
            if (selected) _selected.Add(job.JobId); else _selected.Remove(job.JobId);
        }
        Update(_jobs);
        SelectionChanged?.Invoke();
    }

    private bool? Selection(IReadOnlyList<DownloadJobRecord> jobs)
    {
        var count = jobs.Count(job => _selected.Contains(job.JobId));
        return count == 0 ? false : count == jobs.Count ? true : null;
    }
}

internal sealed class QueueDisplayRow(string id, bool isGroup, Action<QueueDisplayRow, bool> select) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public bool IsGroup { get; } = isGroup;
    public bool IsChapter => !IsGroup;
    public DownloadJobRecord Job { get; private set; } = new();
    public string GroupKey { get; private set; } = "";
    public bool Expanded { get; private set; }
    private bool? _selected;
    public bool? Selected
    {
        get => _selected;
        set { if (_selected != value) select(this, value == true); }
    }
    private IReadOnlyList<DownloadJobRecord> _children = [];
    public string Chevron => Expanded ? "▾" : "▸";
    public string TitleDisplayName => IsGroup ? Job.TitleDisplayName + " / " + Job.Identity.SourceId : "";
    public string ChapterDisplayName => IsGroup ? $"{_children.Count} chapters" : Job.ChapterDisplayName;
    public string GroupDisplayName => IsGroup ? "" : Job.GroupDisplayName;
    public string StateText => IsGroup
        ? $"{_children.Count(job => job.IsInFlight)} active · {_children.Count(job => job.State == DownloadJobState.Paused)} stopped"
        : Job.StateText;
    public string ProgressText => IsGroup
        ? $"{_children.Sum(job => job.CompletedPages)}/{_children.Sum(job => job.PageCount)}" : Job.ProgressText;
    public string? Warning => IsGroup ? null : string.Join("\n", new[] { Job.RouteText, Job.Warning }.Where(value => !string.IsNullOrEmpty(value)));
    public bool CanPause => IsGroup ? _children.Any(job => job.CanPause) : Job.CanPause;
    public bool CanResume => IsGroup ? _children.Any(job => job.CanResume) : Job.CanResume || Job.State == DownloadJobState.Queued;
    public string ResumeActionLabel => IsGroup ? "Resume" : Job.State == DownloadJobState.Failed ? "Retry" : "Start";
    public bool CanActNow => IsGroup || Job.CanActNow;
    public bool CanChooseFallback => !IsGroup && Job.CanChooseFallback;
    public bool CanOpenFolder => !IsGroup && Job.CanOpenFolder;

    public void Update(DownloadJobRecord job, string groupKey, IReadOnlyList<DownloadJobRecord> children, bool expanded, bool? selected)
    {
        if (Job == job && Expanded == expanded && Selected == selected && _children.SequenceEqual(children)) return;
        Job = job; GroupKey = groupKey; _children = children; Expanded = expanded; _selected = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

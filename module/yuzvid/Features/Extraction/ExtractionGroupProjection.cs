using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Module.Yuzvid.Features.Extraction;

/// <summary>
/// Flat row set for the extraction table: one tri-state group-header row per
/// igay69 version (V1..V4) plus its child link rows, shown only when expanded.
///
/// Mirrors MangaReader's <c>QueueGroupProjection</c> pattern (a single
/// ObservableCollection whose header rows carry IsGroup + Chevron + nullable
/// Selected) without importing it — features stay independent. Owned by
/// <see cref="ExtractionWindow"/>.
///
/// Child ordering is a fixed natural sort on the display title ("Part 2" before
/// "Part 10"), independent of arrival order from the tap/DOM merge. User-click
/// column sorting stays disabled: the view mixes group headers with child rows,
/// so a whole-view sort would interleave and break the grouping.
/// </summary>
public sealed class ExtractionGroupProjection
{
    private readonly Dictionary<string, ExtractionRow> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<VideoLink> _links = [];

    public ObservableCollection<ExtractionRow> Visible { get; } = new();

    public event Action? SelectionChanged;

    public IReadOnlyList<VideoLink> SelectedLinks
        => _links.Where(l => _selected.Contains(l.DedupKey)).ToList();

    private static string VersionKey(VideoLink link)
        => string.IsNullOrEmpty(link.VersionGroup) ? "\u2014" : link.VersionGroup;

    /// <summary>
    /// Natural title order: case-insensitive, digit runs compared numerically
    /// ("part 2" &lt; "part 10"). Deterministic tie-break by dedup key so equal
    /// titles never reorder between refreshes.
    /// </summary>
    private static readonly Comparer<VideoLink> TitleOrder = Comparer<VideoLink>.Create(static (a, b) =>
    {
        var cmp = NaturalCompare(a.DisplayTitle, b.DisplayTitle);
        return cmp != 0 ? cmp : string.CompareOrdinal(a.DedupKey, b.DedupKey);
    });

    private static int NaturalCompare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startI = i;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                var startJ = j;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                // Absurdly long digit runs compare as text (overflow guard).
                if (i - startI > 15 || j - startJ > 15)
                {
                    var runCmp = string.CompareOrdinal(x[startI..i], y[startJ..j]);
                    if (runCmp != 0) return runCmp;
                    continue;
                }

                var numeric = long.Parse(x[startI..i]).CompareTo(long.Parse(y[startJ..j]));
                if (numeric != 0) return numeric;
                continue;
            }

            var cmp = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (cmp != 0) return cmp;
            i++;
            j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }

    public void Update(IReadOnlyList<VideoLink> links)
    {
        _links = links ?? [];
        _selected.IntersectWith(_links.Select(l => l.DedupKey));

        var desired = new List<ExtractionRow>();
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in _links
                     .GroupBy(VersionKey, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var children = group.OrderBy(l => l, TitleOrder).ToArray();
            var header = Row("group:" + group.Key, true);
            header.UpdateHeader(group.Key, children, !_collapsed.Contains(group.Key), Selection(children));
            desired.Add(header);

            if (header.Expanded)
            {
                foreach (var link in children)
                {
                    var row = Row("link:" + link.DedupKey, false);
                    row.UpdateLink(link, _selected.Contains(link.DedupKey));
                    desired.Add(row);
                }
            }
        }

        foreach (var stale in _rows.Keys.Where(key => !live.Contains(key)).ToArray())
            _rows.Remove(stale);

        // Incremental structural reconciliation (same shape as QueueGroupProjection).
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Visible.Count && ReferenceEquals(Visible[i], desired[i])) continue;
            var previous = Visible.IndexOf(desired[i]);
            if (previous >= 0) Visible.Move(previous, i);
            else Visible.Insert(i, desired[i]);
        }
        while (Visible.Count > desired.Count) Visible.RemoveAt(Visible.Count - 1);

        ExtractionRow Row(string id, bool isGroup)
        {
            live.Add(id);
            if (!_rows.TryGetValue(id, out var row))
                _rows[id] = row = new ExtractionRow(id, isGroup, Select);
            return row;
        }
    }

    /// <summary>Collapse/expand one group header.</summary>
    public void Toggle(ExtractionRow row)
    {
        if (!row.IsGroup) return;
        if (!_collapsed.Add(row.GroupKey)) _collapsed.Remove(row.GroupKey);
        Update(_links);
    }

    /// <summary>Apply a header (all children) or row-level selection change.</summary>
    public void Select(ExtractionRow row, bool selected)
    {
        if (row.IsGroup)
        {
            foreach (var link in _links.Where(l => VersionKey(l) == row.GroupKey))
            {
                if (selected) _selected.Add(link.DedupKey);
                else _selected.Remove(link.DedupKey);
            }
        }
        else if (row.Link is { } link)
        {
            if (selected) _selected.Add(link.DedupKey);
            else _selected.Remove(link.DedupKey);
        }

        Update(_links);
        SelectionChanged?.Invoke();
    }

    public void SelectAll(bool selected)
    {
        if (selected) foreach (var link in _links) _selected.Add(link.DedupKey);
        else _selected.Clear();
        Update(_links);
        SelectionChanged?.Invoke();
    }

    private bool? Selection(IReadOnlyList<VideoLink> links)
    {
        var count = links.Count(l => _selected.Contains(l.DedupKey));
        return count == 0 ? false : count == links.Count ? true : null;
    }
}

/// <summary>
/// One row in the extraction table — a group header or a child link. Mirrors
/// MangaReader's <c>QueueDisplayRow</c>: one type, an <see cref="IsGroup"/> flag,
/// a nullable <see cref="Selected"/> for tri-state headers, and a chevron.
/// </summary>
public sealed class ExtractionRow : INotifyPropertyChanged
{
    private readonly Action<ExtractionRow, bool> _select;

    public ExtractionRow(string id, bool isGroup, Action<ExtractionRow, bool> select)
    {
        Id = id;
        IsGroup = isGroup;
        _select = select;
    }

    public string Id { get; }
    public bool IsGroup { get; }
    public bool IsLink => !IsGroup;

    public string GroupKey { get; private set; } = "";
    public VideoLink? Link { get; private set; }

    public bool Expanded { get; private set; }
    public string Chevron => Expanded ? "\u25BE" : "\u25B8";

    private bool? _selected;
    public bool? Selected
    {
        get => _selected;
        set { if (_selected != value) _select(this, value == true); }
    }

    public string Title { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Server { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public string CountText { get; private set; } = "";

    public void UpdateHeader(string key, IReadOnlyList<VideoLink> children, bool expanded, bool? selected)
    {
        GroupKey = key;
        Link = null;
        Expanded = expanded;
        _selected = selected;
        Title = VideoLinkExtractor.VersionLabel(key);
        Version = key == "\u2014" ? string.Empty : key;
        Server = string.Empty;
        Detail = string.Empty;
        CountText = children.Count == 1 ? "1 link" : $"{children.Count} links";
        RaiseAll();
    }

    public void UpdateLink(VideoLink link, bool selected)
    {
        GroupKey = "";
        Link = link;
        Expanded = false;
        _selected = selected;
        Title = link.DisplayTitle;
        Version = link.VersionGroup;
        Server = link.ServerGroup;
        Detail = link.DetailLine;
        CountText = string.Empty;
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Title), nameof(Version), nameof(Server), nameof(Detail),
                     nameof(CountText), nameof(Chevron), nameof(Selected),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
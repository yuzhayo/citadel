using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>A sortable, screen-blind table.</summary>
public sealed partial class SettingTable : Control
{
    public static readonly DependencyProperty SortColumnProperty =
        DependencyProperty.Register(
            nameof(SortColumn),
            typeof(string),
            typeof(SettingTable),
            new FrameworkPropertyMetadata(null, OnSortChanged));

    public static readonly DependencyProperty SortDescendingProperty =
        DependencyProperty.Register(
            nameof(SortDescending),
            typeof(bool),
            typeof(SettingTable),
            new FrameworkPropertyMetadata(false, OnSortChanged));

    private readonly ObservableCollection<string> _columns = [];
    private readonly ObservableCollection<IReadOnlyList<string>> _rows = [];
    private readonly List<IReadOnlyList<string>> _source = [];

    public SettingTable()
    {
        Columns = new ReadOnlyObservableCollection<string>(_columns);
        Rows = new ReadOnlyObservableCollection<IReadOnlyList<string>>(_rows);
        InitializeComponent();
    }

    public ReadOnlyObservableCollection<string> Columns { get; }

    public ReadOnlyObservableCollection<IReadOnlyList<string>> Rows { get; }

    public string? SortColumn
    {
        get => (string?)GetValue(SortColumnProperty);
        set => SetValue(SortColumnProperty, value);
    }

    public bool SortDescending
    {
        get => (bool)GetValue(SortDescendingProperty);
        set => SetValue(SortDescendingProperty, value);
    }

    public void SetColumns(IEnumerable<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns.Clear();
        foreach (var column in columns) _columns.Add(column);
        Resort();
    }

    public void SetRows(IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _source.Clear();
        _source.AddRange(rows);
        Resort();
    }

    private static void OnSortChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SettingTable)sender).Resort();

    private void Resort()
    {
        var index = SortColumn is null ? -1 : _columns.IndexOf(SortColumn);
        IEnumerable<IReadOnlyList<string>> ordered = _source;

        if (index >= 0)
        {
            ordered = SortDescending
                ? _source.OrderByDescending(row => Cell(row, index), StringComparer.OrdinalIgnoreCase)
                : _source.OrderBy(row => Cell(row, index), StringComparer.OrdinalIgnoreCase);
        }

        _rows.Clear();
        foreach (var row in ordered) _rows.Add(row);
    }

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index < row.Count ? row[index] : string.Empty;
}

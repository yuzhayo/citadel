using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Citadel.Setting.Components;

/// <summary>
/// A screen-blind table. The original string Columns/Rows API remains binary
/// compatible, while screens that need buttons or other cell content may
/// declare DataGrid columns through InteractiveColumns.
/// </summary>
public sealed partial class SettingTable : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(SettingTable));

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
        InteractiveColumns.CollectionChanged += InteractiveColumns_CollectionChanged;
        InitializeComponent();
        ApplyColumns();
    }

    /// <summary>The legacy text-column labels used by Settings and Gallery.</summary>
    public ReadOnlyObservableCollection<string> Columns { get; }

    public ReadOnlyObservableCollection<IReadOnlyList<string>> Rows { get; }

    /// <summary>Interactive column declarations used by feature screens.</summary>
    public ObservableCollection<DataGridColumn> InteractiveColumns { get; } = [];

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

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
        InteractiveColumns.Clear();
        _columns.Clear();
        foreach (var column in columns)
        {
            _columns.Add(column);
        }
        ApplyColumns();
        Resort();
    }

    public void SetRows(IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _source.Clear();
        _source.AddRange(rows);
        ItemsSource = Rows;
        Resort();
    }

    private static void OnSortChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SettingTable)sender).Resort();

    private void InteractiveColumns_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) => ApplyColumns();

    private void ApplyColumns()
    {
        if (InnerTable is null)
        {
            return;
        }

        InnerTable.Columns.Clear();
        if (InteractiveColumns.Count > 0)
        {
            foreach (var column in InteractiveColumns)
            {
                InnerTable.Columns.Add(column);
            }
            return;
        }

        foreach (var (label, index) in _columns.Select((label, index) => (label, index)))
        {
            InnerTable.Columns.Add(new DataGridTextColumn
            {
                Header = label,
                Binding = new Binding($"[{index}]") { Mode = BindingMode.OneWay },
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
        }
    }

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
        foreach (var row in ordered)
        {
            _rows.Add(row);
        }
    }

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index < row.Count ? row[index] : string.Empty;
}

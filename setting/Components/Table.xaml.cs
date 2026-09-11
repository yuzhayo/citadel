using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
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

    public static readonly DependencyProperty CanUserSortColumnsProperty =
        DependencyProperty.Register(
            nameof(CanUserSortColumns),
            typeof(bool),
            typeof(SettingTable),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty CanUserResizeColumnsProperty =
        DependencyProperty.Register(
            nameof(CanUserResizeColumns),
            typeof(bool),
            typeof(SettingTable),
            new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty ColumnWidthProperty =
        DependencyProperty.Register(
            nameof(ColumnWidth),
            typeof(DataGridLength),
            typeof(SettingTable),
            new FrameworkPropertyMetadata(DataGridLength.Auto));

    public static readonly DependencyProperty CellHorizontalContentAlignmentProperty =
        DependencyProperty.Register(
            nameof(CellHorizontalContentAlignment),
            typeof(HorizontalAlignment),
            typeof(SettingTable),
            new FrameworkPropertyMetadata(HorizontalAlignment.Stretch));

    public static readonly DependencyProperty HeaderHorizontalContentAlignmentProperty =
        DependencyProperty.Register(
            nameof(HeaderHorizontalContentAlignment),
            typeof(HorizontalAlignment),
            typeof(SettingTable),
            new FrameworkPropertyMetadata(HorizontalAlignment.Center));

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

    /// <summary>
    /// Enables native header sorting for opt-in interactive columns.
    /// Legacy text tables keep their existing SortColumn contract.
    /// </summary>
    public bool CanUserSortColumns
    {
        get => (bool)GetValue(CanUserSortColumnsProperty);
        set => SetValue(CanUserSortColumnsProperty, value);
    }

    /// <summary>Allows users to resize columns with the shared header grippers.</summary>
    public bool CanUserResizeColumns
    {
        get => (bool)GetValue(CanUserResizeColumnsProperty);
        set => SetValue(CanUserResizeColumnsProperty, value);
    }

    /// <summary>
    /// Sets the default width for columns that do not declare their own width.
    /// Supports native WPF Auto, SizeToCells, SizeToHeader, pixel, and star values.
    /// </summary>
    public DataGridLength ColumnWidth
    {
        get => (DataGridLength)GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    /// <summary>Sets the default horizontal alignment for table cells.</summary>
    public HorizontalAlignment CellHorizontalContentAlignment
    {
        get => (HorizontalAlignment)GetValue(CellHorizontalContentAlignmentProperty);
        set => SetValue(CellHorizontalContentAlignmentProperty, value);
    }

    /// <summary>Sets the horizontal alignment for column header content.</summary>
    public HorizontalAlignment HeaderHorizontalContentAlignment
    {
        get => (HorizontalAlignment)GetValue(HeaderHorizontalContentAlignmentProperty);
        set => SetValue(HeaderHorizontalContentAlignmentProperty, value);
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
        }
        else
        {
            foreach (var (label, index) in _columns.Select((label, index) => (label, index)))
            {
                InnerTable.Columns.Add(new DataGridTextColumn
                {
                    Header = label,
                    Binding = new Binding($"[{index}]") { Mode = BindingMode.OneWay },
                    IsReadOnly = true,
                });
            }
        }

        if (IsLoaded) UiPreference.Restore(this);
    }

    internal string CapturePreference()
    {
        var columns = InnerTable.Columns
            .Select((column, index) => new TableColumnPreference(
                ColumnKey(column, index),
                column.DisplayIndex,
                column.Width.Value,
                column.Width.UnitType,
                column.SortDirection))
            .ToArray();
        return JsonSerializer.Serialize(new TablePreference(
            SortColumn,
            SortDescending,
            columns));
    }

    internal void RestorePreference(string value)
    {
        TablePreference? preference;
        try
        {
            preference = JsonSerializer.Deserialize<TablePreference>(value);
        }
        catch (JsonException)
        {
            return;
        }

        if (preference?.Columns is null) return;
        var saved = new Dictionary<string, TableColumnPreference>(StringComparer.Ordinal);
        foreach (var column in preference.Columns)
        {
            if (!string.IsNullOrWhiteSpace(column.Key)) saved.TryAdd(column.Key, column);
        }

        var matches = InnerTable.Columns
            .Select((column, index) => (Column: column, Saved: saved.GetValueOrDefault(ColumnKey(column, index))))
            .Where(match => match.Saved is not null)
            .Select(match => (match.Column, Saved: match.Saved!))
            .ToArray();

        foreach (var match in matches)
        {
            var width = RestoredWidth(match.Saved);
            if (width is not null) match.Column.Width = width.Value;
            match.Column.SortDirection = match.Saved.SortDirection;
        }

        var preferredOrder = matches
            .OrderBy(match => match.Saved.DisplayIndex)
            .Select(match => match.Column)
            .Concat(InnerTable.Columns.Where(column => matches.All(match => match.Column != column)))
            .ToArray();
        for (var displayIndex = 0; displayIndex < preferredOrder.Length; displayIndex++)
        {
            preferredOrder[displayIndex].DisplayIndex = displayIndex;
        }

        if (preference.SortColumn is not null)
        {
            SortColumn = preference.SortColumn;
            SortDescending = preference.SortDescending;
        }

        var sorted = matches.FirstOrDefault(match => match.Saved.SortDirection is not null);
        if (sorted.Column is not null
            && InnerTable.CanUserSortColumns
            && ItemsSource is not null
            && !string.IsNullOrWhiteSpace(sorted.Column.SortMemberPath))
        {
            var view = CollectionViewSource.GetDefaultView(ItemsSource);
            if (view.CanSort)
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(
                    sorted.Column.SortMemberPath,
                    sorted.Saved.SortDirection!.Value));
            }
        }
    }

    private static string ColumnKey(DataGridColumn column, int index) =>
        $"{index}|{column.SortMemberPath}|{column.Header}";

    private static DataGridLength? RestoredWidth(TableColumnPreference preference)
    {
        if (!double.IsFinite(preference.Width) || preference.Width < 0) return null;
        return preference.WidthUnit switch
        {
            DataGridLengthUnitType.Auto => DataGridLength.Auto,
            DataGridLengthUnitType.SizeToCells => DataGridLength.SizeToCells,
            DataGridLengthUnitType.SizeToHeader => DataGridLength.SizeToHeader,
            DataGridLengthUnitType.Pixel => new DataGridLength(preference.Width),
            DataGridLengthUnitType.Star when preference.Width > 0 =>
                new DataGridLength(preference.Width, DataGridLengthUnitType.Star),
            _ => null,
        };
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

    private sealed record TablePreference(
        string? SortColumn,
        bool SortDescending,
        IReadOnlyList<TableColumnPreference> Columns);

    private sealed record TableColumnPreference(
        string Key,
        int DisplayIndex,
        double Width,
        DataGridLengthUnitType WidthUnit,
        ListSortDirection? SortDirection);
}

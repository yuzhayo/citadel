using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Components;

/// <summary>
/// The shared Grid/List selector. It shows which mode is current and reports
/// which mode the user asked for; deciding whether that request changes anything
/// belongs to the feature that owns the preference.
/// </summary>
public partial class MangaViewModeSelector : UserControl
{
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(MangaViewMode),
            typeof(MangaViewModeSelector),
            new FrameworkPropertyMetadata(MangaViewMode.Grid, OnModeChanged));

    public MangaViewModeSelector()
    {
        InitializeComponent();
        Render(MangaViewMode.Grid);
    }

    /// <summary>Raised when the user asks for a mode. Always the pressed mode.</summary>
    public event EventHandler<MangaViewMode>? ModeRequested;

    public MangaViewMode Mode
    {
        get => (MangaViewMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private static void OnModeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MangaViewModeSelector)sender).Render((MangaViewMode)args.NewValue);

    private void GridButton_Click(object sender, RoutedEventArgs e) =>
        ModeRequested?.Invoke(this, MangaViewMode.Grid);

    private void ListButton_Click(object sender, RoutedEventArgs e) =>
        ModeRequested?.Invoke(this, MangaViewMode.List);

    /// <summary>
    /// Marks the current mode with the shared selected surface and accent
    /// foreground. The other button keeps the shared default surface, so the
    /// active choice stays readable while either button is hovered.
    /// </summary>
    private void Render(MangaViewMode mode)
    {
        if (GridButton is null || ListButton is null) return;

        Apply(GridButton, mode == MangaViewMode.Grid);
        Apply(ListButton, mode == MangaViewMode.List);
    }

    private static void Apply(Control button, bool active)
    {
        button.SetResourceReference(
            Control.BackgroundProperty,
            active ? "Selected" : "Hover");
        button.SetResourceReference(
            Control.ForegroundProperty,
            active ? "Accent" : "Body");
    }
}

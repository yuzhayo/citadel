using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Rpl;
using Citadel.Setting.Components;

namespace Citadel.Setting.Screens;

/// <summary>
/// Shared shape for the four Settings screens: a scrolling column of sections.
///
/// No screen writes its own route title — the Content card owns route identity
/// and a screen starts with its first meaningful section. That is why there is
/// no title row here.
/// </summary>
public abstract class SettingScreen : UserControl
{
    private readonly StackPanel _body = new();

    protected SettingScreen(Lifetime lifetime)
    {
        Lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));

        NameScope.SetNameScope(this, new NameScope());
        Resources.MergedDictionaries.Add(new SettingResources());
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _body,
        };
        scroller.SetResourceReference(FrameworkElement.StyleProperty, "SettingScrollViewerStyle");
        Content = scroller;
        _body.Margin = new Thickness(20, 18, 20, 20);
    }

    /// <summary>The view's own lifetime, supplied by the Router.</summary>
    protected Lifetime Lifetime { get; }

    protected void Add(UIElement element) => _body.Children.Add(element);

    protected void Clear() => _body.Children.Clear();

    protected static TextBlock Section(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, "SettingSectionStyle");
        return block;
    }

    protected static TextBlock Body(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, "SettingBodyStyle");
        return block;
    }

    protected static Border Card(UIElement content, string? automationId = null)
    {
        var card = new Border { Child = content };
        card.SetResourceReference(StyleProperty, "SettingCardStyle");
        if (automationId is not null) AutomationProperties.SetAutomationId(card, automationId);
        return card;
    }

    /// <summary>Registers a declaration target in this screen's namescope.</summary>
    protected T LayoutSlot<T>(T element, string name) where T : FrameworkElement
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        element.Name = name;
        RegisterName(name, element);
        return element;
    }

    protected static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    protected static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    protected static SettingButton Action(string label, string automationId, Action onClick)
    {
        var button = new SettingButton
        {
            Content = label,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 132,
        };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => onClick();
        return button;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Module.Mangareader;

/// <summary>
/// An opaque, feature-owned Drawer card. The Drawer may order and host the
/// card, but it never interprets or rebuilds the feature's controls.
/// </summary>
public sealed class ReaderDrawerCardContribution : ReaderDrawerContribution
{
    public ReaderDrawerCardContribution(
        string key,
        int order,
        FrameworkElement card)
        : base(key, order)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
    }

    public FrameworkElement Card { get; }
}

internal static class ReaderDrawerCards
{
    public static Border Create(UIElement content, string automationId)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);

        var card = new Border { Child = content };
        card.SetResourceReference(FrameworkElement.StyleProperty, "SettingCardStyle");
        AutomationProperties.SetAutomationId(card, automationId);
        return card;
    }

    public static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text };
        label.SetResourceReference(FrameworkElement.StyleProperty, "SettingBodyStyle");
        return label;
    }
}

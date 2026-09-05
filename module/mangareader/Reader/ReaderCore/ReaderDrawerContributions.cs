using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Module.Mangareader.ReaderCore;

/// <summary>
/// An opaque, feature-owned Drawer card. The Drawer may order and host the
/// card, but it never interprets or rebuilds the feature's controls.
///
/// This is a shared Reader contract, not a Drawer internal: every
/// card-contributing feature builds one, while the Drawer itself only ever
/// handles the abstract <see cref="ReaderDrawerContribution"/>.
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

/// <summary>
/// Shared factory for the surface a feature-owned Drawer card is composed on.
/// It only reuses the existing Setting card and body styles; it adds no
/// rendering or input behavior of its own.
/// </summary>
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

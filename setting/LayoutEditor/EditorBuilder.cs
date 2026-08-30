using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Tokens;
using Citadel.Setting.Components;

namespace Citadel.Setting.LayoutEditor;

/// <summary>The observable result of one generated-editor action.</summary>
public sealed record LayoutEditResult(bool Applied, string? Warning);

/// <summary>
/// Generates the editor for one screen's layout: a control per declared slot, of
/// the kind the slot declares, writing sparse overrides keyed by route.
///
/// Generated, not hand-written, because a screen's slots are its own business —
/// and deliberately narrow. A generated editor can
/// outrun its schema and must not become an open-ended UI description language,
/// so three kinds is the whole vocabulary.
/// </summary>
public static class EditorBuilder
{
    /// <summary>
    /// Builds the editor. Each control commits through `CommitLayout`, so the
    /// sparse-override and validation rules are the store's, not the editor's.
    /// </summary>
    public static FrameworkElement Build(
        string route,
        LayoutDeclaration declaration,
        Tokens tokens,
        Action<LayoutEditResult>? onEdit = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(tokens);

        var slots = DeclarationReader.Read(route, declaration, tokens.LayoutOverrides(route));
        var panel = new StackPanel();
        AutomationProperties.SetAutomationId(panel, $"LayoutEditor:{route}");

        if (slots.Count == 0)
        {
            panel.Children.Add(Note("This screen declares no editable slots."));
            return panel;
        }

        foreach (var slot in slots)
        {
            panel.Children.Add(BuildSlot(route, slot, tokens, onEdit));
        }
        return panel;
    }

    private static FrameworkElement BuildSlot(
        string route,
        SlotModel slot,
        Tokens tokens,
        Action<LayoutEditResult>? onEdit)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetAutomationId(rows, $"Slot:{slot.Name}");

        var heading = new TextBlock
        {
            Text = $"{slot.Name} — {DeclarationReader.NameOf(slot.Kind)}",
            Margin = new Thickness(0, 0, 0, 4),
        };
        heading.SetResourceReference(Control.ForegroundProperty, "Dim");
        rows.Children.Add(heading);

        if (slot.Kind == SlotKind.Visibility)
        {
            var toggle = new SettingToggle
            {
                Content = "visible",
                IsChecked = slot.Visible ?? true,
            };
            AutomationProperties.SetAutomationId(toggle, $"{slot.Name}:visible");
            toggle.Checked += (_, _) => Report(
                onEdit,
                Commit(route, slot.Name, "visible", JsonValue.Create(true), tokens),
                null);
            toggle.Unchecked += (_, _) => Report(
                onEdit,
                Commit(route, slot.Name, "visible", JsonValue.Create(false), tokens),
                null);
            rows.Children.Add(toggle);
            return rows;
        }

        foreach (var property in slot.Properties)
        {
            rows.Children.Add(BuildNumber(route, slot, property, tokens, onEdit));
        }
        return rows;
    }

    private static FrameworkElement BuildNumber(
        string route,
        SlotModel slot,
        string property,
        Tokens tokens,
        Action<LayoutEditResult>? onEdit)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };

        var label = new TextBlock
        {
            Text = property,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(Control.ForegroundProperty, "Body");

        var field = new SettingField
        {
            Text = slot.Numbers[property].ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = 90,
        };
        AutomationProperties.SetAutomationId(field, $"{slot.Name}:{property}");

        var synchronizing = false;
        field.TextChanged += value =>
        {
            if (synchronizing) return;
            if (double.TryParse(
                    value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var number)
                && double.IsFinite(number))
            {
                var edit = DeclarationReader.NormalizeNumber(slot, property, number);
                if (edit.Warning is not null)
                {
                    synchronizing = true;
                    try
                    {
                        field.Text = edit.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                    finally
                    {
                        synchronizing = false;
                    }
                }

                Report(
                    onEdit,
                    Commit(route, slot.Name, property, JsonValue.Create(edit.Value), tokens),
                    edit.Warning);
            }
        };

        line.Children.Add(label);
        line.Children.Add(field);
        return line;
    }

    private static bool Commit(
        string route,
        string slot,
        string property,
        JsonNode? value,
        Tokens tokens) => tokens.CommitLayout(route, slot, property, value);

    private static void Report(
        Action<LayoutEditResult>? onEdit,
        bool applied,
        string? warning) => onEdit?.Invoke(new LayoutEditResult(applied, warning));

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        note.SetResourceReference(Control.ForegroundProperty, "Dim");
        return note;
    }
}

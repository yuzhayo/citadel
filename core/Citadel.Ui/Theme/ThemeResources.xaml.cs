using System.Windows;
using System.Windows.Media;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;

namespace Citadel.Ui.Theme;

/// <summary>
/// Live bridge from the token store into WPF resources. Replacing a dictionary
/// value invalidates every DynamicResource consumer, including metrics; no
/// template takes a one-time StaticResource snapshot.
/// </summary>
public partial class ThemeResources : ResourceDictionary
{
    private static readonly string[] NumberTokens =
    [
        "Rail", "Row", "IconSlot", "TitleX", "FullDefault", "FullMin", "FullMax",
    ];

    private static readonly string[] ColorTokens =
    [
        "BgRail", "Bg", "Fg", "Dim", "Accent", "Hover", "Selected", "Border", "Card", "Body",
    ];

    private Tokens? _boundTokens;

    public ThemeResources() => InitializeComponent();

    public void Bind(Tokens tokens, Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (_boundTokens is not null)
        {
            throw new InvalidOperationException("theme resources are already bound");
        }

        _boundTokens = tokens;
        Apply(tokens);

        void Changed() => Apply(tokens);
        tokens.TokensChanged += Changed;
        lifetime.Add(() =>
        {
            tokens.TokensChanged -= Changed;
            if (ReferenceEquals(_boundTokens, tokens)) _boundTokens = null;
        });
    }

    internal void Apply(Tokens tokens)
    {
        foreach (var token in NumberTokens)
        {
            this[token] = tokens.Number(token);
        }

        this["RailGridLength"] = new GridLength(tokens.Number("Rail"));
        this["RowGridLength"] = new GridLength(tokens.Number("Row"));
        this["TitleXGridLength"] = new GridLength(tokens.Number("TitleX"));

        foreach (var token in ColorTokens)
        {
            var value = tokens.Resolve(token);
            this[token] = new SolidColorBrush(Color.FromArgb(value.A, value.R, value.G, value.B));
        }
    }
}

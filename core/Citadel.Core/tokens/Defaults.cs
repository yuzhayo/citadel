namespace Citadel.Core.Tokens;

/// <summary>
/// Defaults live in code, never on disk, so a corrupt override file can
/// never take the fallback with it. Rail and FullMax come from v0's
/// MainWindow.xaml.cs:62-65. FullDefault/FullMin, Row, and TitleX are
/// intentional starting-grid choices.
/// </summary>
public static class Defaults
{
    public static readonly IReadOnlyDictionary<string, TokenValue> All =
        new Dictionary<string, TokenValue>
        {
            // metrics
            ["Rail"] = TokenValue.OfNumber(58),
            ["Row"] = TokenValue.OfNumber(40),
            ["IconSlot"] = TokenValue.OfNumber(20),
            ["TitleX"] = TokenValue.OfNumber(49),
            ["FullDefault"] = TokenValue.OfNumber(192),
            ["FullMin"] = TokenValue.OfNumber(50),
            ["FullMax"] = TokenValue.OfNumber(320),
            ["WindowW"] = TokenValue.OfNumber(1180),
            ["WindowH"] = TokenValue.OfNumber(900),
            ["WindowMinW"] = TokenValue.OfNumber(900),
            ["WindowMinH"] = TokenValue.OfNumber(560),

            // colors
            ["BgRail"] = Parse("#121211"),
            ["Bg"] = Parse("#171716"),
            ["Fg"] = Parse("#D6D6D3"),
            ["Dim"] = Parse("#8A8886"),
            ["Accent"] = Parse("#C9A96A"),
            ["Hover"] = Parse("#232320"),
            ["Selected"] = Parse("#2E2D29"),
            ["Border"] = Parse("#2A2926"),
            ["Card"] = Parse("#1E1D1B"),
            ["Body"] = Parse("#B5B3AE"),
        };

    public static bool TryGet(string token, out TokenValue value) => All.TryGetValue(token, out value);

    private static TokenValue Parse(string hex) =>
        TokenValue.TryParseColor(hex, out var v)
            ? v
            : throw new InvalidOperationException($"bad default color {hex}");
}

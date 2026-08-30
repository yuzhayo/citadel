using System.Text.Json.Nodes;
using Citadel.Core.Modules;

namespace Citadel.Setting.Screens;

/// <summary>
/// Settings is built-in rather than discovered, so its declaration ships with
/// the screen and the composition root supplies it directly to the Router.
/// </summary>
public static class SettingsLayout
{
    public static LayoutDeclaration Declaration() => new(
        JsonNode.Parse(
            """
            {
              "screens":  { "kind": "visibility", "visible": true },
              "problems": { "kind": "visibility", "visible": true },
              "actions":  { "kind": "position",   "x": 0, "y": 0 }
            }
            """)!.AsObject());
}

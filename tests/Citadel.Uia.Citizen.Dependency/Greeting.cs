namespace Citizen.PrivateDependency;

/// <summary>
/// The one call a citizen makes into its private dependency. Nothing about the
/// work matters — what matters is that this assembly exists only inside the
/// citizen's folder, so reaching it requires the resolver.
/// </summary>
public static class Greeting
{
    public const string Text = "private dependency loaded";

    public static string Say() => Text;
}

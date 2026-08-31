namespace Module.Camoprof.Network;

internal enum NetworkState
{
    Offline,
    Degraded,
    Recovering,
    Stable,
}

internal sealed record NetworkSample(
    bool GeneralReachable,
    bool GoogleReachable,
    DateTimeOffset CheckedAt);

internal sealed record NetworkSnapshot(
    NetworkState State,
    bool GeneralReachable,
    bool GoogleReachable,
    DateTimeOffset CheckedAt,
    string Reason)
{
    public static NetworkSnapshot Initial { get; } = new(
        NetworkState.Recovering,
        GeneralReachable: false,
        GoogleReachable: false,
        DateTimeOffset.MinValue,
        "menunggu sampel jaringan");
}

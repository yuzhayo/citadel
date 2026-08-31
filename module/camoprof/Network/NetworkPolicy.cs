namespace Module.Camoprof.Network;

internal static class NetworkPolicy
{
    public static NetworkSnapshot Classify(IReadOnlyList<NetworkSample> samples)
    {
        if (samples.Count == 0)
        {
            return NetworkSnapshot.Initial;
        }

        var latest = samples[^1];
        var previous = samples.Count > 1 ? samples[^2] : null;
        NetworkState state;
        string reason;

        if (!latest.GeneralReachable)
        {
            var hadRecentSuccess = samples
                .Take(samples.Count - 1)
                .Any(sample => sample.GeneralReachable);
            state = hadRecentSuccess ? NetworkState.Degraded : NetworkState.Offline;
            reason = hadRecentSuccess
                ? "koneksi umum terputus setelah sempat merespons"
                : "koneksi umum tidak merespons";
        }
        else if (previous?.GeneralReachable == true)
        {
            state = NetworkState.Stable;
            reason = latest.GoogleReachable
                ? "koneksi dan endpoint Google merespons"
                : "koneksi stabil, endpoint Google tidak merespons";
        }
        else
        {
            state = NetworkState.Recovering;
            reason = "koneksi kembali; menunggu satu sampel berhasil lagi";
        }

        return new NetworkSnapshot(
            state,
            latest.GeneralReachable,
            latest.GoogleReachable,
            latest.CheckedAt,
            reason);
    }
}

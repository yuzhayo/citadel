using System.Text.Json.Nodes;
using Module.Camoprof.Network;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Providers.Google;

internal sealed class GoogleAccountService
{
    private const string AccountUrl = "https://myaccount.google.com/";

    private readonly NetworkMonitor _network;
    private readonly GoogleCredentialStore _credentials;
    private readonly BrowserSessionCoordinator _sessions;

    public GoogleAccountService(
        NetworkMonitor network,
        GoogleCredentialStore credentials,
        BrowserSessionCoordinator sessions)
    {
        _network = network;
        _credentials = credentials;
        _sessions = sessions;
    }

    public async Task<GoogleAccountResult> CheckAsync(
        ProfileEntry profile,
        bool showBrowser,
        CancellationToken cancellationToken = default)
    {
        var networkResult = await RequireStableNetworkAsync(cancellationToken);
        if (networkResult is not null)
        {
            return networkResult;
        }

        var openedHere = false;
        try
        {
            if (!_sessions.IsOpen(profile.ProfileId))
            {
                await _sessions.OpenAsync(
                    profile.ProfileId,
                    AccountUrl,
                    headless: !showBrowser,
                    cancellationToken);
                openedHere = true;
            }

            var inspection = await _sessions.InspectGoogleAsync(
                profile.ProfileId,
                cancellationToken);
            var state = ReadState(inspection);
            var detectedEmail = ReadEmail(inspection);
            var account = _credentials.TryLoad(profile.ProfileId);

            if (state == "active")
            {
                var result = MatchActiveAccount(account, detectedEmail);
                if (openedHere)
                {
                    await _sessions.CloseAsync(profile.ProfileId, cancellationToken);
                }

                return result;
            }

            if (state != "signed_out")
            {
                if (openedHere)
                {
                    await _sessions.CloseAsync(profile.ProfileId, cancellationToken);
                }

                return Result(
                    GoogleAccountState.ActionRequired,
                    detectedEmail,
                    "Google merespons tetapi status akun tidak dapat dipastikan");
            }

            if (account is null || !_credentials.HasPassword(profile.ProfileId))
            {
                if (openedHere)
                {
                    await _sessions.CloseAsync(profile.ProfileId, cancellationToken);
                }

                return Result(
                    account is null
                        ? GoogleAccountState.Unlinked
                        : GoogleAccountState.SignedOut,
                    account?.Email,
                    account is null
                        ? "profile signed out dan belum dipasangkan ke email"
                        : "session habis; password relog belum tersimpan");
            }

            if (_sessions.IsHeadless(profile.ProfileId))
            {
                await _sessions.CloseAsync(profile.ProfileId, cancellationToken);
                await _sessions.OpenAsync(
                    profile.ProfileId,
                    AccountUrl,
                    headless: false,
                    cancellationToken);
                openedHere = true;
            }

            var password = _credentials.ReadPassword(profile.ProfileId);
            var relog = await _sessions.RelogGoogleAsync(
                profile.ProfileId,
                account.Email,
                password,
                cancellationToken);
            var relogState = ReadState(relog);
            var relogEmail = ReadEmail(relog);

            if (relogState == "active")
            {
                var result = MatchActiveAccount(account, relogEmail);
                if (openedHere)
                {
                    await _sessions.CloseAsync(profile.ProfileId, cancellationToken);
                }

                return result;
            }

            if (relogState == "credential_rejected")
            {
                return Result(
                    GoogleAccountState.CredentialRejected,
                    account.Email,
                    "password ditolak; tekan status Google untuk memperbaruinya");
            }

            return Result(
                GoogleAccountState.ActionRequired,
                account.Email,
                "selesaikan challenge Google di browser yang terbuka");
        }
        catch
        {
            if (openedHere && _sessions.IsOpen(profile.ProfileId))
            {
                try
                {
                    await _sessions.CloseAsync(profile.ProfileId, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Coordinator disposal remains the final cleanup backstop.
                }
            }

            throw;
        }
    }

    public async Task<GoogleAccountResult> DetectAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var networkResult = await RequireStableNetworkAsync(cancellationToken);
        if (networkResult is not null)
        {
            return networkResult;
        }

        if (!_sessions.IsOpen(profileId))
        {
            await _sessions.OpenAsync(
                profileId,
                AccountUrl,
                headless: false,
                cancellationToken);
        }
        else if (_sessions.IsHeadless(profileId))
        {
            await _sessions.CloseAsync(profileId, cancellationToken);
            await _sessions.OpenAsync(
                profileId,
                AccountUrl,
                headless: false,
                cancellationToken);
        }

        var inspection = await _sessions.InspectGoogleAsync(profileId, cancellationToken);
        var state = ReadState(inspection);
        var email = ReadEmail(inspection);
        return state switch
        {
            "active" when !string.IsNullOrWhiteSpace(email) => Result(
                GoogleAccountState.Active,
                email,
                "email Google berhasil dideteksi"),
            "active" => Result(
                GoogleAccountState.ActionRequired,
                null,
                "akun aktif, tetapi email belum dapat dideteksi"),
            "signed_out" => Result(
                GoogleAccountState.SignedOut,
                null,
                "login Google belum selesai di browser"),
            _ => Result(
                GoogleAccountState.ActionRequired,
                null,
                "status Google belum dapat dipastikan"),
        };
    }

    private async Task<GoogleAccountResult?> RequireStableNetworkAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await _network.RefreshForProviderCheckAsync(cancellationToken);
        if (snapshot.State == NetworkState.Offline)
        {
            return Result(GoogleAccountState.Offline, null, snapshot.Reason);
        }

        if (snapshot.State is NetworkState.Degraded or NetworkState.Recovering)
        {
            return Result(GoogleAccountState.Degraded, null, snapshot.Reason);
        }

        return snapshot.GoogleReachable
            ? null
            : Result(
                GoogleAccountState.ProviderUnavailable,
                null,
                "internet stabil, tetapi endpoint Google tidak merespons");
    }

    private static GoogleAccountResult MatchActiveAccount(
        GoogleAccountRecord? account,
        string? detectedEmail)
    {
        if (string.IsNullOrWhiteSpace(detectedEmail))
        {
            return Result(
                GoogleAccountState.ActionRequired,
                null,
                "session aktif, tetapi email tidak dapat dideteksi");
        }

        if (account is null)
        {
            return Result(
                GoogleAccountState.Unlinked,
                detectedEmail,
                "akun terdeteksi; tekan status Google untuk memasangkannya");
        }

        return string.Equals(
            account.Email,
            detectedEmail,
            StringComparison.OrdinalIgnoreCase)
            ? Result(
                GoogleAccountState.Active,
                detectedEmail,
                "resident profile aktif sebagai " + detectedEmail)
            : Result(
                GoogleAccountState.WrongAccount,
                detectedEmail,
                "email aktif berbeda dari " + account.Email);
    }

    private static string ReadState(JsonObject response)
        => response["state"]?.GetValue<string>() ?? "unknown";

    private static string? ReadEmail(JsonObject response)
        => response["email"]?.GetValue<string>();

    private static GoogleAccountResult Result(
        GoogleAccountState state,
        string? email,
        string reason)
        => new(state, email, reason, DateTimeOffset.Now);
}

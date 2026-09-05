using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Camoprof.Providers.Google;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Features.AddProfile;

/// <summary>
/// End-to-end Add Profile orchestration, owned by the feature:
/// ensure a headed resident session (the feature decides the start
/// URL — about:blank, since it claims and navigates the same page),
/// start enrollment (listener armed before navigation), poll status
/// at the contracted interval, and on Complete-with-password call
/// finish exactly once and DPAPI-save the credential BEFORE any
/// non-secret result leaves this class.
///
/// The has_password:false branch (passkey/QR) never calls finish —
/// there is no secret to retrieve — and goes straight to cancel.
///
/// Launcher never sees any of this: the only public entry is
/// <see cref="AddProfileFeature"/>.
/// </summary>
internal sealed class AddProfileCoordinator
{
    private readonly BrowserSessionCoordinator? _sessions;
    private readonly GoogleCredentialStore? _credentials;

    internal Func<string, CancellationToken, Task> EnsureHeadedSessionAsync { get; set; } = null!;
    internal Func<string, string?, CancellationToken, Task<JsonObject>> StartAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task<JsonObject>> StatusAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task<JsonObject>> FinishAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task> CancelAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task> CloseSessionAsync { get; set; } = null!;
    internal Func<string, string, string, CancellationToken, Task> SaveCredentialAsync { get; set; } = null!;

    public AddProfileCoordinator(
        BrowserSessionCoordinator sessions,
        GoogleCredentialStore credentials)
    {
        _sessions = sessions;
        _credentials = credentials;
        var client = new AddProfilePyHostClient(sessions);
        EnsureHeadedSessionAsync = EnsureHeadedSessionCoreAsync;
        StartAsync = client.StartAsync;
        StatusAsync = client.StatusAsync;
        FinishAsync = client.FinishAsync;
        CancelAsync = client.CancelAsync;
        CloseSessionAsync = (profile, token)
            => sessions.CloseAsync(profile, token);
        SaveCredentialAsync = (profile, email, password, token)
            => credentials.SaveAsync(profile, email, password, token);
    }

    /// <summary>Test-only constructor: every seam replaced by the test.</summary>
    internal AddProfileCoordinator()
    {
    }

    internal async Task<AddProfileResult> ExecuteAsync(
        AddProfileRequest request,
        IProgress<AddProfileUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profileId = request.ProfileId;
        try
        {
            return await RunCoreAsync(
                request, progress, cancellationToken);
        }
        finally
        {
            // Flow END: browser milik flow ditutup (Python teardown sudah
            // menjatuhkan session; ini membersihkan registry lokal agar
            // row/status UI benar). Idempotent dan tidak pernah gagalkan
            // hasil.
            await CancelQuietlyAsync(profileId);
            await CloseSessionQuietlyAsync(profileId);
        }
    }

    private async Task<AddProfileResult> RunCoreAsync(
        AddProfileRequest request,
        IProgress<AddProfileUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var profileId = request.ProfileId;
        try
        {
            await EnsureHeadedSessionAsync(profileId, cancellationToken);

            // start returns only after the listener is armed on the
            // enrollment page and its navigation has begun.
            await StartAsync(profileId, request.ExpectedEmail, cancellationToken);
            Report(progress, "armed");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await StatusAsync(profileId, cancellationToken);
                var wireState = status["state"]?.GetValue<string>() ?? "unknown";
                Report(progress, wireState);

                var outcome = AddProfilePolicy.OutcomeFor(wireState);
                if (outcome is not null)
                {
                    return Result(outcome.Value, ReadEmail(status));
                }

                if (wireState == "complete")
                {
                    return await CompleteAsync(
                        profileId, request.ExpectedEmail, status, cancellationToken);
                }

                await Task.Delay(AddProfilePolicy.PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return Result(AddProfileOutcome.Cancelled, null);
        }
        catch (PyHostException ex)
        {
            var outcome = ex.Code switch
            {
                "BROWSER_GONE" => AddProfileOutcome.BrowserGone,
                "WRONG_ACCOUNT" => AddProfileOutcome.WrongAccount,
                _ => AddProfileOutcome.Failed,
            };
            return new AddProfileResult(outcome, null, ex.Code + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            return new AddProfileResult(AddProfileOutcome.Failed, null, ex.Message);
        }
    }

    private async Task<AddProfileResult> CompleteAsync(
        string profileId,
        string? expectedEmail,
        JsonObject status,
        CancellationToken cancellationToken)
    {
        var hasPassword = status["has_password"]?.GetValue<bool>() ?? false;
        var email = ReadEmail(status);

        if (!hasPassword)
        {
            // Passkey/QR login: the session is genuinely active but there
            // is no secret to retrieve — finish is never called on this
            // branch. Honest outcome, nothing saved.
            return Result(AddProfileOutcome.ActiveWithoutPassword, email);
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return new AddProfileResult(
                AddProfileOutcome.Failed,
                null,
                "account active but its identity could not be confirmed");
        }

        JsonObject finish;
        try
        {
            // The single plaintext crossing; the response never leaves
            // this method.
            finish = await FinishAsync(profileId, cancellationToken);
        }
        catch (PyHostException ex)
        {
            var outcome = ex.Code == "WRONG_ACCOUNT"
                ? AddProfileOutcome.WrongAccount
                : AddProfileOutcome.Failed;
            return new AddProfileResult(outcome, email, ex.Code + ": " + ex.Message);
        }

        var finishEmail = ReadEmail(finish) ?? email;
        var password = finish["password"]?.GetValue<string>();

        // Boundary re-validation of the external response: expected-email
        // mismatch must never reach the store, whatever pyhost promised.
        if (!string.IsNullOrWhiteSpace(expectedEmail)
            && !string.Equals(finishEmail, expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Result(AddProfileOutcome.WrongAccount, finishEmail);
        }

        if (string.IsNullOrEmpty(password))
        {
            return Result(AddProfileOutcome.ActiveWithoutPassword, finishEmail);
        }

        try
        {
            await SaveCredentialAsync(profileId, finishEmail, password, cancellationToken);
        }
        catch (Exception ex)
        {
            // Storage failure is never reported as success.
            return new AddProfileResult(
                AddProfileOutcome.Failed,
                finishEmail,
                "credential could not be stored: " + ex.Message);
        }

        return Result(AddProfileOutcome.Completed, finishEmail);
    }

    private async Task CancelQuietlyAsync(string profileId)
    {
        try
        {
            await CancelAsync(profileId, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort — the pyhost teardown already dropped the
            // session; a second cancel is proven-absent.
        }
    }

    private async Task CloseSessionQuietlyAsync(string profileId)
    {
        try
        {
            await CloseSessionAsync(profileId, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort — the pyhost teardown already dropped the
            // session; this only fixes the local registry/row state.
        }
    }

    private async Task EnsureHeadedSessionCoreAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var sessions = _sessions!;
        if (!sessions.IsOpen(profileId))
        {
            // about:blank: the feature claims this very page and
            // navigates it after arming — the user never sees a
            // google.com flash before the login page.
            await sessions.OpenAsync(profileId, "about:blank", false, cancellationToken);
        }
        else if (sessions.IsHeadless(profileId))
        {
            await sessions.CloseAsync(profileId, cancellationToken);
            await sessions.OpenAsync(profileId, "about:blank", false, cancellationToken);
        }
    }

    private static void Report(
        IProgress<AddProfileUpdate>? progress,
        string wireState)
        => progress?.Report(
            new AddProfileUpdate(wireState, AddProfilePolicy.StatusText(wireState)));

    private static string? ReadEmail(JsonObject response)
        => response["email"]?.GetValue<string>();

    private static AddProfileResult Result(
        AddProfileOutcome outcome,
        string? email)
        => new(outcome, email, AddProfilePolicy.LauncherStatus(outcome, email));
}

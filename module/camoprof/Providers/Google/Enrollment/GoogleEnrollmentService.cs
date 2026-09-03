using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Providers.Google.Enrollment;

/// <summary>
/// Runs one enrollment end-to-end: ensures a headed resident session
/// (neutral start URL — the enrollment command owns Google navigation,
/// and only after its listener is armed), polls status, and on
/// Complete-with-password calls <c>finish</c> exactly once and stores the
/// credential via <see cref="GoogleCredentialStore"/> BEFORE any
/// non-secret result leaves this class.
///
/// The has_password:false branch (passkey/QR) never calls finish — there
/// is no secret to retrieve — and goes straight to cancel/teardown.
///
/// Test seams are delegate properties (no interface hierarchy): tests
/// replace them with small fakes; production wiring targets the
/// coordinator and credential store.
/// </summary>
internal sealed class GoogleEnrollmentService
{
    private readonly BrowserSessionCoordinator? _sessions;
    private readonly GoogleCredentialStore? _credentials;

    // Seams are non-nullable in practice: the production constructor
    // wires all of them, and the test constructor's callers replace all
    // of them before first use.
    internal Func<string, string?, CancellationToken, Task> EnsureHeadedSessionAsync { get; set; } = null!;
    internal Func<string, string?, CancellationToken, Task<JsonObject>> StartAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task<JsonObject>> StatusAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task<JsonObject>> FinishAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task> CancelAsync { get; set; } = null!;
    internal Func<string, string, string, CancellationToken, Task> SaveCredentialAsync { get; set; } = null!;

    /// <summary>
    /// Test-only constructor: no coordinator or store is wired — every
    /// seam is replaced by the test before use.
    /// </summary>
    internal GoogleEnrollmentService()
    {
    }

    public GoogleEnrollmentService(
        BrowserSessionCoordinator sessions,
        GoogleCredentialStore credentials)
    {
        _sessions = sessions;
        _credentials = credentials;
        EnsureHeadedSessionAsync = EnsureHeadedSessionCoreAsync;
        StartAsync = (profile, expectedEmail, token)
            => sessions.StartGoogleEnrollmentAsync(profile, expectedEmail, token);
        StatusAsync = (profile, token)
            => sessions.GoogleEnrollmentStatusAsync(profile, token);
        FinishAsync = (profile, token)
            => sessions.FinishGoogleEnrollmentAsync(profile, token);
        CancelAsync = async (profile, token) =>
            await sessions.CancelGoogleEnrollmentAsync(profile, token);
        SaveCredentialAsync = (profile, email, password, token)
            => credentials.SaveAsync(profile, email, password, token);
    }

    public async Task<GoogleEnrollmentResult> EnrollAsync(
        string profileId,
        string? expectedEmail,
        IProgress<GoogleEnrollmentUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureHeadedSessionAsync(profileId, expectedEmail, cancellationToken);

            // start returns only after the listener is armed and the
            // enrollment page has navigated to Google's sign-in.
            await StartAsync(profileId, expectedEmail, cancellationToken);
            Report(progress, "armed");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await StatusAsync(profileId, cancellationToken);
                var wireState = status["state"]?.GetValue<string>() ?? "unknown";
                Report(progress, wireState);

                var outcome = GoogleEnrollmentPolicy.OutcomeFor(wireState);
                if (outcome is not null)
                {
                    return Result(outcome.Value, ReadEmail(status));
                }

                if (wireState == "complete")
                {
                    return await CompleteAsync(
                        profileId, expectedEmail, status, cancellationToken);
                }

                await Task.Delay(
                    GoogleEnrollmentPolicy.PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await CancelQuietlyAsync(profileId);
            return Result(GoogleEnrollmentOutcome.Cancelled, null);
        }
        catch (PyHostException ex)
        {
            await CancelQuietlyAsync(profileId);
            var outcome = ex.Code switch
            {
                "BROWSER_GONE" => GoogleEnrollmentOutcome.BrowserGone,
                "WRONG_ACCOUNT" => GoogleEnrollmentOutcome.WrongAccount,
                _ => GoogleEnrollmentOutcome.Failed,
            };
            return new GoogleEnrollmentResult(outcome, null, ex.Code + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            await CancelQuietlyAsync(profileId);
            return new GoogleEnrollmentResult(
                GoogleEnrollmentOutcome.Failed, null, ex.Message);
        }
    }

    private async Task<GoogleEnrollmentResult> CompleteAsync(
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
            await CancelQuietlyAsync(profileId);
            return Result(GoogleEnrollmentOutcome.ActiveWithoutPassword, email);
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            await CancelQuietlyAsync(profileId);
            return new GoogleEnrollmentResult(
                GoogleEnrollmentOutcome.Failed,
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
            await CancelQuietlyAsync(profileId);
            var outcome = ex.Code == "WRONG_ACCOUNT"
                ? GoogleEnrollmentOutcome.WrongAccount
                : GoogleEnrollmentOutcome.Failed;
            return new GoogleEnrollmentResult(outcome, email, ex.Code + ": " + ex.Message);
        }

        var finishEmail = ReadEmail(finish) ?? email;
        var password = finish["password"]?.GetValue<string>();

        // Boundary re-validation of the external response: expected-email
        // mismatch must never reach the store, whatever pyhost promised.
        if (!string.IsNullOrWhiteSpace(expectedEmail)
            && !string.Equals(finishEmail, expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            await CancelQuietlyAsync(profileId);
            return Result(GoogleEnrollmentOutcome.WrongAccount, finishEmail);
        }

        if (string.IsNullOrEmpty(password))
        {
            await CancelQuietlyAsync(profileId);
            return Result(GoogleEnrollmentOutcome.ActiveWithoutPassword, finishEmail);
        }

        try
        {
            await SaveCredentialAsync(profileId, finishEmail, password, cancellationToken);
        }
        catch (Exception ex)
        {
            // Storage failure is never reported as success.
            return new GoogleEnrollmentResult(
                GoogleEnrollmentOutcome.Failed,
                finishEmail,
                "credential could not be stored: " + ex.Message);
        }

        return Result(GoogleEnrollmentOutcome.Completed, finishEmail);
    }

    private async Task CancelQuietlyAsync(string profileId)
    {
        try
        {
            await CancelAsync(profileId, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort — the pyhost session-death lifecycle hook is
            // the backstop for disarm.
        }
    }

    private async Task EnsureHeadedSessionCoreAsync(
        string profileId,
        string? _expectedEmail,
        CancellationToken cancellationToken)
    {
        var sessions = _sessions!;
        if (!sessions.IsOpen(profileId))
        {
            // The resident page is transient here: the enrollment command
            // closes it once its own page is armed, so exactly ONE browser
            // window stays visible. about:blank keeps that brief page
            // neutral instead of flashing google.com.
            await sessions.OpenAsync(profileId, "about:blank", false, cancellationToken);
        }
        else if (sessions.IsHeadless(profileId))
        {
            await sessions.CloseAsync(profileId, cancellationToken);
            await sessions.OpenAsync(profileId, "about:blank", false, cancellationToken);
        }
    }

    private static void Report(
        IProgress<GoogleEnrollmentUpdate>? progress,
        string wireState)
        => progress?.Report(
            new GoogleEnrollmentUpdate(wireState, GoogleEnrollmentPolicy.StatusText(wireState)));

    private static string? ReadEmail(JsonObject response)
        => response["email"]?.GetValue<string>();

    private static GoogleEnrollmentResult Result(
        GoogleEnrollmentOutcome outcome,
        string? email)
        => new(
            outcome,
            email,
            GoogleEnrollmentPolicy.LauncherStatus(outcome, email));
}

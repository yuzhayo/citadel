using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Camoprof.Providers.Google.Enrollment;
using Xunit;

namespace Module.Camoprof.Tests;

/// <summary>
/// Drives GoogleEnrollmentService through its delegate seams — no
/// browser, no pyhost process, no real DPAPI writes. Asserts the four
/// contracts that matter: credential saved exactly once on
/// Complete-with-password; has_password:false never calls finish;
/// storage failure is never success; cancellation and pyhost errors map
/// to non-secret outcomes.
/// </summary>
public class GoogleEnrollmentServiceTests
{
    private sealed class Harness
    {
        public List<string> Calls { get; } = [];
        public List<(string Profile, string Email, string Password)> Saved { get; } = [];
        public int FinishCalls;
        public int CancelCalls;
        public Queue<Func<JsonObject>> StatusScript { get; } = [];

        public GoogleEnrollmentService Build(
            JsonObject? finishResponse = null,
            Exception? saveFailure = null)
        {
            var service = new GoogleEnrollmentService();
            service.EnsureHeadedSessionAsync = (profile, _email, _token) =>
            {
                Calls.Add("ensure:" + profile);
                return Task.CompletedTask;
            };
            service.StartAsync = (profile, _email, _token) =>
            {
                Calls.Add("start:" + profile);
                return Task.FromResult(new JsonObject { ["state"] = "armed" });
            };
            service.StatusAsync = (profile, _token) =>
            {
                Calls.Add("status");
                return Task.FromResult(StatusScript.Dequeue()());
            };
            service.FinishAsync = (profile, _token) =>
            {
                Calls.Add("finish");
                FinishCalls++;
                return Task.FromResult(
                    finishResponse ?? new JsonObject());
            };
            service.CancelAsync = (profile, _token) =>
            {
                Calls.Add("cancel");
                CancelCalls++;
                return Task.CompletedTask;
            };
            service.SaveCredentialAsync = (profile, email, password, _token) =>
            {
                if (saveFailure is not null)
                {
                    throw saveFailure;
                }

                Saved.Add((profile, email, password));
                return Task.CompletedTask;
            };
            return service;
        }

        public static JsonObject Status(
            string state,
            string? email = null,
            bool hasPassword = false)
            => new()
            {
                ["state"] = state,
                ["email"] = email,
                ["has_password"] = hasPassword,
                ["challenge"] = state == "challenge",
                ["url"] = "https://example.invalid/",
            };
    }

    [Fact]
    public async Task Complete_with_password_saves_exactly_once()
    {
        var harness = new Harness();
        var service = harness.Build(
            finishResponse: new JsonObject
            {
                ["email"] = "user@gmail.com",
                ["password"] = "test-secret",
            });
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", "user@gmail.com", hasPassword: true));

        var result = await service.EnrollAsync("probe", null);

        Assert.Equal(GoogleEnrollmentOutcome.Completed, result.Outcome);
        Assert.Equal("user@gmail.com", result.Email);
        Assert.True(result.SavedCredential);
        Assert.Single(harness.Saved);
        Assert.Equal(("probe", "user@gmail.com", "test-secret"), harness.Saved[0]);
        Assert.Equal(1, harness.FinishCalls);
        // finish performs its own teardown — cancel is not part of the
        // success path.
        Assert.Equal(0, harness.CancelCalls);
    }

    [Fact]
    public async Task Complete_without_password_never_calls_finish()
    {
        var harness = new Harness();
        var service = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", "user@gmail.com", hasPassword: false));

        var result = await service.EnrollAsync("probe", null);

        Assert.Equal(GoogleEnrollmentOutcome.ActiveWithoutPassword, result.Outcome);
        Assert.False(result.SavedCredential);
        Assert.Empty(harness.Saved);
        Assert.Equal(0, harness.FinishCalls);
        Assert.Equal(1, harness.CancelCalls);
    }

    [Fact]
    public async Task Complete_without_identity_fails_honestly()
    {
        var harness = new Harness();
        var service = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", null, hasPassword: true));

        var result = await service.EnrollAsync("probe", null);

        Assert.Equal(GoogleEnrollmentOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(0, harness.FinishCalls);
    }

    [Fact]
    public async Task Expected_email_mismatch_at_boundary_never_saves()
    {
        var harness = new Harness();
        var service = harness.Build(
            finishResponse: new JsonObject
            {
                ["email"] = "other@gmail.com",
                ["password"] = "test-secret",
            });
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", "user@gmail.com", hasPassword: true));

        var result = await service.EnrollAsync("probe", "user@gmail.com");

        Assert.Equal(GoogleEnrollmentOutcome.WrongAccount, result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(1, harness.CancelCalls);
    }

    [Fact]
    public async Task Storage_failure_is_never_reported_as_success()
    {
        var harness = new Harness();
        var service = harness.Build(
            finishResponse: new JsonObject
            {
                ["email"] = "user@gmail.com",
                ["password"] = "test-secret",
            },
            saveFailure: new IOException("disk full"));
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", "user@gmail.com", hasPassword: true));

        var result = await service.EnrollAsync("probe", null);

        Assert.Equal(GoogleEnrollmentOutcome.Failed, result.Outcome);
        Assert.False(result.SavedCredential);
        Assert.Empty(harness.Saved);
        Assert.Contains("disk full", result.Reason);
    }

    [Theory]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("expired", "Expired")]
    [InlineData("browser_gone", "BrowserGone")]
    [InlineData("wrong_account", "WrongAccount")]
    public async Task Terminal_wire_states_map_to_outcomes(
        string wireState,
        string expectedOutcomeName)
    {
        var harness = new Harness();
        var service = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status(wireState));

        var result = await service.EnrollAsync("probe", null);

        Assert.Equal(
            Enum.Parse<GoogleEnrollmentOutcome>(expectedOutcomeName),
            result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(0, harness.FinishCalls);
    }

    [Fact]
    public async Task Cancellation_cancels_and_reports_cancelled()
    {
        var harness = new Harness();
        var service = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status("armed"));
        harness.StatusScript.Enqueue(() =>
            throw new OperationCanceledException());

        var result = await service.EnrollAsync("probe", null);

        Assert.Equal(GoogleEnrollmentOutcome.Cancelled, result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(1, harness.CancelCalls);
    }

    [Theory]
    [InlineData("BROWSER_GONE", "BrowserGone")]
    [InlineData("WRONG_ACCOUNT", "WrongAccount")]
    [InlineData("ENROLLMENT_ACTIVE", "Failed")]
    public async Task Pyhost_errors_map_to_non_secret_outcomes(
        string code,
        string expectedOutcomeName)
    {
        var harness = new Harness();
        var service = harness.Build();
        harness.StatusScript.Enqueue(() =>
            throw new PyHostException(code, code + " detail"));

        var result = await service.EnrollAsync("probe", null);

        Assert.Equal(
            Enum.Parse<GoogleEnrollmentOutcome>(expectedOutcomeName),
            result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(1, harness.CancelCalls);
    }

    [Fact]
    public async Task Progress_receives_armed_then_terminal_updates()
    {
        var harness = new Harness();
        var service = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status(
            "challenge", null, hasPassword: true));
        harness.StatusScript.Enqueue(() => Harness.Status("expired"));

        var collected = new CollectingProgress();
        var result = await service.EnrollAsync("probe", null, collected, default);

        Assert.Equal(GoogleEnrollmentOutcome.Expired, result.Outcome);
        Assert.Equal(new[] { "armed", "challenge", "expired" }, collected.States);
    }

    private sealed class CollectingProgress : IProgress<GoogleEnrollmentUpdate>
    {
        public List<string> States { get; } = [];

        public void Report(GoogleEnrollmentUpdate value) => States.Add(value.WireState);
    }
}

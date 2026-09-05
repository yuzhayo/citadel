using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Camoprof.Features.AddProfile;
using Module.Camoprof.SharedLogic;
using Xunit;

namespace Module.Camoprof.Tests;

/// <summary>
/// Drives AddProfileCoordinator through its delegate seams — no
/// browser, no pyhost process, no real DPAPI writes. Asserts the four
/// contracts that matter: credential saved exactly once on
/// Complete-with-password; has_password:false never calls finish;
/// storage failure is never success; cancellation and pyhost errors map
/// to non-secret outcomes.
/// </summary>
public class AddProfileCoordinatorTests
{
    private sealed class Harness
    {
        public List<string> Calls { get; } = [];
        public List<(string Profile, string Email, string Password)> Saved { get; } = [];
        public int FinishCalls;
        public int CancelCalls;
        public int CloseCalls;
        public Queue<Func<JsonObject>> StatusScript { get; } = [];

        public AddProfileCoordinator Build(
            JsonObject? finishResponse = null,
            Exception? saveFailure = null)
        {
            var coordinator = new AddProfileCoordinator();
            coordinator.EnsureHeadedSessionAsync = (profile, _token) =>
            {
                Calls.Add("ensure:" + profile);
                return Task.CompletedTask;
            };
            coordinator.StartAsync = (profile, _email, _token) =>
            {
                Calls.Add("start:" + profile);
                return Task.FromResult(new JsonObject { ["state"] = "armed" });
            };
            coordinator.StatusAsync = (profile, _token) =>
            {
                Calls.Add("status");
                return Task.FromResult(StatusScript.Dequeue()());
            };
            coordinator.FinishAsync = (profile, _token) =>
            {
                Calls.Add("finish");
                FinishCalls++;
                return Task.FromResult(
                    finishResponse ?? new JsonObject());
            };
            coordinator.CancelAsync = (profile, _token) =>
            {
                Calls.Add("cancel");
                CancelCalls++;
                return Task.CompletedTask;
            };
            coordinator.CloseSessionAsync = (profile, _token) =>
            {
                Calls.Add("close");
                CloseCalls++;
                return Task.CompletedTask;
            };            coordinator.SaveCredentialAsync = (profile, email, password, _token) =>
            {
                if (saveFailure is not null)
                {
                    throw saveFailure;
                }

                Saved.Add((profile, email, password));
                return Task.CompletedTask;
            };
            return coordinator;
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
        var coordinator = harness.Build(
            finishResponse: new JsonObject
            {
                ["email"] = "user@gmail.com",
                ["password"] = "test-secret",
            });
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", "user@gmail.com", hasPassword: true));

        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe"));

        Assert.Equal(AddProfileOutcome.Completed, result.Outcome);
        Assert.Equal("user@gmail.com", result.Email);
        Assert.True(result.SavedCredential);
        Assert.Single(harness.Saved);
        Assert.Equal(("probe", "user@gmail.com", "test-secret"), harness.Saved[0]);
        Assert.Equal(1, harness.FinishCalls);
        // Flow END: the finally-block cleanup closes the session once;
        // cancel is idempotent best-effort before it.
        Assert.Equal(1, harness.CancelCalls);
        Assert.Equal(1, harness.CloseCalls);
    }

    [Fact]
    public async Task Complete_without_password_never_calls_finish()
    {
        var harness = new Harness();
        var coordinator = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", "user@gmail.com", hasPassword: false));

        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe"));

        Assert.Equal(AddProfileOutcome.ActiveWithoutPassword, result.Outcome);
        Assert.False(result.SavedCredential);
        Assert.Empty(harness.Saved);
        Assert.Equal(0, harness.FinishCalls);
        Assert.Equal(1, harness.CancelCalls);
    }

    [Fact]
    public async Task Complete_without_identity_fails_honestly()
    {
        var harness = new Harness();
        var coordinator = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", null, hasPassword: true));

        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe"));

        Assert.Equal(AddProfileOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(0, harness.FinishCalls);
    }

    [Fact]
    public async Task Expected_email_mismatch_at_boundary_never_saves()
    {
        var harness = new Harness();
        var coordinator = harness.Build(
            finishResponse: new JsonObject
            {
                ["email"] = "other@gmail.com",
                ["password"] = "test-secret",
            });
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", "user@gmail.com", hasPassword: true));

        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe", "user@gmail.com"));

        Assert.Equal(AddProfileOutcome.WrongAccount, result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(1, harness.CancelCalls);
    }

    [Fact]
    public async Task Storage_failure_is_never_reported_as_success()
    {
        var harness = new Harness();
        var coordinator = harness.Build(
            finishResponse: new JsonObject
            {
                ["email"] = "user@gmail.com",
                ["password"] = "test-secret",
            },
            saveFailure: new IOException("disk full"));
        harness.StatusScript.Enqueue(() => Harness.Status(
            "complete", "user@gmail.com", hasPassword: true));

        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe"));

        Assert.Equal(AddProfileOutcome.Failed, result.Outcome);
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
        var coordinator = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status(wireState));

        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe"));

        Assert.Equal(
            Enum.Parse<AddProfileOutcome>(expectedOutcomeName),
            result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(0, harness.FinishCalls);
    }

    [Fact]
    public async Task Cancellation_cancels_and_reports_cancelled()
    {
        var harness = new Harness();
        var coordinator = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status("armed"));
        harness.StatusScript.Enqueue(() =>
            throw new OperationCanceledException());

        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe"));

        Assert.Equal(AddProfileOutcome.Cancelled, result.Outcome);
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
        var coordinator = harness.Build();
        harness.StatusScript.Enqueue(() =>
            throw new PyHostException(code, code + " detail"));

        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe"));

        Assert.Equal(
            Enum.Parse<AddProfileOutcome>(expectedOutcomeName),
            result.Outcome);
        Assert.Empty(harness.Saved);
        Assert.Equal(1, harness.CancelCalls);
    }

    [Fact]
    public async Task Progress_receives_armed_then_terminal_updates()
    {
        var harness = new Harness();
        var coordinator = harness.Build();
        harness.StatusScript.Enqueue(() => Harness.Status(
            "challenge", null, hasPassword: true));
        harness.StatusScript.Enqueue(() => Harness.Status("expired"));

        var collected = new CollectingProgress();
        var result = await coordinator.ExecuteAsync(
            new AddProfileRequest("probe"), collected, default);

        Assert.Equal(AddProfileOutcome.Expired, result.Outcome);
        Assert.Equal(new[] { "armed", "challenge", "expired" }, collected.States);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("status")]
    [InlineData("finish")]
    [InlineData("cancel")]
    public async Task Session_commands_carry_session_id_in_payload(string operation)
    {
        const string profile = "test-profile";
        const string sessionId = "resolved-session-id";
        var requests = new List<(string Command, JsonObject Payload)>();
        using var sessions = new BrowserSessionCoordinator(
            profile, sessionId, (command, payload, token) =>
            {
                requests.Add((command, (JsonObject)payload.DeepClone()));
                return Task.FromResult(new JsonObject());
            });
        var client = new AddProfilePyHostClient(sessions);

        // Real feature adapter -> real session dispatcher -> captured process I/O.
        switch (operation)
        {
            case "start":
                await client.StartAsync(profile, "user@example.com", default);
                break;
            case "status":
                await client.StatusAsync(profile, default);
                break;
            case "finish":
                await client.FinishAsync(profile, default);
                break;
            case "cancel":
                await client.CancelAsync(profile, default);
                break;
        }

        var request = Assert.Single(requests);
        Assert.Equal("camoprof.add_profile." + operation, request.Command);
        Assert.Equal(sessionId, request.Payload["session"]?.GetValue<string>());
        if (operation == "start")
            Assert.Equal("user@example.com", request.Payload["expected_email"]?.GetValue<string>());
    }

    private sealed class CollectingProgress : IProgress<AddProfileUpdate>
    {
        public List<string> States { get; } = [];

        public void Report(AddProfileUpdate value) => States.Add(value.WireState);
    }
}

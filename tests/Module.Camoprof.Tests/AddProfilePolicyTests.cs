using Module.Camoprof.Features.AddProfile;
using Xunit;

namespace Module.Camoprof.Tests;

public class AddProfilePolicyTests
{
    [Theory]
    [InlineData("armed")]
    [InlineData("password_observed")]
    [InlineData("waiting_for_google")]
    [InlineData("challenge")]
    [InlineData("complete")]
    [InlineData("consumed")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("browser_gone")]
    [InlineData("wrong_account")]
    [InlineData("failed")]
    [InlineData("something-new")] // unknown wire states degrade, never crash
    public void StatusText_is_nonempty_for_every_wire_state(string wireState)
    {
        var text = AddProfilePolicy.StatusText(wireState);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Theory]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("expired", "Expired")]
    [InlineData("browser_gone", "BrowserGone")]
    [InlineData("wrong_account", "WrongAccount")]
    [InlineData("failed", "Failed")]
    public void OutcomeFor_maps_terminal_wire_states(
        string wireState,
        string expectedOutcome)
    {
        Assert.Equal(
            Enum.Parse<AddProfileOutcome>(expectedOutcome),
            AddProfilePolicy.OutcomeFor(wireState));
    }

    [Theory]
    [InlineData("armed")]
    [InlineData("password_observed")]
    [InlineData("waiting_for_google")]
    [InlineData("challenge")]
    [InlineData("complete")]
    [InlineData("unknown")]
    public void OutcomeFor_returns_null_for_non_terminal_states(string wireState)
    {
        Assert.Null(AddProfilePolicy.OutcomeFor(wireState));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("ActiveWithoutPassword")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    [InlineData("BrowserGone")]
    [InlineData("WrongAccount")]
    [InlineData("Failed")]
    public void LauncherStatus_is_nonempty_for_every_outcome(string outcomeName)
    {
        var text = AddProfilePolicy.LauncherStatus(
            Enum.Parse<AddProfileOutcome>(outcomeName), "user@gmail.com");
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void LauncherStatus_completed_names_the_email()
    {
        Assert.Contains(
            "user@gmail.com",
            AddProfilePolicy.LauncherStatus(
                AddProfileOutcome.Completed, "user@gmail.com"));
    }

    [Fact]
    public void PollInterval_is_the_contracted_500ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), AddProfilePolicy.PollInterval);
    }
}

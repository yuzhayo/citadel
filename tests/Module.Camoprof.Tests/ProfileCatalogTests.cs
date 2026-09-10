using Module.Camoprof.SharedLogic;
using Xunit;

namespace Module.Camoprof.Tests;

public sealed class ProfileCatalogTests
{
    [Fact]
    public void OrderForDisplaySortsProfileNamesAscendingIgnoringCase()
    {
        var profiles = new[]
        {
            new ProfileEntry("profile-3", "zeta@example.com", null, false),
            new ProfileEntry("profile-1", "Alpha@example.com", null, false),
            new ProfileEntry("profile-2", "beta@example.com", null, false),
        };

        var ordered = ProfileCatalog.OrderForDisplay(profiles);

        Assert.Equal(
            ["Alpha@example.com", "beta@example.com", "zeta@example.com"],
            ordered.Select(profile => profile.DisplayName));
    }
}

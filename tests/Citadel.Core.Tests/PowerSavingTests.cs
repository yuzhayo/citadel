using Citadel.Core.Crl;

namespace Citadel.Core.Tests;

/// <summary>
/// Trap 3 and Trap 4 pinning. Enabled == true means saving is ON
/// (v0 polarity); Changed is a real event supporting += and -=.
/// </summary>
public class PowerSavingTests : IDisposable
{
    public PowerSavingTests() => PowerSaving.ResetForTests();

    public void Dispose() => PowerSaving.ResetForTests();

    // Trap 3: polarity. Gateway does
    //   if (PowerSaving.Enabled) timer.Stop(); else timer.Start();
    // Enabled == true must mean "saving is ON, stop working".
    [Fact]
    public void Enabled_True_MeansSavingIsOn()
    {
        Assert.False(PowerSaving.Enabled);

        PowerSaving.Set(true);

        Assert.True(PowerSaving.Enabled);
        Assert.Equal(PowerSaving.Flags.Animations, PowerSaving.Current);
        Assert.True(PowerSaving.On(PowerSaving.Flags.Animations));

        var timerStopped = false;
        if (PowerSaving.Enabled) timerStopped = true;
        Assert.True(timerStopped);
    }

    [Fact]
    public void SetBool_IsTheV0Writer_FiresOnceOnChange()
    {
        var fired = 0;
        PowerSaving.Changed += () => fired++;

        PowerSaving.Set(true);
        PowerSaving.Set(true);   // same value: no fire
        PowerSaving.Set(false);

        Assert.Equal(2, fired);
    }

    // Trap 4: Changed must be a real C# event — Gateway does += AND -=.
    [Fact]
    public void Changed_SupportsSubscribeAndUnsubscribe()
    {
        var fired = 0;
        void Handler() => fired++;

        PowerSaving.Changed += Handler;
        PowerSaving.Set(true);
        Assert.Equal(1, fired);

        PowerSaving.Changed -= Handler;
        PowerSaving.Set(false);
        Assert.Equal(1, fired); // unsubscribed: silent
    }

    [Fact]
    public void FlagsApi_DiffsBeforeFiring()
    {
        var fired = 0;
        PowerSaving.Changed += () => fired++;

        PowerSaving.Set(PowerSaving.Flags.Animations);
        PowerSaving.Set(PowerSaving.Flags.Animations);

        Assert.Equal(1, fired);
        Assert.Equal(PowerSaving.Flags.Animations, PowerSaving.Current);
    }
}

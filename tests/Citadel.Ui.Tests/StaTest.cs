using System.Runtime.ExceptionServices;

namespace Citadel.Ui.Tests;

/// <summary>
/// WPF objects are measured on a dedicated STA thread. This keeps the test
/// project dependency-free instead of adding an STA-specific xUnit package.
/// </summary>
internal static class StaTest
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

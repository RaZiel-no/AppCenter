using System.Threading;

namespace AppCenter.Tests;

/// <summary>
/// Runs a test body on a single-threaded-apartment thread. WPF elements refuse
/// to be built anywhere else, and the test runner's threads are not STA, so
/// anything that touches a Button or a Brush has to borrow one of these.
/// </summary>
internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // Rethrown on the calling thread so xUnit reports the assertion that
        // actually failed rather than a thread that quietly died.
        if (failure is not null)
            throw new InvalidOperationException(failure.Message, failure);
    }
}

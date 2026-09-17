using AppCenter.Services;

namespace AppCenter;

/// <summary>
/// The entry point, written out rather than left to the XAML compiler, so
/// that the warm-up is the first thing the process does. Everything from
/// the Application's own construction on is what it runs alongside.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        Warmup.Begin();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

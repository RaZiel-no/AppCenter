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
    public static int Main(string[] args)
    {
        // --help lists the options and opens no window, whatever else is on
        // the line. See CommandLine.
        if (CommandLine.AsksForHelp(args))
            return CommandLine.ShowHelp();

        // "Update all" from the command line goes to the window already open,
        // if there is one, and this launch only waits for its answer. See
        // CommandLine.
        var request = CommandLine.Parse(args);

        if (request is not null && CommandLine.HandOff(request) is { } code)
            return code;

        CommandLine.Request = request;

        // The pipe later launches hand their requests to, taken now rather
        // than when the window is up: a second launch in the meantime must
        // find this one, not run a batch of its own beside it.
        CommandLine.Reserve();

        // Closed before the batch could start: nothing was.
        if (request is not null)
            CommandLine.Outcome = CommandLine.CouldNotRun;

        Warmup.Begin();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

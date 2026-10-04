using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// `AppCenter.exe --update-all [--without-admin] [--exit]`: what the line is
/// read as, what exit code a batch ends in, and the hand-over to a window that
/// is already open - over a pipe of the test's own, so nothing here reaches an
/// App Center running on the machine.
/// </summary>
public class CommandLineTests : IDisposable
{
    private readonly Func<bool> _elevated = CommandLine.Elevated;
    private readonly Func<string> _pipeName = CommandLine.PipeName;

    public CommandLineTests()
    {
        var name = $"AppCenter.Tests.{Guid.NewGuid():N}";
        CommandLine.PipeName = () => name;
        CommandLine.Elevated = () => false;
    }

    public void Dispose()
    {
        CommandLine.Elevated = _elevated;
        CommandLine.PipeName = _pipeName;
    }

    [Fact]
    public void Reads_update_all_and_its_two_options_in_any_order_and_case()
    {
        Assert.Equal(new UpdateAllRequest(false, false), CommandLine.Parse(["--update-all"]));
        Assert.Equal(new UpdateAllRequest(true, true), CommandLine.Parse(["--EXIT", "--update-all", "--without-admin"]));

        // The options mean nothing on their own: an ordinary launch.
        Assert.Null(CommandLine.Parse(["--without-admin", "--exit"]));
        Assert.Null(CommandLine.Parse([]));
    }

    [Fact]
    public void Reads_help_in_every_spelling_and_ahead_of_everything_else()
    {
        Assert.True(CommandLine.AsksForHelp(["--help"]));
        Assert.True(CommandLine.AsksForHelp(["-h"]));
        Assert.True(CommandLine.AsksForHelp(["-?"]));
        Assert.True(CommandLine.AsksForHelp(["/?"]));
        Assert.True(CommandLine.AsksForHelp(["--update-all", "--exit", "--HELP"]));

        Assert.False(CommandLine.AsksForHelp([]));
        Assert.False(CommandLine.AsksForHelp(["--update-all", "--without-admin", "--exit"]));
    }

    [Fact]
    public void Help_names_the_build_every_option_and_every_exit_code()
    {
        var help = CommandLine.Help;

        Assert.Contains($"App Center {AppInfo.Version}", help);
        Assert.Contains("--update-all", help);
        Assert.Contains("--without-admin", help);
        Assert.Contains("--exit", help);
        Assert.Contains("--help", help);

        // Each code on a line of its own in the list, so that a code added
        // without a line here is noticed.
        foreach (var code in new[] { CommandLine.Done, CommandLine.SomeFailed, CommandLine.CouldNotRun, CommandLine.Interrupted })
            Assert.Contains($"\n  {code}  ", help);
    }

    [Fact]
    public void Help_fits_a_console_as_it_comes()
    {
        // Eighty columns, and nothing a console's code page might lack.
        foreach (var line in CommandLine.Help.Replace("\r\n", "\n").Split('\n'))
        {
            Assert.True(line.Length < 80, $"Wider than a console: {line}");
            Assert.True(line.All(c => c >= ' ' && c <= '~'), $"Not plain ASCII: {line}");
        }
    }

    [Fact]
    public void Ends_in_0_when_every_update_went_through_and_1_when_one_did_not()
    {
        var fine = new Operation { Key = Operation.UpdateAllKey, PackageName = "all packages", Kind = OperationKind.UpdateAll };
        fine.Complete(new WingetResult(0, string.Empty, string.Empty), null);

        var failed = new Operation { Key = Operation.UpdateAllKey, PackageName = "all packages", Kind = OperationKind.UpdateAll };
        failed.Complete(new WingetResult(1603, string.Empty, string.Empty), null);

        Assert.Equal(CommandLine.Done, CommandLine.ExitCodeFor(fine));
        Assert.Equal(CommandLine.SomeFailed, CommandLine.ExitCodeFor(failed));
    }

    [Fact]
    public void Runs_it_here_when_no_window_is_open_to_take_it()
    {
        Assert.Null(CommandLine.HandOff(new UpdateAllRequest(false, false)));
    }

    [Fact]
    public async Task Hands_it_to_the_open_window_and_exits_with_that_windows_answer()
    {
        UpdateAllRequest? received = null;

        CommandLine.Listen(request =>
        {
            received = request;
            return Task.FromResult(CommandLine.SomeFailed);
        });

        var code = await Task.Run(() => CommandLine.HandOff(new UpdateAllRequest(WithoutAdmin: true, Exit: true)));

        Assert.Equal(CommandLine.SomeFailed, code);

        // --exit stays behind: the open window is not the launch's to close.
        Assert.Equal(new UpdateAllRequest(WithoutAdmin: true, Exit: false), received);
    }

    [Fact]
    public async Task Says_interrupted_when_the_window_goes_away_before_it_answers()
    {
        // The window closing mid-batch - by hand, or an installer closing it -
        // drops the pipe without an answer. Some updates may have gone in.
        CommandLine.Listen(_ => throw new InvalidOperationException("closing"));

        Assert.Equal(CommandLine.Interrupted, await Task.Run(() => CommandLine.HandOff(new UpdateAllRequest(false, false))));
    }

    [Fact]
    public async Task Takes_a_request_that_arrived_before_the_window_was_up()
    {
        // The pipe is taken at launch; a request in the second before the
        // window serves it waits on the window rather than running itself.
        CommandLine.Reserve();

        var handedOff = Task.Run(() => CommandLine.HandOff(new UpdateAllRequest(false, false)));
        await Task.Delay(300);
        Assert.False(handedOff.IsCompleted);

        CommandLine.Listen(_ => Task.FromResult(CommandLine.Done));

        Assert.Equal(CommandLine.Done, await handedOff);
    }

    [Fact]
    public async Task A_second_window_leaves_the_requests_to_the_first()
    {
        CommandLine.Listen(_ => Task.FromResult(CommandLine.Done));
        CommandLine.Listen(_ => Task.FromResult(CommandLine.CouldNotRun));

        Assert.Equal(CommandLine.Done, await Task.Run(() => CommandLine.HandOff(new UpdateAllRequest(false, false))));
    }

    [Fact]
    public void Neither_hands_over_nor_listens_when_elevated()
    {
        CommandLine.Elevated = () => true;

        CommandLine.Listen(_ => Task.FromResult(CommandLine.Done));

        Assert.Null(CommandLine.HandOff(new UpdateAllRequest(false, false)));

        // Nor did the elevated one start listening, so an unelevated launch finds nobody.
        CommandLine.Elevated = () => false;
        Assert.Null(CommandLine.HandOff(new UpdateAllRequest(false, false)));
    }
}

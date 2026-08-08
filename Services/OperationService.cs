using System.Windows;

namespace AppCenter.Services;

public enum OperationKind
{
    Install,
    Update,
    Uninstall,
    UpdateAll,
}

/// <summary>
/// One winget command in flight, and every string a page shows for it.
/// The wording lives here rather than in the views so a list row, a detail
/// page and the progress line cannot drift apart.
/// </summary>
public sealed class Operation
{
    /// <summary>"Update all" touches everything, so it runs under its own key.</summary>
    public const string UpdateAllKey = "*update-all*";

    public required string Key { get; init; }
    public required string PackageName { get; init; }
    public required OperationKind Kind { get; init; }

    /// <summary>The latest line winget printed, trimmed to fit.</summary>
    public string Detail { get; private set; } = string.Empty;

    public bool IsRunning { get; private set; } = true;

    /// <summary>How it ended. Empty until it does.</summary>
    public string Summary { get; private set; } = string.Empty;

    public bool Failed { get; private set; }

    /// <summary>"Installing Firefox…" - the page-level heading.</summary>
    public string Heading => Kind switch
    {
        OperationKind.Install => $"Installing {PackageName}…",
        OperationKind.Update => $"Updating {PackageName}…",
        OperationKind.Uninstall => $"Uninstalling {PackageName}…",
        _ => "Updating all packages…",
    };

    /// <summary>"Installing…" - the short label a list row has room for.</summary>
    public string RowLabel => Kind switch
    {
        OperationKind.Install => "Installing…",
        OperationKind.Uninstall => "Uninstalling…",
        _ => "Updating…",
    };

    public string Status => Detail.Length == 0 ? Heading : $"{Heading}  {Detail}";

    internal void Report(string line) => Detail = Shorten(line);

    /// <summary>
    /// Keeps winget's own last words. Detail is deliberately not cleared:
    /// it holds the final meaningful line winget printed, which is where the
    /// useful part lives - "Successfully installed. Restart the application
    /// to complete the upgrade." or "No applicable upgrade found." winget
    /// says almost nothing on stderr, so quoting only stderr, as this used
    /// to, produced a bare exit code and no explanation at all.
    /// </summary>
    internal void Complete(WingetResult? result, string? error)
    {
        IsRunning = false;

        if (error is not null)
        {
            Failed = true;
            Summary = error;
            return;
        }

        var said = Detail.Length > 0 ? Detail : Shorten(result?.StdErr ?? string.Empty);

        if (result is { Success: true })
        {
            Summary = said.Length > 0 ? said : "Done.";
            return;
        }

        Failed = true;

        var code = result?.ExitCode ?? -1;

        // winget's own failures are the 0x8A15xxxx family, which is how they
        // are documented and searched for; an installer's own code comes
        // through as a small positive number and reads better in decimal.
        var shown = code < 0 ? $"0x{code:X8}" : code.ToString();

        Summary = $"winget exited with {shown}. {said}".TrimEnd();
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return trimmed.Length <= 110 ? trimmed : trimmed[..110] + "…";
    }
}

/// <summary>
/// Owns every install, update and uninstall for as long as it runs.
///
/// The work used to belong to whichever page started it, which meant
/// navigating away cancelled it - the page's CancellationTokenSource fired on
/// Unloaded and took winget down with it. Holding the operations here instead
/// means a page can be built, thrown away and built again while winget keeps
/// working, and each page asks what is in flight when it loads rather than
/// remembering anything itself.
///
/// Everything is touched on the UI thread: Start is called from a click, and
/// the winget callbacks - which arrive on its output-reader thread - are
/// marshalled before they reach the list or the events.
/// </summary>
public static class OperationService
{
    private static readonly List<Operation> InFlight = [];

    public static event EventHandler<Operation>? Started;
    public static event EventHandler<Operation>? Progressed;
    public static event EventHandler<Operation>? Finished;

    /// <summary>The most recently started operation still running, if any.</summary>
    public static Operation? Current => InFlight.Count > 0 ? InFlight[^1] : null;

    /// <summary>
    /// The last operation to finish, however it went, kept so a page opened
    /// or reloaded afterwards can still say what happened. Successes matter
    /// as much as failures here: "restart the application to complete the
    /// upgrade" is the whole explanation for a package that is still listed
    /// as upgradable. Cleared when anything new starts.
    /// </summary>
    public static Operation? LastOutcome { get; private set; }

    public static Operation? For(string key) =>
        InFlight.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A package can only have one command running against it, and "update
    /// all" is exclusive in both directions - it is already touching every
    /// package, so nothing else may be moving underneath it. Anything else
    /// may overlap: starting an install and going off to install something
    /// else is normal use, not an error.
    /// </summary>
    public static bool CanStart(string key) =>
        For(key) is null
        && For(Operation.UpdateAllKey) is null
        && (key != Operation.UpdateAllKey || InFlight.Count == 0);

    /// <summary>
    /// Starts a command and returns it, or null if <see cref="CanStart"/>
    /// would have refused. The command is handed a progress callback and a
    /// token; the token is never cancelled, so an install that outlives the
    /// window finishes on its own rather than being killed half-written.
    /// </summary>
    public static Operation? Start(
        string key,
        string packageName,
        OperationKind kind,
        Func<Action<string>, CancellationToken, Task<WingetResult>> command)
    {
        if (!CanStart(key))
            return null;

        var operation = new Operation { Key = key, PackageName = packageName, Kind = kind };

        InFlight.Add(operation);
        LastOutcome = null;

        Started?.Invoke(null, operation);
        _ = RunAsync(operation, command);

        return operation;
    }

    private static async Task RunAsync(
        Operation operation,
        Func<Action<string>, CancellationToken, Task<WingetResult>> command)
    {
        try
        {
            var result = await command(
                line => OnUi(() =>
                {
                    operation.Report(line);
                    Progressed?.Invoke(null, operation);
                }),
                CancellationToken.None);

            OnUi(() => Finish(operation, result, null));
        }
        catch (OperationCanceledException)
        {
            OnUi(() => Finish(operation, null, "Cancelled."));
        }
        catch (Exception ex)
        {
            OnUi(() => Finish(operation, null, $"Failed: {ex.Message}"));
        }
    }

    private static void Finish(Operation operation, WingetResult? result, string? error)
    {
        operation.Complete(result, error);
        InFlight.Remove(operation);
        LastOutcome = operation;

        Finished?.Invoke(null, operation);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}

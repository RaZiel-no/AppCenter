using Xunit;

// OperationService keeps every operation in one static list, CatalogService
// caches the parsed catalogue, and WingetService remembers whether winget could
// be started. All three are static on purpose - they outlive the pages that use
// them - so no two tests may be in them at once.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AppCenter.Tests
{
    internal static class TestDefaults
    {
        /// <summary>
        /// Whether Windows is waiting to be restarted changes what a failed
        /// install says - see PendingRestart - and the machine running the tests
        /// may well be. Every test starts from "no"; the ones about it say "yes".
        /// </summary>
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void NoPendingRestart() => AppCenter.Services.PendingRestart.Probe = () => false;

        /// <summary>
        /// What was installed over an unreadable version, and which updates wait
        /// on a restart, are kept in the settings file. The tests get
        /// dictionaries of their own and no file, so they never read the
        /// settings on this machine, let alone write them.
        /// </summary>
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void NoSettingsFile()
        {
            AppCenter.Services.UpdateMemory.UseScratch();
            AppCenter.Services.FinishingUpdates.UseScratch();
        }

        /// <summary>
        /// The Start menu is read to find what starts each update row. The
        /// tests get an empty one, so none of them enumerates the shell on the
        /// machine running them; the ones about the match hand in their own.
        /// </summary>
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void NoStartMenu() =>
            AppCenter.Services.AppLauncher.Reader =
                () => System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<AppCenter.Services.StartEntry>>([]);
    }
}

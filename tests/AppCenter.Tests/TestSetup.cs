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
    }
}

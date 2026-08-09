using Xunit;

// OperationService keeps every operation in one static list, CatalogService
// caches the parsed catalogue, and WingetService remembers whether winget could
// be started. All three are static on purpose - they outlive the pages that use
// them - so no two tests may be in them at once.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

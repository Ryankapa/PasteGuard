using Xunit;

// Log.PathOnDisk is static, so tests that redirect it must not run at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

using Xunit;

// The tests point the app at a temporary data folder through an environment variable, which is
// process-wide, so they must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

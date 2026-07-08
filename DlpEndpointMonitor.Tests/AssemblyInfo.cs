using Xunit;

// Console.SetOut/Console.SetIn mutate process-wide global state (see docs/TEST-PLAN.md and
// HARNESS rule 3): any test using them would leak output/input to a concurrently-running test
// in another class. Disabling parallelization for the whole assembly is the simplest safe fix
// given the suite is small and fast either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

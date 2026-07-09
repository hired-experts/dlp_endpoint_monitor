using Xunit;

// AlertQueueTests mutates Console.Error (process-wide global state) to capture the "dropped
// alert" log line - same hazard documented in DlpEndpointMonitor.Tests/AssemblyInfo.cs for
// Console.SetOut. Disabling parallelization for the whole assembly avoids that leaking into a
// concurrently-running test in another class.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

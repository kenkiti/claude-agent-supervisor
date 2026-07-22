using Xunit;

// xUnit parallelizes across test classes by default. Phase6UnitTests and RuntimePollerAuthExitTests
// each spin up a real BackgroundService (TaskQueue / RuntimePoller) with genuine async work and
// hardcoded polling deadlines (e.g. a 5-second WaitForTerminal). Running those concurrently with the
// rest of this ~90-test assembly caused thread-pool/scheduler contention severe enough on GitHub
// Actions' constrained Windows runners to intermittently blow those deadlines, even though the same
// tests pass reliably on a local dev machine. tests/AgentSupervisor.IntegrationTests/AssemblyInfo.cs
// disabled parallelism for the same class of reason (concurrent WebApplicationFactory hosts); mirror
// that fix here rather than just widening the timing-sensitive tests' deadlines.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

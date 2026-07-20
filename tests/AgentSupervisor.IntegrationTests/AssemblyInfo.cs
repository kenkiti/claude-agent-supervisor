using System.Runtime.CompilerServices;
using Xunit;

// Multiple test classes in this assembly each host their own WebApplicationFactory<Program>
// (a top-level-statement minimal API entry point). xUnit parallelizes across test classes by
// default, and running more than one WebApplicationFactory<Program>-based host build
// concurrently in the same process reliably breaks HostFactoryResolver's entry-point
// interception ("The entry point exited without ever building an IHost."). Disable
// cross-class parallelism for this assembly so those hosts build one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestAssemblySetup
{
    // Program.cs guards startup with a system-wide "Global\" named mutex so only one real
    // AgentSupervisor.exe can run at a time. That mutex is held for as long as the first
    // WebApplicationFactory<Program>-built host in this process stays alive -- which is the
    // whole test run, since IClassFixture doesn't dispose it until its class finishes -- so
    // every *other* test class's host build would deterministically see owner=false and
    // return before ever calling WebApplication.CreateBuilder. Skip that guard for tests.
    [ModuleInitializer]
    public static void SkipSingleInstanceGuardForTests() =>
        Environment.SetEnvironmentVariable("AGENTSUPERVISOR_SKIP_SINGLE_INSTANCE", "1");
}

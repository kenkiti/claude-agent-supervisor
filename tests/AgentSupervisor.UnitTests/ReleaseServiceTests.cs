using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class ReleaseServiceTests
{
    [Fact] public void Backup_and_restore_round_trip() { var d = Temp(); try { var f = Path.Combine(d, "a"); File.WriteAllText(f, "one"); var b = new FileBackupService().Backup(f, Path.Combine(d, "b"))!; File.WriteAllText(f, "two"); new FileBackupService().Restore(b, f); Assert.Equal("one", File.ReadAllText(f)); } finally { Directory.Delete(d, true); } }
    [Fact] public async Task Update_success_keeps_new_file() { var d = Temp(); try { var f = Path.Combine(d, "a"); var s = Path.Combine(d, "s"); File.WriteAllText(f, "old"); File.WriteAllText(s, "new"); Assert.True(await new UpdateService(new FileBackupService()).StageAndSwapAsync(f, s, Path.Combine(d, "b"), () => Task.FromResult(true))); Assert.Equal("new", File.ReadAllText(f)); } finally { Directory.Delete(d, true); } }
    [Fact] public void Sha256_matches_known_value() { var d = Temp(); try { var f = Path.Combine(d, "x"); File.WriteAllText(f, "abc"); Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Sha256.Compute(f)); } finally { Directory.Delete(d, true); } }
    private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "as8-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
}

using AgentSupervisor.Core;
using Microsoft.Data.Sqlite;

namespace AgentSupervisor.Infrastructure;

public sealed class ProjectRegistryStore
{
    private readonly string _cs;
    public ProjectRegistryStore(string connectionString) => _cs = connectionString;
    public IReadOnlyList<ProjectRecord> Projects() => Read("SELECT id,runtime_id,cwd,max_parallel,default_mode FROM projects", r => new ProjectRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
    public IReadOnlyList<RuntimeRecord> Runtimes() => Read("SELECT id,type,distribution,claude_command FROM runtimes", r => new RuntimeRecord(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)));
    public void AddRuntime(RuntimeRecord x) => Exec("INSERT INTO runtimes(id,type,distribution,claude_command) VALUES($id,$type,$dist,$cmd)", ("$id", x.Id), ("$type", x.Type), ("$dist", (object?)x.Distribution ?? DBNull.Value), ("$cmd", x.ClaudeCommand));
    public void AddProject(ProjectRecord x) => Exec("INSERT INTO projects(id,runtime_id,cwd,max_parallel,default_mode) VALUES($id,$r,$cwd,$max,$mode)", ("$id", x.Id), ("$r", x.RuntimeId), ("$cwd", x.Cwd), ("$max", x.MaxParallel), ("$mode", x.DefaultMode));
    public void UpdateProject(string id, ProjectRecord x) => Exec("UPDATE projects SET runtime_id=$r,cwd=$cwd,max_parallel=$max,default_mode=$mode WHERE id=$id", ("$id", id), ("$r", x.RuntimeId), ("$cwd", x.Cwd), ("$max", x.MaxParallel), ("$mode", x.DefaultMode));
    public void DeleteProject(string id) => Exec("DELETE FROM projects WHERE id=$id", ("$id", id));
    private List<T> Read<T>(string sql, Func<SqliteDataReader, T> map) { using var c = new SqliteConnection(_cs); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; using var r = cmd.ExecuteReader(); var a = new List<T>(); while (r.Read()) a.Add(map(r)); c.Close(); SqliteConnection.ClearPool(c); return a; }
    private void Exec(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, p.Item2); cmd.ExecuteNonQuery(); c.Close(); SqliteConnection.ClearPool(c); }
}

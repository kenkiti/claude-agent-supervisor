using System.Diagnostics;
using System.Text;
using AgentSupervisor.Core;

namespace AgentSupervisor.Infrastructure;

public interface IProcessRunner
{
    Task<RuntimeProbeResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken = default, string? cwd = null);
}
public interface IStreamingProcessRunner
{
    Task<StartedProcessHandle> StartAsync(string fileName, IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken = default);
    Task<int?> WaitForExitAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task StopAsync(string jobId, CancellationToken cancellationToken = default);
    string GetStdoutExcerpt(string jobId);
    string GetStderrExcerpt(string jobId);
}

public sealed class ProcessRunner : IProcessRunner, IStreamingProcessRunner
{
    private const int ExcerptCap = 8192;

    private sealed class TrackedProcess
    {
        public required Process Process;
        public readonly StringBuilder Stdout = new();
        public readonly StringBuilder Stderr = new();
    }

    private readonly Dictionary<string, TrackedProcess> _processes = new();

    public Task<StartedProcessHandle> StartAsync(string fileName, IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = cwd };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var tracked = new TrackedProcess { Process = new Process { StartInfo = psi, EnableRaisingEvents = true } };
        // Redirected streams must be drained continuously (not read-to-end after the fact),
        // otherwise a chatty child process can fill the OS pipe buffer and deadlock against
        // our WaitForExitAsync/timeout logic in TaskQueue.
        tracked.Process.OutputDataReceived += (_, e) => Append(tracked.Stdout, e.Data);
        tracked.Process.ErrorDataReceived += (_, e) => Append(tracked.Stderr, e.Data);
        if (!tracked.Process.Start()) throw new InvalidOperationException("Process could not be started.");
        tracked.Process.BeginOutputReadLine();
        tracked.Process.BeginErrorReadLine();
        var id = Guid.NewGuid().ToString("N");
        lock (_processes) _processes[id] = tracked;
        return Task.FromResult(new StartedProcessHandle(id, tracked.Process.Id));
    }

    public async Task<int?> WaitForExitAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        TrackedProcess? tracked;
        lock (_processes) _processes.TryGetValue(jobId, out tracked);
        if (tracked is null) return null;
        var exited = tracked.Process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(exited, Task.Delay(timeout, cancellationToken));
        if (completed != exited) return null;
        await exited;
        return tracked.Process.ExitCode;
    }

    public async Task StopAsync(string jobId, CancellationToken cancellationToken = default)
    {
        TrackedProcess? tracked;
        lock (_processes) _processes.TryGetValue(jobId, out tracked);
        if (tracked is { Process.HasExited: false })
        {
            try { tracked.Process.Kill(true); } catch (InvalidOperationException) { }
            await tracked.Process.WaitForExitAsync(cancellationToken);
        }
    }

    public string GetStdoutExcerpt(string jobId)
    {
        lock (_processes) return _processes.TryGetValue(jobId, out var tracked) ? tracked.Stdout.ToString() : string.Empty;
    }

    public string GetStderrExcerpt(string jobId)
    {
        lock (_processes) return _processes.TryGetValue(jobId, out var tracked) ? tracked.Stderr.ToString() : string.Empty;
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null) return;
        lock (sb)
        {
            if (sb.Length < ExcerptCap) sb.AppendLine(line);
        }
    }

    public async Task<RuntimeProbeResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken = default, string? cwd = null)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = cwd ?? Environment.CurrentDirectory } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return new(fileName, -1, "", "Process could not be started.");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new(fileName, process.ExitCode, output, error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(fileName, -1, "", ex.Message); }
    }
}

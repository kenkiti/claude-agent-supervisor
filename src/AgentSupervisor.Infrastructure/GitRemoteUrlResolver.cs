using AgentSupervisor.Core;

namespace AgentSupervisor.Infrastructure;

public sealed class GitRemoteUrlResolver
{
    private readonly IProcessRunner _processRunner;
    public GitRemoteUrlResolver(IProcessRunner processRunner) => _processRunner = processRunner;

    public async Task<string?> ResolveAsync(string cwd, RuntimeRecord runtime, CancellationToken cancellationToken = default)
    {
        RuntimeProbeResult result;
        if (string.Equals(runtime.Type, "windows", StringComparison.OrdinalIgnoreCase))
        {
            result = await _processRunner.RunAsync("git", new[] { "-C", cwd, "remote", "get-url", "origin" }, cancellationToken);
        }
        else if (string.Equals(runtime.Type, "wsl", StringComparison.OrdinalIgnoreCase) && runtime.Distribution is not null)
        {
            result = await _processRunner.RunAsync("wsl.exe", new[] { "-d", runtime.Distribution, "--", "git", "-C", cwd, "remote", "get-url", "origin" }, cancellationToken);
        }
        else
        {
            return null;
        }

        if (result.ExitCode != 0) return null;
        var raw = result.Output.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        return Normalize(raw);
    }

    private static string Normalize(string remoteUrl)
    {
        var url = remoteUrl;
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var withoutPrefix = url.Substring("git@".Length);
            var colonIndex = withoutPrefix.IndexOf(":", StringComparison.Ordinal);
            if (colonIndex > 0)
            {
                var host = withoutPrefix.Substring(0, colonIndex);
                var path = withoutPrefix.Substring(colonIndex + 1);
                if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://github.com/" + path;
                }
            }
        }
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url.Substring(0, url.Length - ".git".Length);
        }
        return url;
    }
}

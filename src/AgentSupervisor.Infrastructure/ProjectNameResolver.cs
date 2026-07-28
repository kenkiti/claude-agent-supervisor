using System.Collections.Concurrent;
using AgentSupervisor.Core;

namespace AgentSupervisor.Infrastructure;

public sealed class ProjectNameResolver
{
    private readonly GitRemoteUrlResolver _gitRemoteUrlResolver;
    private readonly ProjectRegistryStore _projectRegistry;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProjectNameResolver(GitRemoteUrlResolver gitRemoteUrlResolver, ProjectRegistryStore projectRegistry)
    {
        _gitRemoteUrlResolver = gitRemoteUrlResolver;
        _projectRegistry = projectRegistry;
    }

    public async Task<string?> ResolveAsync(string? cwd, string? runtimeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var key = cwd.Trim();
        var value = _cache.GetOrAdd(key, static (path, state) =>
            new Lazy<Task<string>>(() => state.Resolver.ResolveUncachedAsync(path, state.RuntimeId), LazyThreadSafetyMode.ExecutionAndPublication),
            (Resolver: this, RuntimeId: runtimeId));
        var resolved = await value.Value.WaitAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
    }

    private async Task<string> ResolveUncachedAsync(string cwd, string? runtimeId)
    {
        try
        {
            var runtime = FindRuntime(runtimeId);
            if (runtime is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var remote = await _gitRemoteUrlResolver.ResolveAsync(cwd, runtime, timeout.Token);
                var repository = ExtractGithubRepositoryName(remote);
                if (repository is not null) return repository;
            }
        }
        catch
        {
            // A missing runtime, git executable, origin remote, or repository must not
            // prevent the notification itself from being delivered.
        }

        return FolderName(cwd) ?? string.Empty;
    }

    private RuntimeRecord? FindRuntime(string? runtimeId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId)) return null;

        var runtimes = _projectRegistry.Runtimes();
        var exact = runtimes.FirstOrDefault(x => string.Equals(x.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        if (runtimeId.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
        {
            var distribution = runtimeId[4..];
            return runtimes.Count(x => string.Equals(x.Type, "wsl", StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Distribution, distribution, StringComparison.OrdinalIgnoreCase)) == 1
                ? runtimes.First(x => string.Equals(x.Type, "wsl", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Distribution, distribution, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        var typeMatches = runtimes.Where(x => string.Equals(x.Type, runtimeId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return typeMatches.Length == 1 ? typeMatches[0] : null;
    }

    private static string? ExtractGithubRepositoryName(string? normalizedUrl)
    {
        if (string.IsNullOrWhiteSpace(normalizedUrl)) return null;
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var path = uri.AbsolutePath.Trim('/');
        var name = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var decoded = Uri.UnescapeDataString(name).TrimEnd('/');
        return decoded.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? decoded[..^4] : decoded;
    }

    private static string? FolderName(string cwd)
    {
        var trimmed = cwd.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return null;

        var separator = trimmed.LastIndexOfAny(['/', '\\']);
        var name = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

using System.Security.Cryptography;
using AgentSupervisor.Core;

namespace AgentSupervisor.Infrastructure;

public interface IHookTokenProvider
{
    string GetToken(string runtimeId);
}

public sealed class HookTokenProvider : IHookTokenProvider
{
    private readonly string _directory;
    private readonly SecretStore _secrets;
    public HookTokenProvider(LocalAppDataLayout layout, SecretStore? secrets = null)
    {
        _directory = layout.Config;
        _secrets = secrets ?? new SecretStore();
        Directory.CreateDirectory(_directory);
    }

    public string GetToken(string runtimeId)
    {
        var safeRuntime = string.Concat(runtimeId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var path = Path.Combine(_directory, "hook-token-" + safeRuntime + ".bin");
        try
        {
            if (File.Exists(path)) return _secrets.Unprotect(File.ReadAllBytes(path));
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllBytes(path, _secrets.Protect(token));
            return token;
        }
        catch (CryptographicException)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllBytes(path, _secrets.Protect(token));
            return token;
        }
    }
}

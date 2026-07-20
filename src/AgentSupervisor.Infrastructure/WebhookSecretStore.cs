using AgentSupervisor.Core;

namespace AgentSupervisor.Infrastructure;

// DESIGN.md 15.4: webhook URLs must never be stored in SQLite or logs as plaintext.
// Same DPAPI-CurrentUser pattern as HookTokenProvider, but for user-supplied secrets
// rather than a generated token, so it exposes Set() and a masked-for-display read.
public sealed class WebhookSecretStore
{
    private readonly string _directory;
    private readonly SecretStore _secrets;

    public WebhookSecretStore(LocalAppDataLayout layout, SecretStore? secrets = null)
    {
        _directory = layout.Config;
        _secrets = secrets ?? new SecretStore();
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string channelId) => Path.Combine(_directory, "webhook-" + channelId + ".bin");

    public string? Get(string channelId)
    {
        var path = PathFor(channelId);
        if (!File.Exists(path)) return null;
        try { return _secrets.Unprotect(File.ReadAllBytes(path)); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public void Set(string channelId, string url) =>
        File.WriteAllBytes(PathFor(channelId), _secrets.Protect(url));

    // Never return the real URL for display -- only enough to confirm which webhook is
    // configured (DESIGN.md 15.4: "設定済み状態と末尾数文字だけを表示する").
    public string? GetMaskedSuffix(string channelId)
    {
        var url = Get(channelId);
        if (url is null) return null;
        return url.Length <= 6 ? "…" : "…" + url[^6..];
    }
}

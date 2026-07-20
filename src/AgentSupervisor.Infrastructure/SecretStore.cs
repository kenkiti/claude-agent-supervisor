using System.Security.Cryptography;
using System.Text;

namespace AgentSupervisor.Infrastructure;

public sealed class SecretStore
{
    public byte[] Protect(string value) => ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
    public string Unprotect(byte[] value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser));
}

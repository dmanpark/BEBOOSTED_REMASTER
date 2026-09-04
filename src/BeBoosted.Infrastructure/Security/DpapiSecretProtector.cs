using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BeBoosted.Application.Abstractions;

namespace BeBoosted.Infrastructure.Security;

/// <summary>Windows DPAPI at user scope: the ciphertext is useless to another account.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public bool IsAvailable => true;

    public string Protect(string plaintext)
        => Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.CurrentUser));

    public string? TryUnprotect(string ciphertext)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(ciphertext), optionalEntropy: null, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

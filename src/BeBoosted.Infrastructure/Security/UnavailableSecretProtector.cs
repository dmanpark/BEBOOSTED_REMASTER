using BeBoosted.Application.Abstractions;

namespace BeBoosted.Infrastructure.Security;

/// <summary>
/// The protector on a platform that has none yet. It reports its own absence instead of
/// throwing, so Settings can disable the cloud option with an explanation and the rest of
/// the app — heuristic and Ollama alike — carries on untouched.
/// </summary>
public sealed class UnavailableSecretProtector : ISecretProtector
{
    public bool IsAvailable => false;

    public string Protect(string plaintext)
        => throw new PlatformNotSupportedException(
            "Saving an API key isn't supported on this platform yet.");

    public string? TryUnprotect(string ciphertext) => null;
}

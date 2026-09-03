namespace BeBoosted.Application.Abstractions;

/// <summary>
/// Encrypts a secret for storage inside the app's own profile. Deliberately not an OS
/// credential vault: the whole profile relocates under BEBOOSTED_DATA_DIR, which is how
/// every test run and disposable-profile session stays isolated from the real library,
/// and a vault entry would live outside that boundary.
/// </summary>
public interface ISecretProtector
{
    /// <summary>False where no implementation exists (non-Windows, until Keychain ships).</summary>
    bool IsAvailable { get; }

    string Protect(string plaintext);

    /// <summary>The secret, or null when it cannot be decrypted — a profile copied from
    /// another machine or user. That is an expected state, not an error.</summary>
    string? TryUnprotect(string ciphertext);
}

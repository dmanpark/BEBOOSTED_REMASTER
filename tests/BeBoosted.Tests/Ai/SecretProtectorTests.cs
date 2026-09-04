using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BeBoosted.Application.Abstractions;
using BeBoosted.Infrastructure.Security;

namespace BeBoosted.Tests.Ai;

public sealed class SecretProtectorTests
{
    [Fact]
    public void TheUnavailableProtector_SaysSo_RatherThanThrowing()
    {
        ISecretProtector protector = new UnavailableSecretProtector();

        Assert.False(protector.IsAvailable);
        Assert.Null(protector.TryUnprotect("anything"));
    }

    // The Windows-only facts below are also marked [SupportedOSPlatform("windows")]: the
    // Assert.SkipUnless check is a runtime skip, not a pattern the platform-compatibility
    // analyzer recognizes as a guard, so without this attribute CA1416 would fail the build
    // on every OS even though the test correctly no-ops off Windows.
    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnWindows_ASecretRoundTrips_AndItsCiphertextIsNotThePlaintext()
    {
        Assert.SkipUnless(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only");
        ISecretProtector protector = new DpapiSecretProtector();
        Assert.True(protector.IsAvailable);

        var cipher = protector.Protect("sk-ant-secret-value");

        Assert.DoesNotContain("sk-ant", cipher, StringComparison.Ordinal);
        Assert.Equal("sk-ant-secret-value", protector.TryUnprotect(cipher));
    }

    /// <summary>The copied-profile case: undecryptable input is null, never a throw.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnWindows_GarbageCiphertext_ReturnsNull()
    {
        Assert.SkipUnless(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only");

        Assert.Null(new DpapiSecretProtector().TryUnprotect("bm90LWEtcmVhbC1ibG9i"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnWindows_NonBase64Ciphertext_ReturnsNull()
    {
        Assert.SkipUnless(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only");

        Assert.Null(new DpapiSecretProtector().TryUnprotect("!!! not base64 !!!"));
    }
}

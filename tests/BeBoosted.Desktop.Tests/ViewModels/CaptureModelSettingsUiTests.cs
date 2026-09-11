using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class CaptureModelSettingsUiTests
{
    private sealed class FakeProtector(bool available) : ISecretProtector
    {
        public bool IsAvailable => available;

        public string Protect(string plaintext) => available
            ? "enc:" + plaintext
            : throw new PlatformNotSupportedException();

        public string? TryUnprotect(string ciphertext)
            => ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : null;
    }

    private static (SettingsViewModel Vm, CaptureModelSettings Settings) Create(bool protectorAvailable = true)
    {
        var store = new InMemorySettingsStore();
        var capture = new CaptureModelSettings(store);
        var vm = new SettingsViewModel(
            new FakeAppDataPaths(), new AiPermissionSettings(store), capture,
            new FakeProtector(protectorAvailable));
        return (vm, capture);
    }

    [Fact]
    public void TheDefaultSelection_IsTheBuiltInHeuristic()
    {
        var (vm, _) = Create();

        Assert.True(vm.IsCaptureHeuristic);
        Assert.False(vm.IsCaptureOllama);
        Assert.False(vm.IsCaptureClaude);
    }

    [Fact]
    public void ChoosingOllama_PersistsIt()
    {
        var (vm, settings) = Create();

        vm.IsCaptureOllama = true;

        Assert.Equal(CaptureModelSource.Ollama, settings.Source);
        Assert.False(vm.IsCaptureHeuristic);
    }

    [Fact]
    public void SavingAKey_StoresOnlyCiphertext_AndClearsTheEntryBox()
    {
        var (vm, settings) = Create();
        vm.ApiKeyEntry = "sk-ant-secret";

        vm.SaveApiKeyCommand.Execute(null);

        Assert.Equal("enc:sk-ant-secret", settings.ProtectedClaudeKey);
        Assert.Equal(string.Empty, vm.ApiKeyEntry);
        Assert.True(vm.HasSavedKey);
    }

    [Fact]
    public void RemovingTheKey_ClearsIt()
    {
        var (vm, settings) = Create();
        vm.ApiKeyEntry = "sk-ant-secret";
        vm.SaveApiKeyCommand.Execute(null);

        vm.RemoveApiKeyCommand.Execute(null);

        Assert.Null(settings.ProtectedClaudeKey);
        Assert.False(vm.HasSavedKey);
    }

    /// <summary>The key is write-only: nothing on the view model can read it back.</summary>
    [Fact]
    public void TheSavedKey_IsNeverExposedForDisplay()
    {
        var (vm, _) = Create();
        vm.ApiKeyEntry = "sk-ant-secret";
        vm.SaveApiKeyCommand.Execute(null);

        Assert.DoesNotContain(
            typeof(SettingsViewModel).GetProperties(),
            property => property.PropertyType == typeof(string)
                && property.GetIndexParameters().Length == 0 // an indexer would throw
                && property.GetValue(vm) as string == "sk-ant-secret");
    }

    [Fact]
    public void WithNoProtector_ClaudeCannotBeChosen()
    {
        var (vm, settings) = Create(protectorAvailable: false);

        Assert.False(vm.CanUseClaude);

        vm.IsCaptureClaude = true;

        Assert.NotEqual(CaptureModelSource.Claude, settings.Source);
    }

    /// <summary>
    /// The spec's Testing section requires "consent copy present" among the Settings UI
    /// tests. These three strings are the one privacy-facing text with nothing else
    /// holding them in place — they are what makes cloud capture honest and opt-in, so
    /// the full text (including the em dash) is asserted exactly, not as a substring,
    /// so a future edit cannot quietly reword what the app promises about the user's
    /// data.
    /// </summary>
    [Theory]
    [InlineData(
        CaptureModelSource.Claude,
        "The message you type, your project names, and today's date leave your computer — "
        + "only when you press send.")]
    // The Ollama line is the default-address wording. It is no longer a fixed string:
    // it follows the endpoint, because the endpoint is what decides whether the claim
    // is true. The address-dependent cases are pinned separately below.
    [InlineData(
        CaptureModelSource.Ollama,
        "Your message goes to Ollama on this computer. Nothing leaves this computer.")]
    [InlineData(
        CaptureModelSource.Heuristic,
        "Nothing is sent anywhere. Task capture uses built-in rules.")]
    public void TheConsentText_MatchesTheApprovedCopy_ForEachSource(
        CaptureModelSource source, string expected)
    {
        var (vm, settings) = Create();
        settings.Source = source;

        Assert.Equal(expected, vm.ConsentText);
    }

    /// <summary>
    /// The old copy promised "Nothing leaves it" unconditionally, which the app cannot
    /// guarantee: the endpoint is a text box, and pointing it at another machine makes
    /// that sentence false while it is still on screen. The promise now follows the
    /// address, because the address is what decides it.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    public void WithALocalOllamaAddress_TheConsentTextPromisesNothingLeaves(string endpoint)
    {
        var (vm, settings) = Create();
        settings.OllamaEndpoint = endpoint;
        vm.IsCaptureOllama = true;

        Assert.Contains("Nothing leaves this computer", vm.ConsentText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://192.168.1.50:11434")]
    [InlineData("http://ollama.example.com:11434")]
    public void WithARemoteOllamaAddress_TheConsentTextSaysTheMessageLeaves(string endpoint)
    {
        var (vm, settings) = Create();
        settings.OllamaEndpoint = endpoint;
        vm.IsCaptureOllama = true;

        Assert.DoesNotContain("Nothing leaves this computer", vm.ConsentText, StringComparison.Ordinal);
        Assert.Contains("leaves this computer", vm.ConsentText, StringComparison.Ordinal);
    }

    /// <summary>A malformed address is not a promise the app can keep either.</summary>
    [Fact]
    public void WithAnUnparseableOllamaAddress_TheConsentTextDoesNotPromiseLocality()
    {
        var (vm, settings) = Create();
        settings.OllamaEndpoint = "not a url";
        vm.IsCaptureOllama = true;

        Assert.DoesNotContain("Nothing leaves this computer", vm.ConsentText, StringComparison.Ordinal);
    }
}

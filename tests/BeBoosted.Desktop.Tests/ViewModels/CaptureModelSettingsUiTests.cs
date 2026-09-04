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
    [InlineData(
        CaptureModelSource.Ollama,
        "Your message goes to the model running on this computer. Nothing leaves it.")]
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
}

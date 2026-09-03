using BeBoosted.Application.Ai;
using BeBoosted.Tests.Support;

namespace BeBoosted.Tests.Ai;

public sealed class CaptureModelSettingsTests
{
    [Fact]
    public void UnsetSource_ReadsAsHeuristic_SoNoExistingProfileChangesBehavior()
    {
        var settings = new CaptureModelSettings(new InMemorySettingsStore());
        Assert.Equal(CaptureModelSource.Heuristic, settings.Source);
    }

    [Fact]
    public void UnrecognisedSource_ReadsAsHeuristic_RatherThanSelectingAMissingBackend()
    {
        var store = new InMemorySettingsStore();
        store.Set("ai.captureModel.source", "gpt-9");
        Assert.Equal(CaptureModelSource.Heuristic, new CaptureModelSettings(store).Source);
    }

    [Theory]
    [InlineData(CaptureModelSource.Ollama)]
    [InlineData(CaptureModelSource.Claude)]
    [InlineData(CaptureModelSource.Heuristic)]
    public void Source_RoundTrips(CaptureModelSource source)
    {
        var store = new InMemorySettingsStore();
        new CaptureModelSettings(store).Source = source;
        Assert.Equal(source, new CaptureModelSettings(store).Source);
    }

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var settings = new CaptureModelSettings(new InMemorySettingsStore());
        Assert.Equal("claude-sonnet-4-6", settings.ClaudeModel);
        Assert.Equal("http://localhost:11434", settings.OllamaEndpoint);
        Assert.Equal("qwen2.5:7b-instruct", settings.OllamaModel);
        Assert.Null(settings.ProtectedClaudeKey);
    }

    [Fact]
    public void ClearingTheKey_RemovesItRatherThanStoringEmpty()
    {
        var store = new InMemorySettingsStore();
        var settings = new CaptureModelSettings(store) { ProtectedClaudeKey = "cipher" };
        Assert.Equal("cipher", new CaptureModelSettings(store).ProtectedClaudeKey);

        settings.ProtectedClaudeKey = null;

        Assert.Null(store.Get("ai.claude.apiKey"));
    }
}

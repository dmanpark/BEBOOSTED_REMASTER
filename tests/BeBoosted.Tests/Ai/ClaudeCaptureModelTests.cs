using System.Net;
using System.Text;
using Anthropic.Core;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Infrastructure.Security;
using BeBoosted.Tests.Support;

namespace BeBoosted.Tests.Ai;

public sealed class ClaudeCaptureModelTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>A protector that round-trips in memory, so these tests run on any OS.</summary>
    private sealed class ReversibleProtector : ISecretProtector
    {
        public bool IsAvailable => true;

        public string Protect(string plaintext) => "enc:" + plaintext;

        public string? TryUnprotect(string ciphertext)
            => ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : null;
    }

    private static CaptureModelSettings SettingsWithKey(string? protectedKey)
    {
        var settings = new CaptureModelSettings(new InMemorySettingsStore());
        settings.ProtectedClaudeKey = protectedKey;
        return settings;
    }

    private static CaptureRequest Request() => new(
        "Finish the DECA presentation before Friday.", ["Schoolwork"], new DateOnly(2026, 9, 3));

    [Fact]
    public async Task WithNoKeySaved_Throws_SoTheRouterFallsBack()
    {
        var model = new ClaudeCaptureModel(SettingsWithKey(null), new ReversibleProtector());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.ExtractAsync(Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>The copied-profile case reaches the same fallback as a missing key.</summary>
    [Fact]
    public async Task WithAnUndecryptableKey_Throws()
    {
        var model = new ClaudeCaptureModel(
            SettingsWithKey("not-encrypted-by-this-user"), new ReversibleProtector());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.ExtractAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsDraftsFromTheModelsTextBlock()
    {
        var body = """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-6",
             "content":[{"type":"text","text":"{\"tasks\":[{\"title\":\"Finish DECA presentation\"}]}"}],
             "stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":10}}
            """;
        var model = new ClaudeCaptureModel(
            SettingsWithKey("enc:sk-ant-test"), new ReversibleProtector(),
            new StubHandler(HttpStatusCode.OK, body));

        var drafts = await model.ExtractAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("Finish DECA presentation", Assert.Single(drafts).Title);
    }

    [Fact]
    public async Task AnAuthFailure_Throws_SoTheRouterFallsBack()
    {
        var model = new ClaudeCaptureModel(
            SettingsWithKey("enc:sk-ant-bad"), new ReversibleProtector(),
            new StubHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key"}}"""));

        await Assert.ThrowsAnyAsync<Exception>(
            () => model.ExtractAsync(Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The spec requires a 15-second timeout on this backend. The SDK's own default
    /// (<see cref="ClientOptions.DefaultTimeout"/>) is 10 minutes — a hung or
    /// rate-limited endpoint left unconfigured would disable the composer for up to
    /// that long, with no notice and no drafts. Pinned against the constant itself,
    /// not just a magic number, so a future edit cannot silently drift back to the
    /// SDK default.
    /// </summary>
    [Fact]
    public void TheConfiguredTimeout_Is15Seconds_NotTheSdkDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ClaudeCaptureModel.RequestTimeout);
        Assert.NotEqual(ClientOptions.DefaultTimeout, ClaudeCaptureModel.RequestTimeout);
    }

    /// <summary>
    /// The spec's non-goals rule out retry loops: "One attempt, then fallback." The
    /// SDK retries connection errors, 408, 409, 429 and 5xx twice by default
    /// (<see cref="ClientOptions.DefaultMaxRetries"/>) — exactly the failure classes
    /// that should instead fall straight through to the heuristic.
    /// </summary>
    [Fact]
    public void RetriesAreDisabled_NotTheSdkDefaultOfTwo()
    {
        Assert.Equal(0, ClaudeCaptureModel.RequestMaxRetries);
        Assert.NotEqual(ClaudeCaptureModel.RequestMaxRetries, ClientOptions.DefaultMaxRetries);
    }

    /// <summary>
    /// Proves the settings actually take effect on the client the production path
    /// builds (no injected handler), not just that the constants have the right
    /// values in isolation.
    /// </summary>
    [Fact]
    public void CreateClient_WithNoHandler_AppliesTheTimeoutAndDisablesRetries()
    {
        var model = new ClaudeCaptureModel(SettingsWithKey(null), new ReversibleProtector());

        using var client = model.CreateClient("sk-ant-test");

        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        Assert.Equal(0, client.MaxRetries);
    }

    /// <summary>
    /// The same guarantee must hold when a handler is injected (the shape every other
    /// test in this file uses), so the production and test paths cannot silently
    /// diverge on timeout/retry behavior.
    /// </summary>
    [Fact]
    public void CreateClient_WithAStubHandler_StillAppliesTheTimeoutAndDisablesRetries()
    {
        var model = new ClaudeCaptureModel(
            SettingsWithKey(null), new ReversibleProtector(),
            new StubHandler(HttpStatusCode.OK, "{}"));

        using var client = model.CreateClient("sk-ant-test");

        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        Assert.Equal(0, client.MaxRetries);
    }
}

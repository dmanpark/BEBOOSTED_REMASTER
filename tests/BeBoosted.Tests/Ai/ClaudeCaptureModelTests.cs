using System.Net;
using System.Text;
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
}

using System.Net;
using System.Text;
using BeBoosted.Application.Ai;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Tests.Support;

namespace BeBoosted.Tests.Ai;

public sealed class OllamaCaptureModelTests
{
    /// <summary>An HttpClient whose every response — or failure — the test dictates.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static (OllamaCaptureModel Model, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var settings = new CaptureModelSettings(new InMemorySettingsStore());
        return (new OllamaCaptureModel(new HttpClient(handler), settings), handler);
    }

    private static CaptureRequest Request() => new(
        "Finish the DECA presentation before Friday.", ["Schoolwork"], new DateOnly(2026, 9, 3));

    [Fact]
    public async Task ReadsTheModelsJsonOutOfTheResponseEnvelope()
    {
        var (model, _) = Create(_ => Json(
            """{"response": "{\"tasks\":[{\"title\":\"Finish DECA presentation\"}]}"}"""));

        var drafts = await model.ExtractAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("Finish DECA presentation", Assert.Single(drafts).Title);
    }

    [Fact]
    public async Task PostsTheConfiguredModelAndJsonFormat()
    {
        var (model, handler) = Create(_ => Json("""{"response": "{\"tasks\":[]}"}"""));

        await model.ExtractAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastBody);
        Assert.Contains("qwen2.5:7b-instruct", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"json\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectionRefusal_Throws_SoTheRouterFallsBack()
    {
        var (model, _) = Create(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => model.ExtractAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnErrorStatus_Throws()
    {
        var (model, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"model not found"}"""),
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => model.ExtractAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANonJsonModelReply_ThrowsFormatException()
    {
        var (model, _) = Create(_ => Json("""{"response": "I'm not sure what you mean."}"""));

        await Assert.ThrowsAsync<FormatException>(
            () => model.ExtractAsync(Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuggestMetadata_ReturnsTheSingleDraft()
    {
        var (model, _) = Create(_ => Json(
            """{"response": "{\"tasks\":[{\"title\":\"Email Ms. Rivera\",\"estimated_minutes\":10}]}"}"""));

        var draft = await model.SuggestMetadataAsync(
            new CaptureRequest("Email Ms. Rivera", [], new DateOnly(2026, 9, 3)),
            TestContext.Current.CancellationToken);

        Assert.Equal(10, draft?.EstimatedMinutes);
    }

    [Fact]
    public async Task SuggestMetadata_WithNoEntries_ReturnsNull()
    {
        var (model, _) = Create(_ => Json("""{"response": "{\"tasks\":[]}"}"""));

        Assert.Null(await model.SuggestMetadataAsync(
            new CaptureRequest("Email Ms. Rivera", [], new DateOnly(2026, 9, 3)),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Ollama evicts an idle model after about five minutes, so without this every
    /// capture that follows a pause pays the cold-load cost again. Measured on the
    /// development machine: a cold qwen2.5:7b-instruct answers in 16-18s, a warm one
    /// in ~11s. Keeping the model resident is what moves the common case off the
    /// timeout, so it is pinned on the wire rather than left to a default.
    /// </summary>
    [Fact]
    public async Task TheRequest_AsksOllamaToKeepTheModelResident()
    {
        var (model, handler) = Create(_ => Json("""{"response": "{\"tasks\":[]}"}"""));

        await model.ExtractAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"keep_alive\":\"30m\"", handler.LastBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The timeout is a ceiling for a wedged server, not a target. It has to clear a
    /// cold local model load: 15s (the original value) sat in the middle of the real
    /// 10.8-18.1s spread, so captures failed about half the time and fell back to the
    /// built-in parser after making the user wait the full budget first.
    /// </summary>
    [Fact]
    public void TheRequestTimeout_ClearsAColdModelLoad()
    {
        Assert.True(
            OllamaCaptureModel.RequestTimeout >= TimeSpan.FromSeconds(30),
            $"a cold 7B load measured 18.1s; {OllamaCaptureModel.RequestTimeout} leaves no headroom");
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeBoosted.Application.Ai;

namespace BeBoosted.Infrastructure.Ai;

/// <summary>
/// A local model through Ollama's native /api/generate. No SDK: the surface used is one
/// POST. Nothing leaves the machine, which is why this backend needs no key and no
/// consent beyond choosing it.
/// </summary>
public sealed class OllamaCaptureModel(HttpClient client, CaptureModelSettings settings)
    : ICaptureModel
{
    /// <summary>
    /// The ceiling on one capture, not a target. It has to clear a cold local model
    /// load: measured on a development machine, qwen2.5:7b-instruct answers in
    /// 16-18s cold and ~11s warm. The original 15s sat inside that spread, so
    /// captures failed roughly half the time — and failed expensively, making the
    /// user wait the whole budget before falling back to the built-in parser.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long Ollama should keep the model in memory after answering. Ollama
    /// evicts an idle model after about five minutes, so without this every capture
    /// following a pause pays the cold-load cost again — which is what pushed the
    /// common case over the old timeout. This is what makes a capture ~11s rather
    /// than ~17s; the timeout above only stops a wedged server.
    /// </summary>
    internal const string KeepAlive = "30m";

    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("keep_alive")] string KeepAliveFor);

    private sealed record GenerateResponse(
        [property: JsonPropertyName("response")] string? Response);

    public async Task<IReadOnlyList<CaptureDraft>> ExtractAsync(
        CaptureRequest request, CancellationToken cancellationToken = default)
        => CaptureDraftParser.Parse(
            await GenerateAsync(CaptureExtractionPrompt.System, request, cancellationToken));

    public async Task<CaptureDraft?> SuggestMetadataAsync(
        CaptureRequest request, CancellationToken cancellationToken = default)
    {
        var drafts = CaptureDraftParser.Parse(
            await GenerateAsync(CaptureExtractionPrompt.MetadataSystem, request, cancellationToken));
        return drafts.Count > 0 ? drafts[0] : null;
    }

    private async Task<string> GenerateAsync(
        string system, CaptureRequest request, CancellationToken cancellationToken)
    {
        var endpoint = settings.OllamaEndpoint.TrimEnd('/') + "/api/generate";
        var payload = new GenerateRequest(
            settings.OllamaModel, system, CaptureExtractionPrompt.BuildUser(request),
            Format: "json", Stream: false, KeepAliveFor: KeepAlive);

        using var response = await client.PostAsJsonAsync(endpoint, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken);

        // The model's JSON is a string inside the envelope, not the envelope itself.
        return envelope?.Response
            ?? throw new FormatException("Ollama returned no response text.");
    }
}

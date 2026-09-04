using System.Text;
using Anthropic;
using Anthropic.Models.Messages;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;

namespace BeBoosted.Infrastructure.Ai;

/// <summary>
/// Capture through the Anthropic API. The key is decrypted per call, so saving one in
/// Settings takes effect immediately and no plaintext is held any longer than a request.
/// </summary>
public sealed class ClaudeCaptureModel(
    CaptureModelSettings settings,
    ISecretProtector protector,
    HttpMessageHandler? handler = null) : ICaptureModel
{
    private const int MaxTokens = 1024;

    public async Task<IReadOnlyList<CaptureDraft>> ExtractAsync(
        CaptureRequest request, CancellationToken cancellationToken = default)
        => CaptureDraftParser.Parse(
            await CompleteAsync(CaptureExtractionPrompt.System, request, cancellationToken));

    public async Task<CaptureDraft?> SuggestMetadataAsync(
        CaptureRequest request, CancellationToken cancellationToken = default)
    {
        var drafts = CaptureDraftParser.Parse(
            await CompleteAsync(CaptureExtractionPrompt.MetadataSystem, request, cancellationToken));
        return drafts.Count > 0 ? drafts[0] : null;
    }

    private async Task<string> CompleteAsync(
        string system, CaptureRequest request, CancellationToken cancellationToken)
    {
        // A missing key and an undecryptable one are the same event to the user: this
        // machine cannot reach Claude. Both land on the router's fallback.
        var key = settings.ProtectedClaudeKey is { } cipher ? protector.TryUnprotect(cipher) : null;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("No usable Claude API key is saved.");
        }

        var client = handler is null
            ? new AnthropicClient { ApiKey = key }
            : new AnthropicClient { ApiKey = key, HttpClient = new HttpClient(handler) };

        var message = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = settings.ClaudeModel,
                MaxTokens = MaxTokens,
                System = system,
                Messages =
                [
                    new() { Role = Role.User, Content = CaptureExtractionPrompt.BuildUser(request) },
                ],
            },
            cancellationToken);

        var text = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock) && textBlock.Text is { Length: > 0 } value)
            {
                text.Append(value);
            }
        }

        return text.Length > 0
            ? text.ToString()
            : throw new FormatException("Claude returned no text content.");
    }
}

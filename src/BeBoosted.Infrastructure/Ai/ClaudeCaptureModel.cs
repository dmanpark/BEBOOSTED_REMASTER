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

    /// <summary>
    /// The spec's degradation table promises a 15-second timeout on this backend. The
    /// SDK's own default (<see cref="Anthropic.Core.ClientOptions.DefaultTimeout"/>) is
    /// 10 minutes, which a hung or rate-limited endpoint would hold the composer
    /// hostage for — <c>ChatViewModel</c> awaits this with no cancellation token on a
    /// non-concurrent command. Internal (not private) so a test can pin the value
    /// without exercising the network path.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The spec's non-goals rule out retry loops: "One attempt, then fallback." The
    /// SDK otherwise retries connection errors, 408, 409, 429 and 5xx twice by default
    /// (<see cref="Anthropic.Core.ClientOptions.DefaultMaxRetries"/>) — exactly the
    /// failure classes that should instead reach <see cref="RoutedAiProvider"/>'s
    /// fallback immediately.
    /// </summary>
    internal const int RequestMaxRetries = 0;

    /// <summary>
    /// Builds the client with the timeout and retry guarantees applied regardless of
    /// whether a test handler is injected, so the production path (no handler) cannot
    /// silently diverge from what the tests exercise. Internal so a test can assert
    /// <see cref="Anthropic.AnthropicClient.Timeout"/> and
    /// <see cref="Anthropic.AnthropicClient.MaxRetries"/> took effect without a live call.
    /// </summary>
    internal AnthropicClient CreateClient(string apiKey) => handler is null
        ? new AnthropicClient
        {
            ApiKey = apiKey,
            Timeout = RequestTimeout,
            MaxRetries = RequestMaxRetries,
        }
        : new AnthropicClient
        {
            ApiKey = apiKey,
            HttpClient = new HttpClient(handler),
            Timeout = RequestTimeout,
            MaxRetries = RequestMaxRetries,
        };

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

        using var client = CreateClient(key);

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

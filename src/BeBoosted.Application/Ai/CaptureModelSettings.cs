using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Settings;

namespace BeBoosted.Application.Ai;

/// <summary>Which parser turns a captured message into task drafts.</summary>
public enum CaptureModelSource
{
    /// <summary>The built-in rule-based parser. No model, nothing leaves the machine.</summary>
    Heuristic = 0,
    Ollama = 1,
    Claude = 2,
}

/// <summary>
/// The capture model's configuration, shaped like <see cref="AiPermissionSettings"/>.
/// An unset or unrecognised source reads as <see cref="CaptureModelSource.Heuristic"/>:
/// every existing profile keeps its current behavior, and a hand-edited database cannot
/// select a backend that does not exist.
/// </summary>
public sealed class CaptureModelSettings(ISettingsStore store)
{
    public const string DefaultClaudeModel = "claude-sonnet-4-6";
    public const string DefaultOllamaEndpoint = "http://localhost:11434";
    public const string DefaultOllamaModel = "qwen2.5:7b-instruct";

    public CaptureModelSource Source
    {
        get => store.Get(SettingKeys.CaptureModelSource) switch
        {
            "ollama" => CaptureModelSource.Ollama,
            "claude" => CaptureModelSource.Claude,
            _ => CaptureModelSource.Heuristic,
        };
        set => store.Set(SettingKeys.CaptureModelSource, value switch
        {
            CaptureModelSource.Ollama => "ollama",
            CaptureModelSource.Claude => "claude",
            _ => "heuristic",
        });
    }

    public string ClaudeModel
    {
        get => Read(SettingKeys.ClaudeModel, DefaultClaudeModel);
        set => store.Set(SettingKeys.ClaudeModel, value.Trim());
    }

    /// <summary>The encrypted key, or null when none is saved. Never plaintext.</summary>
    public string? ProtectedClaudeKey
    {
        get => store.Get(SettingKeys.ClaudeApiKey) is { Length: > 0 } cipher ? cipher : null;
        set
        {
            if (value is { Length: > 0 })
            {
                store.Set(SettingKeys.ClaudeApiKey, value);
            }
            else
            {
                store.Remove(SettingKeys.ClaudeApiKey);
            }
        }
    }

    public string OllamaEndpoint
    {
        get => Read(SettingKeys.OllamaEndpoint, DefaultOllamaEndpoint);
        set => store.Set(SettingKeys.OllamaEndpoint, value.Trim());
    }

    public string OllamaModel
    {
        get => Read(SettingKeys.OllamaModel, DefaultOllamaModel);
        set => store.Set(SettingKeys.OllamaModel, value.Trim());
    }

    private string Read(string key, string fallback)
        => store.Get(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}

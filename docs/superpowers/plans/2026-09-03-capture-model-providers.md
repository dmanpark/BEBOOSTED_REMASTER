# Pluggable Capture Models Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route task capture through a real language model — Claude or a local Ollama model — while the built-in heuristic stays the default and the fallback.

**Architecture:** A narrow `ICaptureModel` port carries two backends (Anthropic C# SDK, Ollama over `HttpClient`). A `RoutedAiProvider` becomes the single registered `IAiProvider`: it reads the configured source per call, delegates capture, falls back to `LocalHeuristicAiProvider` on any failure with a user-visible notice, and passes project Q&A to the heuristic unchanged. Backends never see domain identifiers — the router maps project names to `ProjectId` in both directions.

**Tech Stack:** C# 13 / .NET 10, Avalonia 12 MVVM, xUnit v3, `Anthropic` 12.45.0, `System.Security.Cryptography.ProtectedData` 10.0.11.

**Spec:** `docs/superpowers/specs/2026-09-03-capture-model-providers-design.md`

## Global Constraints

- **TDD, always.** Every task writes a failing test first and watches it fail for the right reason before implementing. This repo's whole culture rests on it.
- **No live API calls in any test.** Claude and Ollama transports are tested against stub/fake HTTP layers only. The one real call is a manual live check in Task 12.
- **Central package management:** new dependencies go in `Directory.Packages.props` as `<PackageVersion>`, and the consuming `.csproj` gets a versionless `<PackageReference>`.
- **Dependency direction:** Desktop→Application→Domain, Infrastructure→Application→Domain. `ICaptureModel`, `ISecretProtector`, the prompt, the parser, and the settings accessor live in **Application**; both backends, the protector implementation, and the router live in **Infrastructure**.
- **Default source is `heuristic`.** An unset or unrecognised `ai.captureModel.source` reads as `heuristic`, so no existing profile changes behavior.
- **Settings keys, exact strings:** `ai.captureModel.source`, `ai.claude.model` (default `claude-sonnet-4-6`), `ai.claude.apiKey`, `ai.ollama.endpoint` (default `http://localhost:11434`), `ai.ollama.model` (default `qwen2.5:7b-instruct`).
- **Timeout:** 15 seconds per model call, both backends.
- **The key is never redisplayed, never logged.** Only ciphertext is persisted.
- **Gates before every commit that touches code:** `dotnet test BeBoosted.slnx` green, `dotnet build BeBoosted.slnx -warnaserror` clean, `dotnet format BeBoosted.slnx --verify-no-changes` clean.
- **Run tests from the repo root** using the paths shown; a `dotnet test` filter is `--filter "FullyQualifiedName~Name"`.

---

## File Structure

**Create — Application:**
- `src/BeBoosted.Application/Ai/ICaptureModel.cs` — the narrow port, `CaptureRequest`, `CaptureDraft`
- `src/BeBoosted.Application/Ai/CaptureExtractionPrompt.cs` — the shared prompt text
- `src/BeBoosted.Application/Ai/CaptureDraftParser.cs` — shared JSON → `CaptureDraft` validation
- `src/BeBoosted.Application/Ai/CaptureModelSettings.cs` — `CaptureModelSource` enum + settings accessor
- `src/BeBoosted.Application/Abstractions/ISecretProtector.cs` — protector seam

**Create — Infrastructure:**
- `src/BeBoosted.Infrastructure/Security/DpapiSecretProtector.cs` + `UnavailableSecretProtector.cs`
- `src/BeBoosted.Infrastructure/Ai/OllamaCaptureModel.cs`
- `src/BeBoosted.Infrastructure/Ai/ClaudeCaptureModel.cs`
- `src/BeBoosted.Infrastructure/Ai/RoutedAiProvider.cs`

**Modify:**
- `src/BeBoosted.Application/Settings/SettingKeys.cs` — five new keys
- `src/BeBoosted.Application/Ai/IAiProvider.cs` — `ExtractTasksAsync` returns `CaptureExtractionResult`
- `src/BeBoosted.Application/Ai/AiService.cs` — carry the notice into `TaskExtractionOutcome`
- `src/BeBoosted.Infrastructure/Ai/LocalHeuristicAiProvider.cs` — conform to the new return type
- `src/BeBoosted.Infrastructure/ServiceCollectionExtensions.cs` — register the new graph
- `src/BeBoosted.Desktop/ViewModels/ChatViewModel.cs` — render the degraded notice
- `src/BeBoosted.Desktop/ViewModels/SettingsViewModel.cs` + `Views/SettingsView.axaml` — the Capture model card
- `Directory.Packages.props`, `src/BeBoosted.Infrastructure/BeBoosted.Infrastructure.csproj`

**Test files:** `tests/BeBoosted.Tests/Ai/CaptureDraftParserTests.cs`, `CaptureModelSettingsTests.cs`, `OllamaCaptureModelTests.cs`, `ClaudeCaptureModelTests.cs`, `RoutedAiProviderTests.cs`, `SecretProtectorTests.cs`; `tests/BeBoosted.Desktop.Tests/ViewModels/CaptureModelSettingsUiTests.cs`, `ChatDegradedNoticeTests.cs`.

---

### Task 1: Settings keys and the source accessor

**Files:**
- Modify: `src/BeBoosted.Application/Settings/SettingKeys.cs`
- Create: `src/BeBoosted.Application/Ai/CaptureModelSettings.cs`
- Test: `tests/BeBoosted.Tests/Ai/CaptureModelSettingsTests.cs`

**Interfaces:**
- Consumes: `ISettingsStore` (`string? Get(string)`, `void Set(string, string)`, `void Remove(string)`).
- Produces: `enum CaptureModelSource { Heuristic, Ollama, Claude }`; `CaptureModelSettings(ISettingsStore store)` with `CaptureModelSource Source { get; set; }`, `string ClaudeModel { get; set; }`, `string? ProtectedClaudeKey { get; set; }` (null clears), `string OllamaEndpoint { get; set; }`, `string OllamaModel { get; set; }`.

- [ ] **Step 1: Write the failing test**

```csharp
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
```

**Note:** `InMemorySettingsStore` exists in the Desktop test project only. If `tests/BeBoosted.Tests/Support/` has no equivalent, add this alongside the test file in `tests/BeBoosted.Tests/Support/InMemorySettingsStore.cs`:

```csharp
using BeBoosted.Application.Abstractions;

namespace BeBoosted.Tests.Support;

public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value) => _values[key] = value;

    public void Remove(string key) => _values.Remove(key);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~CaptureModelSettingsTests"`
Expected: FAIL to build — `CaptureModelSettings` and `CaptureModelSource` do not exist.

- [ ] **Step 3: Write the implementation**

Add to `src/BeBoosted.Application/Settings/SettingKeys.cs`:

```csharp
    public const string CaptureModelSource = "ai.captureModel.source";
    public const string ClaudeModel = "ai.claude.model";
    public const string ClaudeApiKey = "ai.claude.apiKey";
    public const string OllamaEndpoint = "ai.ollama.endpoint";
    public const string OllamaModel = "ai.ollama.model";
```

Create `src/BeBoosted.Application/Ai/CaptureModelSettings.cs`:

```csharp
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~CaptureModelSettingsTests"`
Expected: PASS, 5 tests (the Theory counts as 3).

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Application/Settings/SettingKeys.cs src/BeBoosted.Application/Ai/CaptureModelSettings.cs tests/BeBoosted.Tests/Ai/CaptureModelSettingsTests.cs tests/BeBoosted.Tests/Support/InMemorySettingsStore.cs
git commit -m "feat: settings for the capture model source, endpoints, and key"
```

---

### Task 2: The capture port and the shared prompt

**Files:**
- Create: `src/BeBoosted.Application/Ai/ICaptureModel.cs`
- Create: `src/BeBoosted.Application/Ai/CaptureExtractionPrompt.cs`
- Test: `tests/BeBoosted.Tests/Ai/CaptureExtractionPromptTests.cs`

**Interfaces:**
- Produces: `record CaptureRequest(string Message, IReadOnlyList<string> ProjectNames, DateOnly Today)`; `record CaptureDraft(string Title, int? EstimatedMinutes, DateOnly? Deadline, string? ProjectName)`; `interface ICaptureModel` with `Task<IReadOnlyList<CaptureDraft>> ExtractAsync(CaptureRequest, CancellationToken)` and `Task<CaptureDraft?> SuggestMetadataAsync(CaptureRequest, CancellationToken)`; `static class CaptureExtractionPrompt` with `string System`, `string BuildUser(CaptureRequest)`, `string MetadataSystem`.

- [ ] **Step 1: Write the failing test**

```csharp
using BeBoosted.Application.Ai;

namespace BeBoosted.Tests.Ai;

public sealed class CaptureExtractionPromptTests
{
    private static CaptureRequest Request(string message) => new(
        message, ["Schoolwork", "College Admissions"], new DateOnly(2026, 9, 3));

    [Fact]
    public void TheUserPrompt_CarriesTheMessage_TheProjectNames_AndToday()
    {
        var prompt = CaptureExtractionPrompt.BuildUser(Request("Finish the essay"));

        Assert.Contains("Finish the essay", prompt, StringComparison.Ordinal);
        Assert.Contains("Schoolwork", prompt, StringComparison.Ordinal);
        Assert.Contains("College Admissions", prompt, StringComparison.Ordinal);
        Assert.Contains("2026-09-03", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The BB-QA-003 rule, stated as an instruction: an elaborating sentence is not a
    /// second task. If this drops out of the prompt the defect returns silently.
    /// </summary>
    [Fact]
    public void TheSystemPrompt_ForbidsTurningAnElaboratingSentenceIntoATask()
    {
        Assert.Contains("elaborat", CaptureExtractionPrompt.System, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", CaptureExtractionPrompt.System, StringComparison.Ordinal);
        Assert.Contains("\"tasks\"", CaptureExtractionPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSystemPrompt_ForbidsInventingFacts()
    {
        Assert.Contains("Do not invent", CaptureExtractionPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoProjects_TheUserPromptStillBuilds()
    {
        var prompt = CaptureExtractionPrompt.BuildUser(
            new CaptureRequest("Email Ms. Rivera", [], new DateOnly(2026, 9, 3)));

        Assert.Contains("Email Ms. Rivera", prompt, StringComparison.Ordinal);
        Assert.Contains("none", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~CaptureExtractionPromptTests"`
Expected: FAIL to build — `CaptureExtractionPrompt` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/BeBoosted.Application/Ai/ICaptureModel.cs`:

```csharp
namespace BeBoosted.Application.Ai;

/// <summary>
/// One capture, as a backend sees it. Deliberately not <see cref="AiContext"/>: that
/// carries a ProjectId, and a backend that never sees a domain identifier cannot leak
/// one into a prompt or invent one in a response. The router maps names to ids.
/// </summary>
public sealed record CaptureRequest(
    string Message, IReadOnlyList<string> ProjectNames, DateOnly Today);

/// <summary>A draft as the model returns it: a project is named, never identified.</summary>
public sealed record CaptureDraft(
    string Title, int? EstimatedMinutes, DateOnly? Deadline, string? ProjectName);

/// <summary>
/// The narrow capture port. Only the two operations a model actually serves in this
/// slice; project Q&amp;A is not here, so no backend is made to implement it.
/// A backend that cannot answer throws, and the router decides what that means.
/// </summary>
public interface ICaptureModel
{
    Task<IReadOnlyList<CaptureDraft>> ExtractAsync(
        CaptureRequest request, CancellationToken cancellationToken = default);

    /// <summary>Duration/deadline for one bare title; null when the model offers nothing.</summary>
    Task<CaptureDraft?> SuggestMetadataAsync(
        CaptureRequest request, CancellationToken cancellationToken = default);
}
```

Create `src/BeBoosted.Application/Ai/CaptureExtractionPrompt.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace BeBoosted.Application.Ai;

/// <summary>
/// The one extraction prompt, shared by every backend so their outputs are comparable
/// and one parser can read them both.
/// </summary>
public static class CaptureExtractionPrompt
{
    public const string System = """
        You turn a person's message into task drafts for a calendar planner.

        Reply with JSON only, in this exact shape:
        {"tasks": [{"title": "...", "estimated_minutes": 30, "deadline": "2026-09-04", "project": "..."}]}

        Rules:
        - A sentence that elaborates on the task before it is NOT a new task. "It probably
          needs two focused sessions" describes the previous task; fold that information
          into it rather than creating a task from the fragment.
        - Titles are short and imperative ("Finish DECA presentation"), never a copied
          sentence.
        - Do not invent deadlines, durations, or projects. Omit a field the message does
          not support.
        - "project" must be exactly one of the supplied project names, or omitted.
        - estimated_minutes is a whole number of minutes.
        - deadline is YYYY-MM-DD, resolved against the supplied today's date.
        - A message with no task in it returns {"tasks": []}.
        """;

    public const string MetadataSystem = """
        You estimate scheduling metadata for one task title.

        Reply with JSON only: {"tasks": [{"title": "...", "estimated_minutes": 30, "deadline": "2026-09-04"}]}

        Rules:
        - Return exactly one entry, echoing the title you were given.
        - Do not invent a deadline the title does not imply; omit it instead.
        - estimated_minutes is a whole number of minutes.
        - deadline is YYYY-MM-DD, resolved against the supplied today's date.
        """;

    public static string BuildUser(CaptureRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("Today is ")
            .Append(request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .AppendLine(".");
        builder.Append("Existing projects: ")
            .AppendLine(request.ProjectNames.Count == 0
                ? "none"
                : string.Join(", ", request.ProjectNames));
        builder.AppendLine().AppendLine("Message:").Append(request.Message);
        return builder.ToString();
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~CaptureExtractionPromptTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Application/Ai/ICaptureModel.cs src/BeBoosted.Application/Ai/CaptureExtractionPrompt.cs tests/BeBoosted.Tests/Ai/CaptureExtractionPromptTests.cs
git commit -m "feat: add the capture model port and its shared extraction prompt"
```

---

### Task 3: The shared response parser

**Files:**
- Create: `src/BeBoosted.Application/Ai/CaptureDraftParser.cs`
- Test: `tests/BeBoosted.Tests/Ai/CaptureDraftParserTests.cs`

**Interfaces:**
- Consumes: `CaptureDraft` (Task 2).
- Produces: `static class CaptureDraftParser` with `IReadOnlyList<CaptureDraft> Parse(string json)`. Throws `FormatException` when the payload is not the expected object shape; skips individual malformed entries.

**Why the bounds:** the parser is the only thing standing between a model's output and the app's data. Every field is clamped here so no backend has to be trusted.

- [ ] **Step 1: Write the failing test**

```csharp
using BeBoosted.Application.Ai;

namespace BeBoosted.Tests.Ai;

public sealed class CaptureDraftParserTests
{
    [Fact]
    public void ParsesAWellFormedResponse()
    {
        var drafts = CaptureDraftParser.Parse("""
            {"tasks": [
              {"title": "Finish DECA presentation", "estimated_minutes": 90,
               "deadline": "2026-09-04", "project": "Schoolwork"}
            ]}
            """);

        var draft = Assert.Single(drafts);
        Assert.Equal("Finish DECA presentation", draft.Title);
        Assert.Equal(90, draft.EstimatedMinutes);
        Assert.Equal(new DateOnly(2026, 9, 4), draft.Deadline);
        Assert.Equal("Schoolwork", draft.ProjectName);
    }

    [Fact]
    public void AnEmptyTaskArray_IsAValidAnswer()
        => Assert.Empty(CaptureDraftParser.Parse("""{"tasks": []}"""));

    [Fact]
    public void OmittedOptionalFields_BecomeNull()
    {
        var draft = Assert.Single(CaptureDraftParser.Parse("""{"tasks":[{"title":"Email Ms. Rivera"}]}"""));

        Assert.Null(draft.EstimatedMinutes);
        Assert.Null(draft.Deadline);
        Assert.Null(draft.ProjectName);
    }

    [Fact]
    public void JsonFencedInProse_IsStillRead_BecauseSmallModelsDoThat()
    {
        var drafts = CaptureDraftParser.Parse("""
            Here you go:
            ```json
            {"tasks": [{"title": "Draft the essay"}]}
            ```
            """);

        Assert.Equal("Draft the essay", Assert.Single(drafts).Title);
    }

    [Fact]
    public void AnEntryWithNoTitle_IsSkipped_WithoutFailingTheBatch()
    {
        var drafts = CaptureDraftParser.Parse("""
            {"tasks": [{"estimated_minutes": 30}, {"title": "Real task"}]}
            """);

        Assert.Equal("Real task", Assert.Single(drafts).Title);
    }

    [Fact]
    public void AnUnparseableDeadline_DropsTheDeadline_NotTheTask()
    {
        var draft = Assert.Single(CaptureDraftParser.Parse(
            """{"tasks":[{"title":"Task","deadline":"next Friday"}]}"""));

        Assert.Null(draft.Deadline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(100000)]
    public void AnOutOfRangeDuration_IsDropped(int minutes)
    {
        var draft = Assert.Single(CaptureDraftParser.Parse(
            $$"""{"tasks":[{"title":"Task","estimated_minutes":{{minutes}}}]}"""));

        Assert.Null(draft.EstimatedMinutes);
    }

    [Fact]
    public void AnAbsurdlyLongTitle_IsTruncatedRatherThanStored()
    {
        var long_ = new string('x', 5000);
        var draft = Assert.Single(CaptureDraftParser.Parse(
            $$"""{"tasks":[{"title":"{{long_}}"}]}"""));

        Assert.True(draft.Title.Length <= 200, "a model must not be able to write an unbounded title");
    }

    [Fact]
    public void AWhitespaceOnlyTitle_IsSkipped()
        => Assert.Empty(CaptureDraftParser.Parse("""{"tasks":[{"title":"   "}]}"""));

    [Fact]
    public void NonJson_Throws_SoTheRouterCanFallBack()
        => Assert.Throws<FormatException>(() => CaptureDraftParser.Parse("I'm not sure what you mean."));

    [Fact]
    public void AMissingTasksProperty_Throws()
        => Assert.Throws<FormatException>(() => CaptureDraftParser.Parse("""{"result": []}"""));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~CaptureDraftParserTests"`
Expected: FAIL to build — `CaptureDraftParser` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Globalization;
using System.Text.Json;

namespace BeBoosted.Application.Ai;

/// <summary>
/// Reads a model's JSON into <see cref="CaptureDraft"/> values, bounding every field.
/// This is the only thing between a model's output and the app's data, so nothing here
/// trusts the backend: a malformed entry is skipped, an out-of-range value is dropped,
/// and a payload that is not the agreed shape throws so the router can fall back.
/// </summary>
public static class CaptureDraftParser
{
    private const int MaxTitleLength = 200;
    private const int MinMinutes = 1;
    private const int MaxMinutes = 24 * 60;

    public static IReadOnlyList<CaptureDraft> Parse(string json)
    {
        using var document = ParseDocument(Unfence(json));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The model's reply had no \"tasks\" array.");
        }

        var drafts = new List<CaptureDraft>();
        foreach (var entry in tasks.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || ReadTitle(entry) is not { } title)
            {
                continue; // one bad entry never fails the batch
            }

            drafts.Add(new CaptureDraft(
                title, ReadMinutes(entry), ReadDeadline(entry), ReadProject(entry)));
        }

        return drafts;
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new FormatException("The model's reply was not JSON.", error);
        }
    }

    /// <summary>
    /// Small local models routinely wrap JSON in prose or a code fence even when asked
    /// not to. Taking the outermost braces costs nothing and saves those replies.
    /// </summary>
    private static string Unfence(string json)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        return start >= 0 && end > start ? json[start..(end + 1)] : json;
    }

    private static string? ReadTitle(JsonElement entry)
    {
        if (!entry.TryGetProperty("title", out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString()?.Trim() is not { Length: > 0 } title)
        {
            return null;
        }

        return title.Length > MaxTitleLength ? title[..MaxTitleLength].TrimEnd() : title;
    }

    private static int? ReadMinutes(JsonElement entry)
    {
        if (!entry.TryGetProperty("estimated_minutes", out var value))
        {
            return null;
        }

        var minutes = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (int?)null,
        };

        return minutes is >= MinMinutes and <= MaxMinutes ? minutes : null;
    }

    private static DateOnly? ReadDeadline(JsonElement entry)
        => entry.TryGetProperty("deadline", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParse(
                value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var deadline)
            ? deadline
            : null;

    private static string? ReadProject(JsonElement entry)
        => entry.TryGetProperty("project", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString()?.Trim() is { Length: > 0 } name
            ? name
            : null;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~CaptureDraftParserTests"`
Expected: PASS, 13 tests (the Theory counts as 3).

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Application/Ai/CaptureDraftParser.cs tests/BeBoosted.Tests/Ai/CaptureDraftParserTests.cs
git commit -m "feat: parse and bound a model's capture response"
```

---

### Task 4: The secret protector

**Files:**
- Create: `src/BeBoosted.Application/Abstractions/ISecretProtector.cs`
- Create: `src/BeBoosted.Infrastructure/Security/DpapiSecretProtector.cs`
- Create: `src/BeBoosted.Infrastructure/Security/UnavailableSecretProtector.cs`
- Modify: `Directory.Packages.props`, `src/BeBoosted.Infrastructure/BeBoosted.Infrastructure.csproj`
- Test: `tests/BeBoosted.Tests/Ai/SecretProtectorTests.cs`

**Interfaces:**
- Produces: `interface ISecretProtector { bool IsAvailable { get; } string Protect(string plaintext); string? TryUnprotect(string ciphertext); }`; `DpapiSecretProtector` (Windows, `DataProtectionScope.CurrentUser`); `UnavailableSecretProtector` (everything else).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Runtime.InteropServices;
using BeBoosted.Application.Abstractions;
using BeBoosted.Infrastructure.Security;

namespace BeBoosted.Tests.Ai;

public sealed class SecretProtectorTests
{
    [Fact]
    public void TheUnavailableProtector_SaysSo_RatherThanThrowing()
    {
        ISecretProtector protector = new UnavailableSecretProtector();

        Assert.False(protector.IsAvailable);
        Assert.Null(protector.TryUnprotect("anything"));
    }

    [Fact]
    public void OnWindows_ASecretRoundTrips_AndItsCiphertextIsNotThePlaintext()
    {
        Assert.SkipUnless(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only");
        ISecretProtector protector = new DpapiSecretProtector();
        Assert.True(protector.IsAvailable);

        var cipher = protector.Protect("sk-ant-secret-value");

        Assert.DoesNotContain("sk-ant", cipher, StringComparison.Ordinal);
        Assert.Equal("sk-ant-secret-value", protector.TryUnprotect(cipher));
    }

    /// <summary>The copied-profile case: undecryptable input is null, never a throw.</summary>
    [Fact]
    public void OnWindows_GarbageCiphertext_ReturnsNull()
    {
        Assert.SkipUnless(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only");

        Assert.Null(new DpapiSecretProtector().TryUnprotect("bm90LWEtcmVhbC1ibG9i"));
    }

    [Fact]
    public void OnWindows_NonBase64Ciphertext_ReturnsNull()
    {
        Assert.SkipUnless(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "DPAPI is Windows-only");

        Assert.Null(new DpapiSecretProtector().TryUnprotect("!!! not base64 !!!"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~SecretProtectorTests"`
Expected: FAIL to build — the protector types do not exist.

- [ ] **Step 3: Write the implementation**

In `Directory.Packages.props`, inside the existing `<ItemGroup>`, after the Persistence entry:

```xml
    <!-- AI -->
    <PackageVersion Include="Anthropic" Version="12.45.0" />
    <PackageVersion Include="System.Security.Cryptography.ProtectedData" Version="10.0.11" />
```

In `src/BeBoosted.Infrastructure/BeBoosted.Infrastructure.csproj`, add to the `PackageReference` group:

```xml
    <PackageReference Include="System.Security.Cryptography.ProtectedData" />
```

Create `src/BeBoosted.Application/Abstractions/ISecretProtector.cs`:

```csharp
namespace BeBoosted.Application.Abstractions;

/// <summary>
/// Encrypts a secret for storage inside the app's own profile. Deliberately not an OS
/// credential vault: the whole profile relocates under BEBOOSTED_DATA_DIR, which is how
/// every test run and disposable-profile session stays isolated from the real library,
/// and a vault entry would live outside that boundary.
/// </summary>
public interface ISecretProtector
{
    /// <summary>False where no implementation exists (non-Windows, until Keychain ships).</summary>
    bool IsAvailable { get; }

    string Protect(string plaintext);

    /// <summary>The secret, or null when it cannot be decrypted — a profile copied from
    /// another machine or user. That is an expected state, not an error.</summary>
    string? TryUnprotect(string ciphertext);
}
```

Create `src/BeBoosted.Infrastructure/Security/DpapiSecretProtector.cs`:

```csharp
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BeBoosted.Application.Abstractions;

namespace BeBoosted.Infrastructure.Security;

/// <summary>Windows DPAPI at user scope: the ciphertext is useless to another account.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public bool IsAvailable => true;

    public string Protect(string plaintext)
        => Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.CurrentUser));

    public string? TryUnprotect(string ciphertext)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(ciphertext), optionalEntropy: null, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
```

Create `src/BeBoosted.Infrastructure/Security/UnavailableSecretProtector.cs`:

```csharp
using BeBoosted.Application.Abstractions;

namespace BeBoosted.Infrastructure.Security;

/// <summary>
/// The protector on a platform that has none yet. It reports its own absence instead of
/// throwing, so Settings can disable the cloud option with an explanation and the rest of
/// the app — heuristic and Ollama alike — carries on untouched.
/// </summary>
public sealed class UnavailableSecretProtector : ISecretProtector
{
    public bool IsAvailable => false;

    public string Protect(string plaintext)
        => throw new PlatformNotSupportedException(
            "Saving an API key isn't supported on this platform yet.");

    public string? TryUnprotect(string ciphertext) => null;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~SecretProtectorTests"`
Expected: PASS, 4 tests on Windows (3 skip elsewhere).

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/BeBoosted.Infrastructure/BeBoosted.Infrastructure.csproj src/BeBoosted.Application/Abstractions/ISecretProtector.cs src/BeBoosted.Infrastructure/Security tests/BeBoosted.Tests/Ai/SecretProtectorTests.cs
git commit -m "feat: protect the stored API key inside the profile"
```

---

### Task 5: The Ollama backend

**Files:**
- Create: `src/BeBoosted.Infrastructure/Ai/OllamaCaptureModel.cs`
- Test: `tests/BeBoosted.Tests/Ai/OllamaCaptureModelTests.cs`

**Interfaces:**
- Consumes: `ICaptureModel`, `CaptureRequest`, `CaptureDraft`, `CaptureExtractionPrompt`, `CaptureDraftParser`, `CaptureModelSettings`.
- Produces: `OllamaCaptureModel(HttpClient client, CaptureModelSettings settings) : ICaptureModel`.

**Note on the API:** Ollama's `POST /api/generate` takes `{model, system, prompt, format: "json", stream: false}` and replies `{"response": "<the model's text>", ...}`. The model's JSON is a *string inside* `response`, which is what gets handed to the parser.

- [ ] **Step 1: Write the failing test**

```csharp
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

        var drafts = await model.ExtractAsync(Request());

        Assert.Equal("Finish DECA presentation", Assert.Single(drafts).Title);
    }

    [Fact]
    public async Task PostsTheConfiguredModelAndJsonFormat()
    {
        var (model, handler) = Create(_ => Json("""{"response": "{\"tasks\":[]}"}"""));

        await model.ExtractAsync(Request());

        Assert.NotNull(handler.LastBody);
        Assert.Contains("qwen2.5:7b-instruct", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"json\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectionRefusal_Throws_SoTheRouterFallsBack()
    {
        var (model, _) = Create(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => model.ExtractAsync(Request()));
    }

    [Fact]
    public async Task AnErrorStatus_Throws()
    {
        var (model, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"model not found"}"""),
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => model.ExtractAsync(Request()));
    }

    [Fact]
    public async Task ANonJsonModelReply_ThrowsFormatException()
    {
        var (model, _) = Create(_ => Json("""{"response": "I'm not sure what you mean."}"""));

        await Assert.ThrowsAsync<FormatException>(() => model.ExtractAsync(Request()));
    }

    [Fact]
    public async Task SuggestMetadata_ReturnsTheSingleDraft()
    {
        var (model, _) = Create(_ => Json(
            """{"response": "{\"tasks\":[{\"title\":\"Email Ms. Rivera\",\"estimated_minutes\":10}]}"}"""));

        var draft = await model.SuggestMetadataAsync(
            new CaptureRequest("Email Ms. Rivera", [], new DateOnly(2026, 9, 3)));

        Assert.Equal(10, draft?.EstimatedMinutes);
    }

    [Fact]
    public async Task SuggestMetadata_WithNoEntries_ReturnsNull()
    {
        var (model, _) = Create(_ => Json("""{"response": "{\"tasks\":[]}"}"""));

        Assert.Null(await model.SuggestMetadataAsync(
            new CaptureRequest("Email Ms. Rivera", [], new DateOnly(2026, 9, 3))));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~OllamaCaptureModelTests"`
Expected: FAIL to build — `OllamaCaptureModel` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
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
    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("stream")] bool Stream);

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
            Format: "json", Stream: false);

        using var response = await client.PostAsJsonAsync(endpoint, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken);

        // The model's JSON is a string inside the envelope, not the envelope itself.
        return envelope?.Response
            ?? throw new FormatException("Ollama returned no response text.");
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~OllamaCaptureModelTests"`
Expected: PASS, 7 tests.

If `PostAsJsonAsync`'s serializer emits different casing than the assertions expect, the fix is in the test's expected strings only after confirming the real wire body — do not loosen the assertion to `Contains("qwen")`.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Infrastructure/Ai/OllamaCaptureModel.cs tests/BeBoosted.Tests/Ai/OllamaCaptureModelTests.cs
git commit -m "feat: extract capture drafts with a local Ollama model"
```

---

### Task 6: The Claude backend

**Files:**
- Create: `src/BeBoosted.Infrastructure/Ai/ClaudeCaptureModel.cs`
- Modify: `src/BeBoosted.Infrastructure/BeBoosted.Infrastructure.csproj`
- Test: `tests/BeBoosted.Tests/Ai/ClaudeCaptureModelTests.cs`

**Interfaces:**
- Consumes: same Application pieces as Task 5, plus `ISecretProtector` and `CaptureModelSettings`.
- Produces: `ClaudeCaptureModel(CaptureModelSettings settings, ISecretProtector protector, HttpMessageHandler? handler = null) : ICaptureModel`. Throws `InvalidOperationException` when no usable key is available.

**Note on the SDK:** the official package is `Anthropic` (Anthropic-owned; *not* the community `Anthropic.SDK`). `AnthropicClient` accepts an API key and client options; the request type is `Anthropic.Models.Messages.MessageCreateParams` with `Model`, `MaxTokens`, `System`, and `Messages`. The `handler` parameter exists so tests can supply a stub transport — **no test may make a live call**. If the SDK's option for a custom transport differs from what this task shows, discover the exact member with `strings ~/.nuget/packages/anthropic/12.45.0/lib/*/Anthropic.dll | grep -i httpclient` and adjust the constructor only; the tests below stay as written.

- [ ] **Step 1: Write the failing test**

```csharp
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ExtractAsync(Request()));
    }

    /// <summary>The copied-profile case reaches the same fallback as a missing key.</summary>
    [Fact]
    public async Task WithAnUndecryptableKey_Throws()
    {
        var model = new ClaudeCaptureModel(
            SettingsWithKey("not-encrypted-by-this-user"), new ReversibleProtector());

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ExtractAsync(Request()));
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

        var drafts = await model.ExtractAsync(Request());

        Assert.Equal("Finish DECA presentation", Assert.Single(drafts).Title);
    }

    [Fact]
    public async Task AnAuthFailure_Throws_SoTheRouterFallsBack()
    {
        var model = new ClaudeCaptureModel(
            SettingsWithKey("enc:sk-ant-bad"), new ReversibleProtector(),
            new StubHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key"}}"""));

        await Assert.ThrowsAnyAsync<Exception>(() => model.ExtractAsync(Request()));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~ClaudeCaptureModelTests"`
Expected: FAIL to build — `ClaudeCaptureModel` does not exist.

- [ ] **Step 3: Write the implementation**

Add to `src/BeBoosted.Infrastructure/BeBoosted.Infrastructure.csproj`:

```xml
    <PackageReference Include="Anthropic" />
```

Create `src/BeBoosted.Infrastructure/Ai/ClaudeCaptureModel.cs`:

```csharp
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

        var options = new AnthropicClientOptions { ApiKey = key };
        if (handler is not null)
        {
            options.HttpClient = new HttpClient(handler);
        }

        var client = new AnthropicClient(options);
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
            if (block is TextBlock { Text: { Length: > 0 } value })
            {
                text.Append(value);
            }
        }

        return text.Length > 0
            ? text.ToString()
            : throw new FormatException("Claude returned no text content.");
    }
}
```

**If the SDK's names differ** (`AnthropicClientOptions`, `.HttpClient`, `Messages.Create`, `TextBlock.Text`, `message.Content`), let the compiler tell you: `error CS1061` names the wrong member directly. Fix the implementation to match the SDK; do not change the tests, which assert behavior rather than SDK shape.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~ClaudeCaptureModelTests"`
Expected: PASS, 4 tests, with no network access.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Infrastructure/BeBoosted.Infrastructure.csproj src/BeBoosted.Infrastructure/Ai/ClaudeCaptureModel.cs tests/BeBoosted.Tests/Ai/ClaudeCaptureModelTests.cs
git commit -m "feat: extract capture drafts with Claude"
```

---

### Task 7: The extraction result carries a notice

**Files:**
- Modify: `src/BeBoosted.Application/Ai/IAiProvider.cs`, `src/BeBoosted.Application/Ai/AiService.cs`, `src/BeBoosted.Infrastructure/Ai/LocalHeuristicAiProvider.cs`
- Test: `tests/BeBoosted.Tests/Ai/AiServiceTests.cs` (extend)

**Interfaces:**
- Produces: `record CaptureExtractionResult(IReadOnlyList<ExtractedTaskDraft> Drafts, string? DegradedNotice)`; `IAiProvider.ExtractTasksAsync` now returns `Task<CaptureExtractionResult>`; `TaskExtractionOutcome` gains a `string? DegradedNotice` member.

**Why:** the notice has to survive from the router to the chat. This task changes the shape and keeps every existing caller green; nothing produces a non-null notice yet.

- [ ] **Step 1: Write the failing test**

Append to `tests/BeBoosted.Tests/Ai/AiServiceTests.cs`:

```csharp
    [Fact]
    public async Task AHealthyExtraction_CarriesNoDegradedNotice()
    {
        var projects = new InMemoryProjectRepository();
        var provider = new LocalHeuristicAiProvider(_resources, projects);
        var service = new AiService(
            provider, new InMemoryAiProvenanceRepository(), _tasks,
            new AiPermissionSettings(new InMemorySettingsStore()), _clock);

        var outcome = await service.ExtractTasksAsync(
            "Draft the essay outline", new AiContext(null, _clock.Today));

        Assert.NotEmpty(outcome.Drafts);
        Assert.Null(outcome.DegradedNotice);
    }
```

**Note:** match the surrounding test class's existing field names (`_resources`, `_tasks`, `_clock`) — read the top of the file first and adapt if they differ.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~AHealthyExtraction_CarriesNoDegradedNotice"`
Expected: FAIL to build — `TaskExtractionOutcome` has no `DegradedNotice`.

- [ ] **Step 3: Write the implementation**

In `src/BeBoosted.Application/Ai/IAiProvider.cs`, add the result record and change the signature:

```csharp
/// <summary>
/// Task drafts plus, when the configured model could not be reached, the plain sentence
/// the chat shows about it. A null notice means the configured parser did the work.
/// </summary>
public sealed record CaptureExtractionResult(
    IReadOnlyList<ExtractedTaskDraft> Drafts, string? DegradedNotice = null);
```

```csharp
    /// <summary>Proposes tasks from a natural-language message.</summary>
    Task<CaptureExtractionResult> ExtractTasksAsync(
        string message, AiContext context, CancellationToken cancellationToken = default);
```

In `LocalHeuristicAiProvider.ExtractTasksAsync`, change the return to wrap the list:

```csharp
        return Task.FromResult(new CaptureExtractionResult(drafts));
```

(and its declared return type to `Task<CaptureExtractionResult>`).

In `AiService`, add the member to the outcome record and thread the notice:

```csharp
public sealed record TaskExtractionOutcome(
    IReadOnlyList<ExtractedTaskDraft> Drafts,
    bool AddedAutomatically,
    IReadOnlyList<TaskItem> AddedTasks,
    string? DegradedNotice = null);
```

```csharp
    public async Task<TaskExtractionOutcome> ExtractTasksAsync(
        string message, AiContext context, CancellationToken cancellationToken = default)
    {
        var result = await provider.ExtractTasksAsync(message, context, cancellationToken);
        if (result.Drafts.Count == 0
            || permissions.TaskCapture == TaskCapturePermission.ReviewBeforeAdding)
        {
            return new TaskExtractionOutcome(
                result.Drafts, AddedAutomatically: false, [], result.DegradedNotice);
        }

        // Auto-add is allowed, but every task keeps its AI origin and provenance.
        var added = AcceptDrafts(result.Drafts);
        return new TaskExtractionOutcome(
            result.Drafts, AddedAutomatically: true, added, result.DegradedNotice);
    }
```

Then fix every compile error the signature change surfaces (`ChatViewModel` reads `extraction.Drafts`, which still works; any test double implementing `IAiProvider` needs the new return type).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test BeBoosted.slnx`
Expected: PASS, whole suite green — this task must not change any existing behavior.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Application/Ai src/BeBoosted.Infrastructure/Ai/LocalHeuristicAiProvider.cs tests/BeBoosted.Tests/Ai/AiServiceTests.cs
git commit -m "refactor: let an extraction carry a degraded-parse notice"
```

---

### Task 8: The routing provider

**Files:**
- Create: `src/BeBoosted.Infrastructure/Ai/RoutedAiProvider.cs`
- Test: `tests/BeBoosted.Tests/Ai/RoutedAiProviderTests.cs`

**Interfaces:**
- Consumes: `IAiProvider` (the heuristic), `ICaptureModel` (both backends), `CaptureModelSettings`, `IProjectRepository`.
- Produces: `RoutedAiProvider(IAiProvider heuristic, Func<CaptureModelSource, ICaptureModel?> backends, CaptureModelSettings settings, IProjectRepository projects) : IAiProvider`.

**This is the heart of the feature.** Every row of the spec's degradation table is a test here.

- [ ] **Step 1: Write the failing test**

```csharp
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Projects;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Settings;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Ai;

/// <summary>
/// The router over real repositories, the way every other test in this project works —
/// this project has no in-memory doubles, and the routing logic is what is under test,
/// not the storage beneath it.
/// </summary>
public sealed class RoutedAiProviderTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public RoutedAiProviderTests()
        => new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());

    public void Dispose() => _database.Dispose();

    private sealed class StubCaptureModel(
        Func<CaptureRequest, IReadOnlyList<CaptureDraft>> extract) : ICaptureModel
    {
        public int Calls { get; private set; }

        public CaptureRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<CaptureDraft>> ExtractAsync(
            CaptureRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(extract(request));
        }

        public Task<CaptureDraft?> SuggestMetadataAsync(
            CaptureRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var drafts = extract(request);
            return Task.FromResult(drafts.Count > 0 ? drafts[0] : null);
        }
    }

    private (RoutedAiProvider Provider, CaptureModelSettings Settings, SqliteProjectRepository Projects)
        Create(ICaptureModel? claude = null, ICaptureModel? ollama = null)
    {
        var projects = new SqliteProjectRepository(_database.Factory);
        var resources = new SqliteResourceRepository(_database.Factory);
        var heuristic = new LocalHeuristicAiProvider(resources, projects);
        var settings = new CaptureModelSettings(new SqliteSettingsStore(_database.Factory));
        var provider = new RoutedAiProvider(
            heuristic,
            source => source switch
            {
                CaptureModelSource.Claude => claude,
                CaptureModelSource.Ollama => ollama,
                _ => null,
            },
            settings, projects);
        return (provider, settings, projects);
    }

    private static AiContext Context() => new(null, new DateOnly(2026, 9, 3));

    [Fact]
    public async Task WithTheHeuristicSelected_NoBackendIsCalled()
    {
        var claude = new StubCaptureModel(_ => [new CaptureDraft("Model draft", null, null, null)]);
        var (provider, _, _) = Create(claude: claude);

        var result = await provider.ExtractTasksAsync("Draft the essay outline", Context());

        Assert.Equal(0, claude.Calls);
        Assert.Null(result.DegradedNotice);
        Assert.NotEmpty(result.Drafts);
    }

    [Fact]
    public async Task WithClaudeSelected_ItsDraftsAreUsed()
    {
        var claude = new StubCaptureModel(_ => [new CaptureDraft("Finish DECA presentation", 90, null, null)]);
        var (provider, settings, _) = Create(claude: claude);
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync("finish deca thing", Context());

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("Finish DECA presentation", draft.Title);
        Assert.Equal(TimeSpan.FromMinutes(90), draft.EstimatedDuration);
        Assert.Null(result.DegradedNotice);
    }

    [Fact]
    public async Task AFailingBackend_FallsBackToTheHeuristic_AndSaysSo()
    {
        var claude = new StubCaptureModel(_ => throw new HttpRequestException("offline"));
        var (provider, settings, _) = Create(claude: claude);
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync("Draft the essay outline", Context());

        Assert.NotEmpty(result.Drafts); // the capture is not lost
        Assert.NotNull(result.DegradedNotice);
        Assert.Contains("Claude", result.DegradedNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNoticeNamesOllama_WhenOllamaIsTheConfiguredSource()
    {
        var ollama = new StubCaptureModel(_ => throw new FormatException("not json"));
        var (provider, settings, _) = Create(ollama: ollama);
        settings.Source = CaptureModelSource.Ollama;

        var result = await provider.ExtractTasksAsync("Draft the essay outline", Context());

        Assert.Contains("Ollama", result.DegradedNotice!, StringComparison.Ordinal);
    }

    /// <summary>The notice is chat copy, not a log line.</summary>
    [Fact]
    public async Task TheNoticeNeverLeaksTheRawExceptionMessage()
    {
        var ollama = new StubCaptureModel(
            _ => throw new HttpRequestException("SocketException: ECONNREFUSED 127.0.0.1:11434"));
        var (provider, settings, _) = Create(ollama: ollama);
        settings.Source = CaptureModelSource.Ollama;

        var result = await provider.ExtractTasksAsync("Draft the essay outline", Context());

        Assert.DoesNotContain("ECONNREFUSED", result.DegradedNotice!, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", result.DegradedNotice!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfiguredSourceWithNoRegisteredBackend_FallsBack()
    {
        var (provider, settings, _) = Create(); // no backends supplied
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync("Draft the essay outline", Context());

        Assert.NotNull(result.DegradedNotice);
    }

    [Fact]
    public async Task ProjectNamesGoOutAndAProjectIdComesBack()
    {
        var backend = new StubCaptureModel(
            _ => [new CaptureDraft("Finish DECA presentation", null, null, "Schoolwork")]);
        var (provider, settings, projects) = Create(claude: backend);
        var schoolwork = Project.Create("Schoolwork", "#5B8DEF", DateTimeOffset.UtcNow);
        projects.Add(schoolwork);
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync("finish deca", Context());

        // Out as a name, back as an id: the backend never handles domain identity.
        Assert.Contains("Schoolwork", backend.LastRequest!.ProjectNames);
        Assert.Equal(schoolwork.Id, Assert.Single(result.Drafts).ProjectId);
    }

    [Fact]
    public async Task AnUnknownProjectName_BecomesNoProject_RatherThanAGuess()
    {
        var backend = new StubCaptureModel(
            _ => [new CaptureDraft("Task", null, null, "Nonexistent Project")]);
        var (provider, settings, _) = Create(claude: backend);
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync("something", Context());

        Assert.Null(Assert.Single(result.Drafts).ProjectId);
    }

    /// <summary>Metadata fills an optional hint; a notice for it would be noise.</summary>
    [Fact]
    public async Task SuggestMetadata_DegradesSilently()
    {
        var claude = new StubCaptureModel(_ => throw new HttpRequestException("offline"));
        var (provider, settings, _) = Create(claude: claude);
        settings.Source = CaptureModelSource.Claude;

        var suggestion = await provider.SuggestMetadataAsync("Email Ms. Rivera", Context());

        Assert.NotNull(suggestion); // the heuristic answered
    }

    [Fact]
    public async Task ProjectQuestions_AlwaysReachTheHeuristic()
    {
        var claude = new StubCaptureModel(_ => throw new InvalidOperationException("must not be called"));
        var (provider, settings, projects) = Create(claude: claude);
        settings.Source = CaptureModelSource.Claude;
        var project = Project.Create("Schoolwork", "#5B8DEF", DateTimeOffset.UtcNow);
        projects.Add(project);

        var answer = await provider.AnswerQuestionAsync(project.Id, "What did I write down?");

        Assert.NotNull(answer);
        Assert.Equal(0, claude.Calls);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~RoutedAiProviderTests"`
Expected: FAIL to build — `RoutedAiProvider` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using BeBoosted.Application.Ai;
using BeBoosted.Application.Projects;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Infrastructure.Ai;

/// <summary>
/// The registered <see cref="IAiProvider"/>. It reads the configured source per call, so
/// changing it in Settings takes effect on the next capture with no restart, and it is
/// the single place fallback lives: every failure a backend can have — no key, offline,
/// timeout, refused, unparseable — is the same event to the user, so all of them land
/// here and produce one plain sentence.
///
/// Project Q&amp;A is delegated to the heuristic unconditionally: answering well needs
/// resource content that is not indexed yet, and a confident wrong answer over a
/// title-only index would be worse than the keyword answer it replaces.
/// </summary>
public sealed class RoutedAiProvider(
    IAiProvider heuristic,
    Func<CaptureModelSource, ICaptureModel?> backends,
    CaptureModelSettings settings,
    IProjectRepository projects) : IAiProvider
{
    public async Task<CaptureExtractionResult> ExtractTasksAsync(
        string message, AiContext context, CancellationToken cancellationToken = default)
    {
        var source = settings.Source;
        if (source == CaptureModelSource.Heuristic || backends(source) is not { } model)
        {
            return source == CaptureModelSource.Heuristic
                ? await heuristic.ExtractTasksAsync(message, context, cancellationToken)
                : await DegradeAsync(source, message, context, cancellationToken);
        }

        var known = projects.GetAll();
        try
        {
            var drafts = await model.ExtractAsync(
                new CaptureRequest(message, [.. known.Select(p => p.Name)], context.Today),
                cancellationToken);
            return new CaptureExtractionResult([.. drafts.Select(draft => ToDraft(draft, known, context))]);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return await DegradeAsync(source, message, context, cancellationToken);
        }
    }

    public async Task<TaskMetadataSuggestion> SuggestMetadataAsync(
        string title, AiContext context, CancellationToken cancellationToken = default)
    {
        var source = settings.Source;
        if (source != CaptureModelSource.Heuristic && backends(source) is { } model)
        {
            try
            {
                var draft = await model.SuggestMetadataAsync(
                    new CaptureRequest(title, [], context.Today), cancellationToken);
                if (draft is not null)
                {
                    return new TaskMetadataSuggestion(
                        draft.EstimatedMinutes is { } minutes
                            ? TimeSpan.FromMinutes(minutes)
                            : null,
                        draft.Deadline);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Silent by design: this fills an optional hint field, and a notice
                // about a missing duration estimate would be noise.
            }
        }

        return await heuristic.SuggestMetadataAsync(title, context, cancellationToken);
    }

    public Task<ProjectAnswerResult> AnswerQuestionAsync(
        ProjectId projectId, string question, CancellationToken cancellationToken = default)
        => heuristic.AnswerQuestionAsync(projectId, question, cancellationToken);

    private async Task<CaptureExtractionResult> DegradeAsync(
        CaptureModelSource source, string message, AiContext context,
        CancellationToken cancellationToken)
    {
        var fallback = await heuristic.ExtractTasksAsync(message, context, cancellationToken);
        return fallback with { DegradedNotice = NoticeFor(source) };
    }

    /// <summary>Chat copy, never a log line: it names the source and nothing else.</summary>
    private static string NoticeFor(CaptureModelSource source) => source switch
    {
        CaptureModelSource.Claude => "Parsed locally — Claude couldn't be reached.",
        CaptureModelSource.Ollama => "Parsed locally — Ollama couldn't be reached.",
        _ => "Parsed locally.",
    };

    private static ExtractedTaskDraft ToDraft(
        CaptureDraft draft, IReadOnlyList<Project> known, AiContext context)
    {
        // A name the model invented resolves to no project rather than a guess.
        var project = draft.ProjectName is { } name
            ? known.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;

        return new ExtractedTaskDraft(
            draft.Title,
            draft.EstimatedMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            draft.Deadline,
            project?.Id ?? context.ActiveProjectId,
            "from your message");
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Tests --filter "FullyQualifiedName~RoutedAiProviderTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Infrastructure/Ai/RoutedAiProvider.cs tests/BeBoosted.Tests/Ai/RoutedAiProviderTests.cs
git commit -m "feat: route capture to the configured model and fall back honestly"
```

---

### Task 9: Register the graph

**Files:**
- Modify: `src/BeBoosted.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/BeBoosted.Desktop.Tests/Composition/CaptureModelCompositionTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–8.
- Produces: `IAiProvider` resolving to `RoutedAiProvider` through the real container.

**Why a composition test:** the resource-groups work found a registration bug that every unit test missed, because an optional constructor parameter silently defaulted to null. Prove the real graph.

- [ ] **Step 1: Write the failing test**

```csharp
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Infrastructure;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BeBoosted.Desktop.Tests.Composition;

/// <summary>
/// The production container, validated. A registration that only unit tests cover can be
/// missing here and nothing would notice until the app ran.
/// </summary>
public sealed class CaptureModelCompositionTests
{
    private sealed class TemporaryPaths : IAppDataPaths, IDisposable
    {
        public TemporaryPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"beboosted-capture-di-{Guid.NewGuid():N}");
            LogsDirectory = Path.Combine(DataDirectory, "logs");
            ResourcesDirectory = Path.Combine(DataDirectory, "resources");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(ResourcesDirectory);
        }

        public string DataDirectory { get; }

        public string LogsDirectory { get; }

        public string ResourcesDirectory { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static ServiceProvider Build(TemporaryPaths paths)
        => new ServiceCollection()
            .AddBeBoostedInfrastructure(paths)
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

    [Fact]
    public void TheRegisteredProvider_IsTheRouter()
    {
        using var paths = new TemporaryPaths();
        using var services = Build(paths);

        Assert.IsType<RoutedAiProvider>(services.GetRequiredService<IAiProvider>());
    }

    [Fact]
    public void BothBackendsAndTheSettingsAccessorResolve()
    {
        using var paths = new TemporaryPaths();
        using var services = Build(paths);

        Assert.NotNull(services.GetRequiredService<CaptureModelSettings>());
        Assert.NotNull(services.GetRequiredService<OllamaCaptureModel>());
        Assert.NotNull(services.GetRequiredService<ClaudeCaptureModel>());
        Assert.NotNull(services.GetRequiredService<ISecretProtector>());
    }

    /// <summary>
    /// A fresh profile must behave exactly as it does today: the heuristic parses, and
    /// no capture touches a network.
    /// </summary>
    [Fact]
    public async Task AFreshProfile_ParsesWithTheHeuristic()
    {
        using var paths = new TemporaryPaths();
        using var services = Build(paths);
        var provider = services.GetRequiredService<IAiProvider>();

        var result = await provider.ExtractTasksAsync(
            "Draft the essay outline", new AiContext(null, new DateOnly(2026, 9, 3)));

        Assert.NotEmpty(result.Drafts);
        Assert.Null(result.DegradedNotice);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Desktop.Tests --filter "FullyQualifiedName~CaptureModelCompositionTests"`
Expected: FAIL — `IAiProvider` resolves to `LocalHeuristicAiProvider`, and the new types are not registered.

- [ ] **Step 3: Write the implementation**

In `ServiceCollectionExtensions.cs`, replace the single `IAiProvider` line (currently line 53) with:

```csharp
        services.AddSingleton<CaptureModelSettings>();
        services.AddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new UnavailableSecretProtector());
        services.AddSingleton<LocalHeuristicAiProvider>();
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<OllamaCaptureModel>();
        services.AddSingleton<ClaudeCaptureModel>();
        services.AddSingleton<IAiProvider>(sp => new RoutedAiProvider(
            sp.GetRequiredService<LocalHeuristicAiProvider>(),
            source => source switch
            {
                CaptureModelSource.Ollama => sp.GetRequiredService<OllamaCaptureModel>(),
                CaptureModelSource.Claude => sp.GetRequiredService<ClaudeCaptureModel>(),
                _ => null,
            },
            sp.GetRequiredService<CaptureModelSettings>(),
            sp.GetRequiredService<IProjectRepository>()));
```

Add `using BeBoosted.Infrastructure.Security;` to the file's usings.

**Note:** `ClaudeCaptureModel`'s third constructor parameter is optional (`HttpMessageHandler? handler = null`), which the container satisfies by omission — the same shape that hid a bug during the resource-groups work, which is exactly why the composition test above asserts behavior and not just resolution.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test BeBoosted.slnx`
Expected: PASS, whole suite green.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Infrastructure/ServiceCollectionExtensions.cs tests/BeBoosted.Desktop.Tests/Composition/CaptureModelCompositionTests.cs
git commit -m "feat: register the routed provider and both capture backends"
```

---

### Task 10: The chat shows the degraded notice

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/ChatViewModel.cs:240-258`
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/ChatDegradedNoticeTests.cs`

**Interfaces:**
- Consumes: `TaskExtractionOutcome.DegradedNotice` (Task 7).
- Produces: no new types — a `ChatAssistantMessageViewModel` carrying the notice, added before the review item.

- [ ] **Step 1: Write the failing test**

```csharp
using BeBoosted.Application.Ai;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// A capture that fell back to the heuristic says so in the reply. Without this the
/// downgrade is invisible and the user cannot tell a model answer from a rule answer.
/// </summary>
public sealed class ChatDegradedNoticeTests
{
    private sealed class DegradingProvider(string? notice) : IAiProvider
    {
        public Task<CaptureExtractionResult> ExtractTasksAsync(
            string message, AiContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new CaptureExtractionResult(
                [new ExtractedTaskDraft("Finish DECA presentation", null, null, null, "from your message")],
                notice));

        public Task<TaskMetadataSuggestion> SuggestMetadataAsync(
            string title, AiContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskMetadataSuggestion(null, null));

        public Task<ProjectAnswerResult> AnswerQuestionAsync(
            ProjectId projectId, string question, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProjectAnswerResult("no answer", []));
    }

    /// <summary>A List, not IReadOnlyList: the ordering assertion needs IndexOf.</summary>
    private static async Task<List<ChatItemViewModel>> SendAsync(string? notice)
    {
        var shell = TestShell.Create(aiProvider: new DegradingProvider(notice));
        var chat = shell.Chat;
        chat.InputText = "Finish my DECA presentation before Friday.";
        await chat.SubmitCommand.ExecuteAsync(null);
        return [.. chat.Items];
    }

    [Fact]
    public async Task ADegradedCapture_RendersItsNoticeBeforeTheReviewList()
    {
        var items = await SendAsync("Parsed locally — Claude couldn't be reached.");

        var notice = items.OfType<ChatAssistantMessageViewModel>()
            .FirstOrDefault(item => item.Text.Contains("Parsed locally", StringComparison.Ordinal));
        Assert.NotNull(notice);
        Assert.Contains(items, item => item is ChatReviewItemViewModel);
        Assert.True(
            items.IndexOf(notice) < items.IndexOf(items.First(i => i is ChatReviewItemViewModel)),
            "the notice explains the drafts, so it comes before them");
    }

    [Fact]
    public async Task AHealthyCapture_RendersNoNotice()
    {
        var items = await SendAsync(notice: null);

        Assert.DoesNotContain(
            items.OfType<ChatAssistantMessageViewModel>(),
            item => item.Text.Contains("Parsed locally", StringComparison.Ordinal));
    }
}
```

**Note:** `TestShell.Create` has no `aiProvider` parameter today. Add one (defaulting to null → the current `LocalHeuristicAiProvider`) in `tests/BeBoosted.Desktop.Tests/Support/TestDoubles.cs` around line 717, where the provider is constructed. Keep every existing caller compiling by giving the parameter a default.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Desktop.Tests --filter "FullyQualifiedName~ChatDegradedNoticeTests"`
Expected: FAIL — the notice never renders.

- [ ] **Step 3: Write the implementation**

In `ChatViewModel.SubmitAsync`, immediately after the `extraction` call and before the empty-drafts branch:

```csharp
        var extraction = await _ai.ExtractTasksAsync(text, context);
        if (extraction.DegradedNotice is { } degraded)
        {
            // Before the drafts, because it explains them.
            Items.Add(new ChatAssistantMessageViewModel(degraded));
        }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Desktop.Tests --filter "FullyQualifiedName~ChatDegradedNoticeTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Desktop/ViewModels/ChatViewModel.cs tests/BeBoosted.Desktop.Tests/ViewModels/ChatDegradedNoticeTests.cs tests/BeBoosted.Desktop.Tests/Support/TestDoubles.cs
git commit -m "feat: say when a capture was parsed locally instead of by the model"
```

---

### Task 11: The Settings card

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/SettingsViewModel.cs`, `src/BeBoosted.Desktop/Views/SettingsView.axaml`
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/CaptureModelSettingsUiTests.cs`

**Interfaces:**
- Consumes: `CaptureModelSettings`, `ISecretProtector`.
- Produces: `SettingsViewModel` members `IsCaptureHeuristic/IsCaptureOllama/IsCaptureClaude` (bool, settable), `OllamaEndpoint`, `OllamaModel`, `ClaudeModel` (string, settable), `ApiKeyEntry` (string, settable), `HasSavedKey` (bool), `CanUseClaude` (bool), `SaveApiKeyCommand`, `RemoveApiKeyCommand`.

**Copy, exact:**
- Card header: `Capture model`
- Claude consent: `The message you type, your project names, and today's date leave your computer — only when you press send.`
- Ollama consent: `Your message goes to the model running on this computer. Nothing leaves it.`
- Heuristic consent: `Nothing is sent anywhere. Task capture uses built-in rules.`
- Unavailable protector: `Saving an API key isn't supported on this platform yet.`

- [ ] **Step 1: Write the failing test**

```csharp
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
}
```

**Note:** `FakeAppDataPaths` may not exist. If not, add a minimal one to `TestDoubles.cs` implementing `IAppDataPaths` with three temp-path properties.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Desktop.Tests --filter "FullyQualifiedName~CaptureModelSettingsUiTests"`
Expected: FAIL to build — `SettingsViewModel` takes two constructor arguments.

- [ ] **Step 3: Write the implementation**

Rewrite `SettingsViewModel` to take the two new dependencies and add the members. Keep the existing AI-permission properties untouched:

```csharp
public sealed partial class SettingsViewModel(
    IAppDataPaths paths,
    AiPermissionSettings aiPermissions,
    CaptureModelSettings captureModel,
    ISecretProtector protector) : ViewModelBase
{
    // ... existing DataDirectory / Version / permission properties unchanged ...

    /// <summary>False where no protector exists, which is what disables the Claude option.</summary>
    public bool CanUseClaude => protector.IsAvailable;

    public string UnavailableKeyNotice => "Saving an API key isn't supported on this platform yet.";

    public bool IsCaptureHeuristic
    {
        get => captureModel.Source == CaptureModelSource.Heuristic;
        set => SetSource(value, CaptureModelSource.Heuristic);
    }

    public bool IsCaptureOllama
    {
        get => captureModel.Source == CaptureModelSource.Ollama;
        set => SetSource(value, CaptureModelSource.Ollama);
    }

    public bool IsCaptureClaude
    {
        get => captureModel.Source == CaptureModelSource.Claude;
        set
        {
            // Refused rather than half-applied: without a protector there is nowhere
            // safe to keep the key the choice would need.
            if (value && !CanUseClaude)
            {
                OnPropertyChanged();
                return;
            }

            SetSource(value, CaptureModelSource.Claude);
        }
    }

    private void SetSource(bool selected, CaptureModelSource source)
    {
        if (!selected || captureModel.Source == source)
        {
            return;
        }

        captureModel.Source = source;
        OnPropertyChanged(nameof(IsCaptureHeuristic));
        OnPropertyChanged(nameof(IsCaptureOllama));
        OnPropertyChanged(nameof(IsCaptureClaude));
        OnPropertyChanged(nameof(ConsentText));
    }

    public string ConsentText => captureModel.Source switch
    {
        CaptureModelSource.Claude =>
            "The message you type, your project names, and today's date leave your computer — "
            + "only when you press send.",
        CaptureModelSource.Ollama =>
            "Your message goes to the model running on this computer. Nothing leaves it.",
        _ => "Nothing is sent anywhere. Task capture uses built-in rules.",
    };

    public string OllamaEndpoint
    {
        get => captureModel.OllamaEndpoint;
        set { captureModel.OllamaEndpoint = value; OnPropertyChanged(); }
    }

    public string OllamaModel
    {
        get => captureModel.OllamaModel;
        set { captureModel.OllamaModel = value; OnPropertyChanged(); }
    }

    public string ClaudeModel
    {
        get => captureModel.ClaudeModel;
        set { captureModel.ClaudeModel = value; OnPropertyChanged(); }
    }

    /// <summary>The entry box only. The saved key is never read back into it.</summary>
    [ObservableProperty]
    public partial string ApiKeyEntry { get; set; } = string.Empty;

    public bool HasSavedKey => captureModel.ProtectedClaudeKey is not null;

    [RelayCommand]
    private void SaveApiKey()
    {
        if (!protector.IsAvailable || string.IsNullOrWhiteSpace(ApiKeyEntry))
        {
            return;
        }

        captureModel.ProtectedClaudeKey = protector.Protect(ApiKeyEntry.Trim());
        ApiKeyEntry = string.Empty;
        OnPropertyChanged(nameof(HasSavedKey));
    }

    [RelayCommand]
    private void RemoveApiKey()
    {
        captureModel.ProtectedClaudeKey = null;
        OnPropertyChanged(nameof(HasSavedKey));
    }
}
```

In `SettingsView.axaml`, add a card above the About card, following the existing cards' structure and classes exactly. Three `RadioButton`s in one `GroupName` bound to the three bool properties; the Ollama fields visible when `IsCaptureOllama`; the Claude fields when `IsCaptureClaude`; a `TextBox` with `PasswordChar="•"` bound to `ApiKeyEntry` plus Save/Remove buttons; a `TextBlock` bound to `ConsentText`; and a `TextBlock` bound to `UnavailableKeyNotice` shown when `CanUseClaude` is false. Give the card header the text `Capture model` and set `AutomationProperties.Name` on the three radios to `Built-in capture`, `Ollama capture`, and `Claude capture`.

Update `App.axaml.cs`'s registration if `SettingsViewModel` is constructed by hand there; if it is resolved from DI, add nothing — the container already has both new dependencies from Task 9.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Desktop.Tests --filter "FullyQualifiedName~CaptureModelSettingsUiTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Desktop/ViewModels/SettingsViewModel.cs src/BeBoosted.Desktop/Views/SettingsView.axaml tests/BeBoosted.Desktop.Tests/ViewModels/CaptureModelSettingsUiTests.cs tests/BeBoosted.Desktop.Tests/Support/TestDoubles.cs
git commit -m "feat: choose and configure the capture model in Settings"
```

---

### Task 12: Full gates and the manual live check

**Files:**
- Create: `docs/superpowers/plans/2026-09-03-capture-model-verification.md`

**Interfaces:**
- Consumes: the whole feature.

**This task ships nothing new.** It proves the feature against reality and records what was and was not verified — the standard this repo set with the resource-groups phase-1 record.

- [ ] **Step 1: Run the full gates**

```bash
dotnet test BeBoosted.slnx
dotnet build BeBoosted.slnx -warnaserror
dotnet format BeBoosted.slnx --verify-no-changes
```

Expected: both suites green with no new skips; no warnings; no format diff. Fix anything that fails before continuing.

- [ ] **Step 2: Capture the screenshot set**

```bash
BEBOOSTED_SCREENSHOT_DIR=$TEMP/bb-capture-renders dotnet test tests/BeBoosted.Desktop.Tests --filter "FullyQualifiedName~ScreenshotCapture"
```

Review the Settings render: the Capture model card must show the three choices, the consent sentence for the selected one, and no key value anywhere on screen.

- [ ] **Step 3: Live check — Ollama first, no account needed**

Ollama is already running locally with `qwen2.5:7b-instruct` pulled. In a **disposable profile**:

```bash
BEBOOSTED_DATA_DIR=$TEMP/bb-capture-live ./bb
```

In the app: Settings → Capture model → Ollama. Then send this exact message in the composer — the BB-QA-003 repro:

> Finish my DECA presentation before Friday. It probably needs two focused sessions. Also I still owe Ms. Rivera the rec request email.

Record what comes back. The heuristic produces three drafts, one titled *"It probably needs two focused sessions"*. Success is **two** drafts, with the "two focused sessions" information folded into the DECA task rather than standing as its own. Record the actual result either way — a local model that does not manage it is a finding, not a failure to hide.

- [ ] **Step 4: Live check — Claude**

Same disposable profile: Settings → Capture model → Claude, paste a real API key, Save. Send the same message. Record the drafts.

Then verify fallback for real: disconnect the network (or save a deliberately wrong key), send again, and confirm the reply says *"Parsed locally — Claude couldn't be reached."* and the drafts still arrive.

- [ ] **Step 5: Confirm the real library was never touched**

```bash
ls -la "$LOCALAPPDATA/BeBoosted/beboosted.db"
```

Its modification time must predate this session's work. Also confirm no plaintext key is anywhere in the disposable profile:

```bash
grep -r "sk-ant" "$TEMP/bb-capture-live" || echo "no plaintext key found"
```

- [ ] **Step 6: Write the verification record**

Create `docs/superpowers/plans/2026-09-03-capture-model-verification.md` covering: the suite numbers, what each live check did, the **exact drafts each backend returned for the BB-QA-003 message**, whether the fallback notice appeared, the plaintext-key grep result, and — explicitly — anything not verified. State limits plainly; the phase-1 record is the model to follow.

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/plans/2026-09-03-capture-model-verification.md
git commit -m "docs: record the capture-model verification evidence and its limits"
```

---

## Self-Review

**Spec coverage:** Goals → Tasks 5/6 (both backends), 8 (fallback + notice), 11 (consent copy, opt-in), 1 (heuristic default). Behavior sections → Task 11 (choosing), Task 4 + 11 (the key), Task 8 (capture end to end), Task 2 (what the model is asked), Task 8 (degradation table — one test per row). Components → Tasks 1–9 one-to-one. Dependencies → Task 4 (both packages). Testing → Tasks 3, 5, 6, 8 (parser/transports/router), 4 (protector), 11 (Settings UI), 10 (chat notice), 12 (manual live check). Non-goals honored: no task touches Q&A beyond delegating it, planning, streaming, or Keychain.

**Placeholder scan:** No TBD/TODO. Every code step carries real code. The two "if the SDK differs" notes in Task 6 name the exact discovery command and forbid changing the tests, which is guidance, not a placeholder.

**Type consistency:** `CaptureRequest`/`CaptureDraft`/`ICaptureModel` (Task 2) are used unchanged in 3, 5, 6, 8. `CaptureModelSettings` members (Task 1) match every later call site. `CaptureExtractionResult` (Task 7) is what Task 8 returns and Task 10 reads. `ISecretProtector` (Task 4) matches Tasks 6, 9, 11. `RoutedAiProvider`'s constructor (Task 8) matches its registration (Task 9).

**Fixed during review:** Task 8's `ProjectNamesGoOutAndAProjectIdComesBack` originally built two fixtures and asserted against the second — noise an implementer would have had to untangle. Rewritten to one fixture, asserting both directions of the mapping in the order they happen.

**One thing the implementer will hit:** Task 6's SDK member names (`AnthropicClientOptions.HttpClient`, `Messages.Create`, `TextBlock.Text`) are written from the SDK's documented shape, not from a compile against 12.45.0 in this solution. If they are wrong, the compiler names the wrong member in seconds and Task 6 says exactly how to find the right one. The tests assert behavior, so they do not move.

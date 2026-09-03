# Pluggable capture models: Claude, Ollama, and the built-in heuristic

Date: 2026-09-03

Status: Approved design, ready for implementation planning

## Problem

The product definition calls BeBoosted "a calm, chatbot-assisted calendar planner"
whose AI "helps capture, compare, prioritize, and schedule work". Everything around
that promise is built — review-first permissions, provenance by default, the Inbox
review list, the chat surface — but the intelligence behind it is
`LocalHeuristicAiProvider`: rule-based sentence splitting, regex duration and deadline
parsing, and substring project matching. Its own summary says a real provider "can be
registered behind the same `IAiProvider` port later".

The heuristic's limits are visible in daily use and are recorded as BB-QA-003, which
survived one fix attempt and is still open. Typing

> Finish my DECA presentation before Friday. It probably needs two focused sessions.
> Also I still owe Ms. Rivera the rec request email.

yields three drafts, one of which is titled *"It probably needs two focused sessions"*
— a sentence fragment presented as a task. The rule cannot be patched into
understanding: deciding that a sentence elaborates the previous task rather than
naming a new one is the judgment a language model makes and a regex cannot.

This is the highest-leverage gap between what the app promises and what it does.

## Goals

- Route **capture** — task extraction and metadata suggestion — through a real
  language model, so messy multi-sentence input produces sensible task drafts.
- Support **two model backends**: Claude via the Anthropic API, and a **local model
  through Ollama**, so the app is useful with no cloud account and no key.
- Keep the app fully functional with **no model configured at all**: the built-in
  heuristic remains the default and the fallback.
- Make what leaves the machine **explicit, opt-in, and stated in plain language**,
  honouring the "no required accounts or cloud synchronization" exclusion.
- Never lose a capture to an outage: a failed model call degrades to the heuristic and
  **says that it did**.

## Non-goals

- **Project Q&A stays on the heuristic.** `AnswerQuestionAsync` is unchanged in this
  slice. A model-backed answer is only as good as what it can read, and
  `SimpleLocalIndexer` indexes title and filename only (BB-QA-010) — resource-content
  extraction is its own project, and doing Q&A well means doing that first.
- **No plan explanations, no priority reasoning, no scheduling by model.**
  `PlanningService` and `PrioritySortService` are untouched. Their outputs are
  deterministic and explainable today, which the design principles require.
- **No streaming.** Capture is a single short request whose result is a review list,
  not a conversation. Token-by-token display would add a UI mode for no gain.
- **No conversation memory.** Each capture is one stateless request. The chat surface
  is documented as "temporary — collapses on close" and stays that way.
- **No retry loops or queueing.** One attempt, then fallback. The user can send again.
- **No macOS Keychain implementation.** The protector seam is designed for it; only
  the Windows DPAPI implementation ships here.
- **No Ollama model auto-discovery or model management.** The user names the model.
- No change to the AI permission model, provenance, `AiService`, or the review flow.

## Approach

Approach A of three considered: **a routing provider in front of narrow per-backend
capture models.**

A new `ICaptureModel` port carries only the two capture operations. Two
implementations sit behind it — Claude and Ollama — and a `RoutedAiProvider` becomes
the single registered `IAiProvider`. It reads the configured source per call,
delegates capture to that backend, falls back to the heuristic on any failure, and
passes project Q&A straight to the heuristic.

The two rejected alternatives:

- **Select the provider at startup in DI.** The composition root reads the setting and
  registers one `IAiProvider`. It adds no type, but changing the setting needs a
  restart, and each provider would have to implement its own fallback and its own Q&A
  delegation — the same logic three times, in the classes least able to share it.
- **Decorator chain** (`Fallback(Selected(Claude, Ollama, Heuristic))`). Composable in
  principle, but with three sources, live switching, and a notice that must survive
  back to the UI, the wrapping obscures a flow that reads plainly as one router.

`ICaptureModel` is deliberately narrower than `IAiProvider`. A backend that only
extracts tasks should not be made to implement project Q&A it never serves, and the
narrow port keeps each backend small enough to hold in one file and one test class.

## Behavior

### Choosing a capture model

Settings gains a **Capture model** card with three exclusive choices:

- **Built-in (no model)** — the default, and what every existing profile keeps. The
  heuristic parses captures. Nothing leaves the machine. No configuration.
- **Ollama (on this computer)** — endpoint (default `http://localhost:11434`) and
  model name (default `mistral`) fields.
- **Claude (cloud)** — an API key field and a model name (default
  `claude-sonnet-4-6`).

The card states, in plain language and without hedging, what each choice sends. For
Claude: *the message you type, your project names, and today's date leave your
computer, only when you press send.* For Ollama: *your message goes to the model
running on this computer; nothing leaves it.* For built-in: nothing is sent anywhere.

Switching takes effect on the next capture. No restart.

### The API key

The key is stored **encrypted in the settings database**, not in an OS credential
vault. That is a deliberate choice for this app: the whole profile — database,
resources, logs — already relocates under `BEBOOSTED_DATA_DIR`, which is how every
test run and every disposable-profile session stays isolated from the real library. A
credential-manager entry lives outside the profile, so a throwaway profile would read,
overwrite, or delete the real key. Keeping the key in the profile keeps that isolation
whole.

Encryption goes through a small `ISecretProtector` seam: Windows DPAPI at user scope
now, a Keychain implementation later, with the ciphertext stored as a settings value.
The key is write-only in the UI — saved or removed, never redisplayed. A profile
copied to another machine or another user account simply fails to decrypt, and the app
treats that exactly like a missing key.

### A capture, end to end

1. The user types into the composer and sends.
2. The router reads the configured source. Built-in → heuristic, done.
3. Otherwise it builds the shared extraction request: the message, the names of
   existing projects, and today's date.
4. The backend returns strict JSON. The shared parser validates it into
   `ExtractedTaskDraft` values.
5. Drafts enter the existing review list, subject to the unchanged task-capture
   permission. Their `SourceDescription` stays "from your message".
6. If anything in steps 3–4 fails, the heuristic parses the same message and the
   result carries a **degraded notice** the chat displays: *"Parsed locally — Claude
   couldn't be reached."*

The model never sees a domain identifier. It is given project *names* and returns a
project name; the router maps that back to a `ProjectId`, and an unrecognised name
becomes no project rather than a guess.

### What the model is asked to do

One prompt, shared by both backends, instructing the model to return an array of
tasks, each with a title, optional estimated minutes, optional deadline date, and
optional project name. The prompt states the rules that matter for this product:

- A sentence that elaborates the previous task — *"It probably needs two focused
  sessions"* — is not a new task; fold its information into that task instead. This is
  BB-QA-003 stated as an instruction.
- Titles are imperative and short, not copied sentences.
- Only what the message actually says: no invented deadlines, durations, or projects.
- Project names must come from the supplied list, or be omitted.
- An empty array is a valid answer for a message that contains no task.

### Degradation, stated precisely

Fallback covers every failure the same way, because to the user they are one event —
the model did not answer:

| Situation | Result |
| --- | --- |
| Source is Claude, no key saved | Heuristic, notice |
| Network unreachable, DNS failure, timeout (15 s) | Heuristic, notice |
| HTTP 4xx (bad key, refused) or 5xx | Heuristic, notice |
| Ollama not running, or the named model absent | Heuristic, notice |
| Response is not valid JSON, or fails validation | Heuristic, notice |
| Key present but undecryptable (copied profile) | Heuristic, notice |
| No protector on this platform (non-Windows) | Claude not selectable; the setting cannot be reached |

`SuggestMetadataAsync` degrades **silently** — it fills optional hint fields, and a
notice for a missing duration estimate would be noise.

The notice names the configured source ("Claude", "Ollama"), never a raw exception
message: the surface that shows it is a chat reply, not a log.

## Components

### `BeBoosted.Application` — `ICaptureModel`

The narrow port: `ExtractTasksAsync` and `SuggestMetadataAsync`, each taking a
`CaptureRequest` — the message text, the candidate project **names**, and today's
date. Deliberately **not** `AiContext`, which carries a `ProjectId`: a backend that
never sees a domain identifier cannot leak one into a prompt or invent one in a
response. The router builds the request and maps the answer back.

Returns `CaptureDraft` values — title, optional minutes, optional deadline, optional
project *name* — which the router turns into `ExtractedTaskDraft` once it has resolved
the name. A backend that cannot answer throws, and the router decides what that means.

### `BeBoosted.Application` — `CaptureModelSource`

`Heuristic | Ollama | Claude`, with the settings-backed accessor alongside
`AiPermissionSettings`, which it mirrors in shape. Defaults to `Heuristic` when unset
or unrecognised.

### `BeBoosted.Application` — `CaptureExtractionPrompt` and `CaptureDraftParser`

The shared prompt text and the shared response parser, in Application because both
backends and their tests depend on them and neither owns them. The parser validates
and clamps: titles trimmed and length-bounded, durations bounded to a sane range,
deadlines parsed as dates, malformed entries skipped rather than failing the batch.
Its output is `CaptureDraft`; the router resolves project names and produces the
`ExtractedTaskDraft` values the rest of the app already understands, so nothing
downstream knows a model was involved.

### `BeBoosted.Application` — `ISecretProtector`

`bool IsAvailable`, `Protect(string) → string`, and `TryUnprotect(string) → string?`.
Implemented in Infrastructure by `DpapiSecretProtector` (Windows, user scope).
`TryUnprotect` returning null is the "copied profile" case and is not an error.

`IsAvailable` exists because DPAPI is Windows-only and this codebase is macOS-ready:
`ProtectedData` throws `PlatformNotSupportedException` off Windows, and the honest
answer there is not to crash but to say so. A `UnavailableSecretProtector` is
registered on other platforms; it reports `IsAvailable = false`, and Settings then
disables the Claude option with the plain sentence *"Saving an API key isn't supported
on this platform yet."* Ollama and the built-in heuristic are unaffected — which means
the macOS build still has a real model backend before the Keychain protector lands.

### `BeBoosted.Infrastructure` — `ClaudeCaptureModel`

The official Anthropic C# SDK (`Anthropic` NuGet package), model `claude-sonnet-4-6`
by default, structured output for the draft array, `max_tokens` sized for a short
list, and a 15-second timeout. It reads the key through `ISecretProtector` at call
time, so saving a key in Settings takes effect immediately.

### `BeBoosted.Infrastructure` — `OllamaCaptureModel`

Plain `HttpClient` against Ollama's native `/api/generate` with `format: json` and
`stream: false`; endpoint and model from settings; the same 15-second timeout. No SDK
dependency — the surface used is one POST.

### `BeBoosted.Infrastructure` — `RoutedAiProvider`

The registered `IAiProvider`. Selects the backend per call, catches everything from
it, falls back to `LocalHeuristicAiProvider`, and delegates `AnswerQuestionAsync` to
the heuristic unconditionally. It also owns the project-name ↔ `ProjectId` mapping in
both directions, so no backend touches domain identity.

### `BeBoosted.Application` — extraction result

`IAiProvider.ExtractTasksAsync` returns drafts plus an optional degraded notice, and
`AiService.TaskExtractionOutcome` carries it through to `ChatViewModel`, which renders
it beside the review list. This is the one signature change on the existing port;
`SuggestMetadataAsync` and `AnswerQuestionAsync` are untouched.

### `BeBoosted.Desktop` — Settings

The Capture model card: the three-way picker, the conditional Ollama and Claude
fields, the consent copy, and Save/Remove for the key. It follows the existing AI
permissions cards in structure and voice.

## Dependencies

Two new packages, both pinned in `Directory.Packages.props` like every other
dependency (central package management is on):

| Package | Version | Project | Why |
| --- | --- | --- | --- |
| `Anthropic` | 12.45.0 | Infrastructure | The official Anthropic C# SDK. Verified as the Anthropic-owned package on nuget.org, not a community wrapper. |
| `System.Security.Cryptography.ProtectedData` | 10.0.11 | Infrastructure | DPAPI. Not in the BCL by default; the version matches the project's other Microsoft pins. |

Ollama needs no package: it is one `HttpClient` POST to a local endpoint.

## Risks

- **A model returns confident nonsense.** Mitigated by the review-first default that
  already exists: drafts are proposals until accepted, and every AI-originated task
  keeps its origin and provenance.
- **A local model returns worse drafts than the heuristic.** Real and accepted: the
  user chooses the backend, the parser refuses malformed output, and switching back is
  one radio button.
- **Cost surprise.** A capture is a short request; the model is named in Settings and
  the slice adds no background or automatic calls. Nothing calls the model except a
  message the user sent.
- **A hostile or enormous response.** The parser bounds every field, and `max_tokens`
  bounds the response.
- **Prompt injection via imported content.** Not reachable in this slice: only the
  composer message and project names are sent. It becomes real when Q&A ships, which
  is out of scope here.

## Testing

- **Parser**: well-formed, malformed, empty-array, missing-field, out-of-range,
  hostile-length, and unknown-project-name responses.
- **Router**: each source selects its backend; every failure row in the degradation
  table falls back and produces a notice; the notice names the configured source; Q&A
  always reaches the heuristic; `SuggestMetadata` degrades without a notice; project
  names map to ids and unknown names to none.
- **Ollama transport**: a fake `HttpMessageHandler` covering a good response, a
  connection refusal, a timeout, and a non-JSON body.
- **Claude transport**: constructed against a stub HTTP layer; **no live API calls in
  the suites.**
- **Protector**: round-trip, undecryptable input returning null, and the unavailable
  protector reporting `IsAvailable = false` rather than throwing. The DPAPI round-trip
  test is Windows-gated, the way the existing read-only-delete test is platform-gated.
- **Settings UI**: picker persistence, conditional field visibility, key save and
  remove, key never redisplayed, consent copy present, and the Claude option disabled
  with its explanatory sentence when no protector is available.
- **Chat**: a degraded capture renders its notice; a healthy capture renders none.
- **Live check, manual, at the end of the branch**: one real Claude capture with a
  real key and one real Ollama capture, in a disposable profile, recorded the way the
  resource-groups phase-1 verification was.

## Known limitations

- Project Q&A remains heuristic and keyword-based; BB-QA-010 is unchanged.
- The key is protected at user scope on one machine: a copied profile cannot decrypt
  it, by design.
- On non-Windows platforms the Claude backend cannot be configured at all until a
  Keychain protector ships; Ollama is the model backend there.
- Ollama must be started by the user; the app does not launch or install it.
- No streaming, so a slow local model shows nothing until it answers or the 15-second
  timeout fires.
- The prompt is tuned against Claude and a Mistral-class local model. A very small
  local model may parse poorly; that is visible in the review list and reversible.

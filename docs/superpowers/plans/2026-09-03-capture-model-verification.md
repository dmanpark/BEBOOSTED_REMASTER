# Capture Model Providers — Verification Record

Branch `feature/ai-provider`, tip `a666bc9` at the time this record's live verification ran. This
record covers Task 12, the full-gates and manual-live-check task for the pluggable capture-model
feature (Tasks 1–11). It states what was verified, how, and what was **not** — including one thing
found live that was not expected: a reproducible crash. That crash has since been **fixed and
independently re-reviewed**, in commit `0625420` — see the addendum near the end of this record.
The finding, its diagnosis, and the fix all stay here in full: this is not a record of a clean run,
it is a record of a real defect caught before it shipped.

Nothing has been pushed, no PR opened, nothing merged. Branch `main` was never touched.

## Gate results

| Gate | Command | Result |
| --- | --- | --- |
| Core tests | `dotnet test BeBoosted.slnx` — `BeBoosted.Tests` | **590 passed, 0 skipped** |
| Desktop tests | `dotnet test BeBoosted.slnx` — `BeBoosted.Desktop.Tests` | **564 passed, 3 skipped** |
| Build | `dotnet build BeBoosted.slnx -warnaserror` | **0 warnings, 0 errors** |
| Format | `dotnet format BeBoosted.slnx --verify-no-changes` | **clean, exit 0, no diff** |

The 3 desktop skips are the pre-existing screenshot-capture tests (`CaptureShellScreens`,
`CaptureMinimumWindowScreens`, `CaptureTaskEditorAndProjectScreens`), skipped whenever
`BEBOOSTED_SCREENSHOT_DIR` is unset. They predate this branch and are not new. All four gates are
green with no fixes applied — nothing here needed reporting back.

This table reflects the gate run at tip `a666bc9`, before the crash below was found and fixed. Four
tests were added with the fix (commit `0625420`); the core row is **594 passed, 0 skipped**
afterward, desktop unchanged. See the addendum near the end of this record for the full post-fix
gate run.

## Screenshot suite

Re-run with `BEBOOSTED_SCREENSHOT_DIR` set to a throwaway directory:

```
dotnet test tests/BeBoosted.Desktop.Tests --filter "FullyQualifiedName~ScreenshotCapture"
```

**3 passed, 0 skipped.** `shell-settings-1440x960.png` was inspected directly. The Capture model
card shows all three choices — Built-in rules, Ollama running locally, Claude — with the consent
sentence for the selected default ("No model involved. Works offline, every time.") and no key
value anywhere on screen. That last point is structural, not incidental: the API-key `TextBox` in
`SettingsView.axaml` only exists in the visual tree when `IsCaptureClaude` is true, which it is not
by default, so a screenshot of the default state cannot show a key regardless of what is saved.

## Live check — Ollama: the capture crashed the app (twice, reproducibly)

Settings → Capture model → Ollama was selected live, in a disposable profile
(`BEBOOSTED_DATA_DIR` = a throwaway temp directory), driven entirely through UI Automation
(`Find-ById`/`Find-ByName`/`Toggle-El`/`Set-ElValue` against the running app's own tree — no
coordinate clicks). Submission used a targeted `PostMessage(WM_KEYDOWN/WM_KEYUP, VK_RETURN)` sent
directly to the app's own window handle after `AutomationElement.SetFocus()` placed logical focus
on the composer — the same safety bar as the phase-1 record's control-handle technique: no cursor,
no z-order, no foreground dependency, no possibility of input reaching another application. The
composer correctly showed the endpoint (`http://localhost:11434`) and model (`qwen2.5:7b-instruct`)
defaults, and the consent line updated correctly to "Your message goes to the model running on this
computer. Nothing leaves it."

Sending the BB-QA-003 message — *"Finish my DECA presentation before Friday. It probably needs two
focused sessions. Also I still owe Ms. Rivera the rec request email."* — **crashed the whole
desktop process**, twice, on two separate launches. This was not a UI hang or an error toast; the
process itself terminated.

**Root cause, confirmed from the OS crash record (`Get-WinEvent` on the `.NET Runtime` Application
log source), stack trace identical both times:**

```
System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured
HttpClient.Timeout of 15 seconds elapsing.
 ---> System.TimeoutException / TaskCanceledException / IOException / SocketException (995)
   at System.Net.Http.HttpClient... (SendAsync chain)
   at BeBoosted.Infrastructure.Ai.OllamaCaptureModel.GenerateAsync(...) OllamaCaptureModel.cs:line 47
   at BeBoosted.Infrastructure.Ai.OllamaCaptureModel.ExtractAsync(...) OllamaCaptureModel.cs:line 28
   at BeBoosted.Infrastructure.Ai.RoutedAiProvider.ExtractTasksAsync(...) RoutedAiProvider.cs:line 42
   at BeBoosted.Application.Ai.AiService.ExtractTasksAsync(...) AiService.cs:line 47
   at BeBoosted.Desktop.ViewModels.ChatViewModel.SubmitAsync() ChatViewModel.cs:line 240
   at CommunityToolkit.Mvvm.Input.AsyncRelayCommand.AwaitAndThrowIfFailed(...)
   ... Avalonia.Threading.Dispatcher... Win32Platform.WndProc ...
```

The mechanism, traced through the actual source:

- `ServiceCollectionExtensions.cs:59` registers one shared `HttpClient` with
  `Timeout = TimeSpan.FromSeconds(15)`, used by both capture backends.
- `RoutedAiProvider.cs:47` catches backend failures with
  `catch (Exception error) when (error is not OperationCanceledException)` — the class's own doc
  comment states the intent plainly: *"no key, offline, timeout, refused, unparseable — is the same
  event to the user, so all of them land here."* But .NET represents **both** a caller-requested
  cancellation and an `HttpClient`-internal timeout as the same exception type
  (`TaskCanceledException : OperationCanceledException`), and the filter cannot tell them apart. A
  genuine `HttpClient.Timeout` firing is exactly as excluded as a real user cancellation, so it is
  **not** caught, and propagates out of `ExtractTasksAsync` uncaught.
- `ChatViewModel.cs:240` (`var extraction = await _ai.ExtractTasksAsync(text, context);`) awaits
  this with no surrounding try/catch.
- `CommunityToolkit.Mvvm`'s `AsyncRelayCommand` rethrows an unhandled command-execution exception
  onto the captured `SynchronizationContext`, which Avalonia's dispatcher then re-raises inside its
  own message pump (`Dispatcher.ExecuteJobsCore` → `Win32Platform.WndProc`) with nothing upstream to
  catch it — terminating the whole process. No global unhandled-exception handler is wired up
  anywhere in the app to intercept this.

**This is reproducible, not a fluke.** Both crashes (`2026-09-03 21:13:11` and
`2026-09-03 21:18:44`, per the Application log) have byte-identical stack traces. Ollama's own
`server.log` independently corroborates both: request one ran 13.87s before the client (BeBoosted)
closed the connection (`499`, `"client connection closed before llama-server finished loading"`);
request two ran 13.04s before an internal cancel (`500`, `slot: cancel task, id_task = 4`) — both
within a hair of the app's 15-second ceiling. On this machine, `qwen2.5:7b-instruct` runs **CPU-only**
(no CUDA/Vulkan device attached to the Ollama process for this call): a trivial 32-token warm-up
prompt measured 37 ms/token prompt-eval and took 13.17s of its 14.85s total just to *load* the
model into memory; the real BB-QA-003 extraction prompt (271 tokens) took over 13s even with the
model already warm. A 15-second budget is tight-to-insufficient for CPU-only local inference on
this hardware, and this app has **no code path that survives a real timeout** — every time one
fires, the result is a crash instead of the documented graceful degradation.

**This traces directly to a previously-deferred concern.** The Task 8 review (recorded in this
plan's `progress.md`) already flagged: *"no test proves `OperationCanceledException` actually
propagates (verified by code inspection only)."* It was accepted as a minor and deferred. This live
check is that untested path, now exercised for real — and it does not do what the class's own
comment says it should. No test in the suite constructs an `HttpClient`-timeout scenario at all
(`grep` for `OperationCanceledException|TaskCanceledException|Timeout` across `tests/` returns
nothing), so nothing catches this in CI either. (This gap is closed by the regression tests added
with the fix — see the addendum below.)

**Not fixed at the time this check ran.** Per this task's instructions, production code and tests
were out of scope for Task 12, so the defect was reported here rather than repaired. It was flagged
plainly: **this needs a fix before this feature ships** — either distinguish a genuine caller
cancellation from an `HttpClient`-internal timeout inside the catch (e.g. check whether the token
actually passed in was the one that fired), or catch `TimeoutException`/timeout-shaped
`TaskCanceledException` explicitly and route it into `DegradeAsync` like every other backend
failure, and add a test that manufactures exactly this condition (a handler that ignores the
cancellation token and stalls past the client timeout).

**It has since been fixed**, in commit `0625420`, using the first of these two approaches. See the
addendum near the end of this record for the fix, why the existing tests could not have caught it,
and the new tests that now pin it.

### What Ollama actually returns for BB-QA-003 (obtained outside the crash)

Because the live GUI path could not complete, the model's real output was captured by issuing the
**identical** request the app constructs — same system prompt (`CaptureExtractionPrompt.System`),
same user-prompt shape (`CaptureExtractionPrompt.BuildUser`, "Existing projects: none", today
2026-09-03), same endpoint, same model, `"format": "json"` — directly to Ollama's `/api/generate`
with a generous client-side timeout, bypassing only the app's undersized 15-second ceiling. This is
a supplementary diagnostic, explicitly **not** a live GUI verification; it exists to still answer
the semantic question this task exists to answer, since the crash prevented the app from answering
it itself.

Raw model response:

```json
{"tasks": [
  {"title": "Finish DECA presentation", "estimated_minutes": 120, "deadline": "2026-09-07", "project": "DECA"},
  {"title": "Send rec request to Ms. Rivera", "estimated_minutes": 30, "deadline": "2026-09-08", "project": null}
]}
```

**Two drafts, not three.** The core BB-QA-003 defect — the heuristic splitting "It probably needs
two focused sessions" into its own task — **is fixed by the model**: that sentence is folded into
the DECA task (reflected as `estimated_minutes: 120`, i.e. two ~1-hour sessions) rather than
standing alone. This is the headline result and it is a genuine success on the exact repro in the
task brief.

It is not a clean pass in every respect, and both issues below are worth recording honestly rather
than smoothing over:

- **Wrong deadline.** 2026-09-03 is a Thursday, so "before Friday" resolves to 2026-09-04. The model
  returned `2026-09-07` (the following Monday) — a real date-arithmetic error, three days off.
- **Two rule violations against its own system prompt.** The prompt says *"project" must be exactly
  one of the supplied project names, or omitted* (none were supplied — "Existing projects: none")
  and *do not invent deadlines... omit a field the message does not support*. The model invented a
  project name (`"DECA"`, matching nothing supplied) and invented a deadline for the second task
  (`2026-09-08`) despite the message giving no deadline cue for the Rivera email at all.

The invented project name is defended downstream: `RoutedAiProvider.ToDraft`'s comment states
plainly that a name the model invents "resolves to no project rather than a guess," and with no
project named "DECA" in the (empty) project list, this would correctly land as `ProjectId = null` in
the real app — not misfiled. The invented deadline has no such backstop and would show up in the
Inbox as a due date the user never gave.

## Live check — Claude fallback (no key saved)

Selected live via UI Automation: Settings → Capture model → Claude. `CanUseClaude` reflects
`ISecretProtector.IsAvailable` (DPAPI, always true on Windows) rather than whether a key is saved,
so the radio is enabled and selectable with no key present — confirmed via
`RadioButton.IsEnabled = True` before toggling. Confirmed no key was saved (no "Key saved" label in
the tree) before sending.

Sent the same BB-QA-003 message. **The app did not crash** — this path fails fast:
`ClaudeCaptureModel.CompleteAsync` throws `InvalidOperationException("No usable Claude API key is
saved.")` synchronously, before any network call is attempted, so it never touches the 15-second
timeout at all. `RoutedAiProvider`'s catch (not an `OperationCanceledException`) catches it cleanly.

The chat showed, in order:

1. The echoed user message.
2. **"Parsed locally - Claude couldn't be reached."** — the exact degraded notice.
3. "I found 3 tasks. Review them before they join your Inbox:"
4. Three drafts, read directly from the `Proposed task title` edit fields:
   - `Finish my DECA presentation` — due Fri · 1 h 30 min · from your message
   - `It probably needs two focused sessions` — 3 h · from your message
   - `Ms. Rivera the rec request email` — 10 min · from your message

The fallback notice appeared and drafts still arrived, confirming the degradation path end to end.
Incidentally, this run **live-reproduces the exact BB-QA-003 heuristic defect** the whole feature
exists to fix — the fragment "It probably needs two focused sessions" appears as its own task, since
this path exercises the built-in heuristic. That is expected and correct: it is the same heuristic
this feature routes *around* when a model is reachable, and this live run is simply the case where
the model source (Claude, no key) is unreachable by design.

## Isolation

| Check | Result |
| --- | --- |
| Real profile db (`%LOCALAPPDATA%\BeBoosted\beboosted.db`) last write, before this session | `2026-09-03 16:18:26.706036900 -0700` |
| Same file, checked again after all live checks | `2026-09-03 16:18:26.706036900 -0700` — **unchanged** |
| Plaintext key anywhere in the throwaway profile | `grep -r "sk-ant" $TEMP/bb-capture-live` → **no plaintext key found** |
| Broader key-shaped strings in the throwaway db | `grep -ac "sk-\|ApiKey\|ProtectedClaudeKey"` on the raw db file → **0 matches** |

The real profile was never opened by anything in this session. No key was ever saved during this
task (by design — the ruling below forbids it), so the "no plaintext key" result is expected rather
than a close call, and the broader grep confirms it beyond the brief's literal `sk-ant` pattern.

An unrelated, pre-existing `BeBoosted.exe` process (PID 31596, **Release** build, started
2026-09-03 16:14:20, well before this session) was present throughout and was left untouched — it
belongs to the user's own separate use of the app, not to this task, and nothing in this session
targeted it.

## Not verified

**The Claude live capture with a real API key — deliberately not performed, per this task's
ruling.** Hunting for or using the user's own Anthropic key is not this task's (or this agent's) to
do. This needs the user's own key and about five minutes of their time: Settings → Capture model →
Claude → paste a key → Save → send a message, then (optionally) remove or invalidate the key and
resend to see the degraded notice on a real, reachable-but-failing network path. What this leaves
unproven: the actual wire format and response handling against the real Anthropic API, and Claude's
real answer to the BB-QA-003 message. What still covers it: Task 6's stub-transport tests exercise
`ClaudeCaptureModel` against a fake `HttpMessageHandler` standing in for the SDK's transport, proven
(per Task 6's review) to genuinely intercept all traffic — so the parsing and mapping logic is
tested, just not the real network round-trip.

**The Ollama live capture, completed through the app's own UI, without crashing.** This was
attempted twice and crashed both times (see above). The reported drafts came from a supplementary
direct-to-Ollama probe outside the app, not from a successful live run of the feature's own
end-to-end path. The underlying defect has since been fixed and is covered by regression tests
(commit `0625420`, addendum below), but this record does not include a live re-run of the Ollama
capture against a real model after the fix — that re-verification has not been performed here.

**Whether the same crash occurs on the Claude backend under a slow-but-live network.** The fallback
check here used the no-key path, which fails before any HTTP call — it never approaches the
15-second timeout. `ClaudeCaptureModel` does **not** share the same `HttpClient` as Ollama —
at the time this check ran it built its own `AnthropicClient` per call, which left the SDK's own
defaults in effect (a 10-minute timeout, `Anthropic.Core.ClientOptions.DefaultTimeout`, and up to
two silent retries on connection errors, 408/409/429, and 5xx —
`Anthropic.Core.ClientOptions.DefaultMaxRetries`) instead of the spec's 15-second budget. That was a
real, separate defect from the one this addendum fixes: a slow or hanging Claude response would not
have produced a 15-second-shaped `TaskCanceledException` at all, so it would not even have reached
the router's misclassified catch clause within any reasonable window — the actual exposure was an
unbounded multi-minute hang (potentially up to three attempts under the default retry count), not a
15-second crash. `ClaudeCaptureModel` does share the same `RoutedAiProvider` catch clause, which is
the part of the original claim that was correct. This has since been fixed on this branch:
`ClaudeCaptureModel` now builds its `AnthropicClient` with an explicit 15-second timeout and zero
retries regardless of whether a test handler is injected (`ClaudeCaptureModel.RequestTimeout`,
`RequestMaxRetries`, and `CreateClient`), verified directly against the constructed client's own
`Timeout`/`MaxRetries` properties in `ClaudeCaptureModelTests`. Claude and Ollama now carry the same
effective 15-second guarantee even though they still don't share the same `HttpClient` instance — but
a live, slow-but-real Claude call was still not exercised here, before or after that fix.

**`SuggestMetadataAsync`'s behavior under the same timeout.** This is the other caller of both
`ICaptureModel` backends (used for duration/deadline hints when adding a task manually), and its own
catch clause has the identical `error is not OperationCanceledException` shape. Not exercised live
in this task; only `ExtractTasksAsync` (the composer path) was driven through the GUI.

**No screen reader was run**, matching the phase-1 record's own caveat — no claim is made about how
any assistive technology announces the Capture model card or the chat notice.

## Addendum 2026-09-03 — the crash is fixed and independently re-reviewed

Commit `0625420` fixes the timeout-misclassification defect described above, in
`src/BeBoosted.Infrastructure/Ai/RoutedAiProvider.cs`. Both catch sites previously read:

```csharp
catch (Exception error) when (error is not OperationCanceledException)
```

which cannot distinguish a genuine caller cancellation from an `HttpClient`-internal timeout, since
.NET represents both as `TaskCanceledException : OperationCanceledException`. Both sites now read:

```csharp
catch (Exception) when (!cancellationToken.IsCancellationRequested)
```

testing the caller's own token instead of the exception's type. A timeout — or any other backend
failure — now falls back through `DegradeAsync` like every other case; a request the caller actually
cancelled still propagates. The class's doc comment was rewritten to explain why the exception's
type is not a usable signal here, ending with an explicit instruction not to "fix" it back to a
type check.

**Why none of the router's existing tests caught this, stated plainly:** all 11 of
`RoutedAiProviderTests`'s pre-existing cases passed both before and after this fix, because every
one of them throws an exception type the old filter happened to let through correctly. A fully
green suite could not have found this defect — only running the real app against a real, slow model
did. That is the reason Task 12's live check existed at all, and this crash is the most valuable
thing it produced.

Two new tests were added to `tests/BeBoosted.Tests/Ai/RoutedAiProviderTests.cs`, with equivalents
for the metadata-suggestion path as well:

- A timeout — a stub throwing `TaskCanceledException` while the caller's own token is **not**
  cancelled — must fall back with the degraded notice and must not escape the method. This
  regression test was confirmed **red** against the old filter before the fix was applied, then
  green after.
- A genuine cancellation — an already-cancelled token — must still propagate rather than degrade.

**Suite after the fix:** `RoutedAiProviderTests` 15/15. Full solution: 594 passed / 0 skipped
(core), 564 passed / 3 skipped (desktop — the same pre-existing, unrelated screenshot-capture skips
described above). `dotnet build BeBoosted.slnx -warnaserror`: clean. `dotnet format BeBoosted.slnx
--verify-no-changes`: clean.

The fix was independently re-reviewed on top of the new tests.

**What this addendum does not claim.** The fix is verified by unit tests and code review, and the
degraded-fallback path itself was exercised live in the Claude no-key check above — but that check
fails before any HTTP call, so it never approached the 15-second timeout. This addendum does
**not** claim the app was re-driven live against a real, slow Ollama model after the fix to watch
the process survive past 15 seconds without crashing. That live re-run has not been performed in
this record.

## Status

Gates are clean. The feature's core capability — a real model correctly folding a sentence fragment
into its parent task, where the built-in heuristic splits it into a third, wrong draft — is
confirmed working, for the message this defect was originally filed against. The live Ollama check
surfaced a **real, reproducible crash** in the router's exception handling, unrelated to model
quality, affecting any capture that takes longer than 15 seconds — on CPU-only local inference,
close to the common case rather than an edge case. That defect has since been **fixed and
independently re-reviewed**, in commit `0625420` (addendum above): both catch sites now test the
caller's own cancellation token instead of the exception's type, a timeout-shaped regression test
was confirmed red against the old code and green against the fix, and the full suite — 594 passed
core, 564 passed / 3 pre-existing skips desktop — is green under `-warnaserror` and
`format --verify-no-changes`. No open ship-blocking defect remains on this branch. What this
addendum does not claim: the app was not re-driven live against a real Ollama model after the fix —
only the fallback path was exercised live (via the no-key Claude check above), and the fix itself
was verified by tests and review rather than by a fresh live GUI run. The Claude live capture with
a real API key remains explicitly unperformed, pending the user's own key.

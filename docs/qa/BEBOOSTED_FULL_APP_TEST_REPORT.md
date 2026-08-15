# BeBoosted Full Application QA Report

## Executive verdict

**Overall: Reject pending fixes.** The application has a strong local-first architecture, a clean build, a stable 234-test automated suite, working Windows packages, and many successful live workflows. It is not ready for broader distribution because two normal-use Windows defects block calendar coverage and bottom-edge controls, and the macOS output is not an installable application.

| Target | Verdict | Rationale |
|---|---|---|
| Internal dogfooding | **Accept with known issues** | Safe with disposable/known data and explicit workarounds; no observed data loss, network activity, or test instability. |
| Windows alpha distribution | **Reject pending fixes** | BB-QA-001 and BB-QA-002 affect core calendar and chat/shell access on supported Windows hardware. |
| Windows public release | **Reject pending fixes** | High defects plus incomplete task editing, import validation, and accessibility behavior remain. |
| macOS readiness | **Reject / not distribution-ready** | `osx-arm64` compiles, but there is no `.app`, plist, signing, notarization, or Mac runtime evidence. |

Defects: **15 total — 0 Critical, 3 High, 8 Medium, 4 Low**.

The two user-reported blockers were independently reproduced and are prominent in this report:

- The Today and Week timelines are hard-limited to **06:00–23:00**; scrolling or scheduling outside that interval is impossible (BB-QA-001).
- At 200% display scaling, the restored/default window extends beneath the Windows work area and the taskbar cuts off the bottom composer and other bottom controls (BB-QA-002).
- The Projects empty-state message is not reliably centered in the usable content area (BB-QA-014).

## Test environment

| Item | Value |
|---|---|
| Audit date | 2026-08-12, America/Los_Angeles (PDT) |
| OS | Windows 11 Home 10.0.26200, build 26200 |
| Architecture | x64 |
| Display used for live high-DPI reproduction | 2880×1800 physical pixels, 200% scaling; Windows work area 2880×1704 |
| .NET SDK | 10.0.111 |
| .NET runtime | 10.0.11 |
| Git branch | `main` |
| Commit | `c966d24724f38ad5faa57744ba44a5ab52d9624a` |
| Initial Git status | Clean; `main...origin/main [gone]` |
| Repository | `C:\Users\daria\BeBoosted_Remaster\BEBOOSTED_REMASTER` |
| Disposable QA root | `C:\Users\daria\AppData\Local\Temp\BeBoosted-QA-20260812-163915-24e35ed5` (530 files / 343,192,269 bytes recycled after evidence collection) |
| Development executable | `src/BeBoosted.Desktop/bin/Debug/net10.0/BeBoosted.exe` |
| Existing Windows package | `publish/win-x64/BeBoosted.exe` |
| Fresh Windows package | Disposable `publish/win-x64` output under the QA root |
| Fresh macOS compile output | Disposable `publish/osx-arm64` output under the QA root |

Every application launch used `BEBOOSTED_DATA_DIR` under the disposable QA root. The normal BeBoosted profile was not opened or modified. Only generated text, PDF, PNG, link, task, project, and calendar data were used.

## Scope and methodology

The audit read the approved design specification, Claude UI prompt, design references, implementation report, solution/project files, all seven migrations, all 36 test source files, and all 24 phase-seven screenshots before evaluating claims. Evidence types are labeled as follows:

- **Live**: exercised in a visible native Windows process through UI Automation, keyboard/mouse input, or native file-picker interaction.
- **Automated**: verified by the checked-in tests or the screenshot-capture test.
- **Static**: verified by direct source, project, migration, or package inspection.
- **Headless visual**: rendered by the Avalonia screenshot test; not a screenshot of the packaged native window.
- **Not tested**: not honestly executable in this Windows environment or not exposed by the current UI.

No production code, tests, specifications, committed screenshots, publish outputs, or Git history were changed. The only repository outputs are this report and one selected evidence image.

## Baseline verification

### Repository and architecture

- 281 tracked files; initial worktree clean.
- No tracked database, log, secret, user resource, or tracked `publish/` artifact was found.
- Domain has no project/package dependencies on Application, Infrastructure, or Desktop.
- Application references Domain only.
- Infrastructure references Application and implements persistence/storage/provider ports.
- Desktop references Application and Infrastructure and owns composition.
- Production search found no `TODO`, `FIXME`, `HACK`, `NotImplementedException`, hard-coded user-machine path, embedded secret, vendor calendar client, network AI client, telemetry client, production mock-data path, UI SQL, or warning suppression.
- Three `async void` event handlers exist for UI-only drag/file-picker events. The file-picker handlers can still surface an unhandled exception if storage fails after a selection.
- No production empty catch was found. A test-cleanup catch is intentionally best effort.
- Review-sized files include `CalendarViewModel.cs` (630 lines), SQLite project repositories (333), `FileDetailViewModel.cs` (298), and `ChatViewModel.cs` (285).
- `FileDetailViewModel` directly calls `File.Exists`, `Path.GetExtension`, and Avalonia `Bitmap`; this is a layering/review concern, not a demonstrated runtime failure.

### Exact fresh command results

| Command | Exit | Result | Wall time observed |
|---|---:|---|---:|
| `dotnet --info` | 0 | SDK 10.0.111; runtime 10.0.11; Windows x64 | Informational |
| `dotnet restore BeBoosted.slnx` | 0 | Restored/up to date | 2.711 s |
| `dotnet format BeBoosted.slnx --verify-no-changes` | 0 | No formatting changes required | 74.474 s |
| `dotnet build BeBoosted.slnx -warnaserror --no-restore` | 0 | 0 warnings, 0 errors | 63.959 s |
| `dotnet test BeBoosted.slnx` — run 1 | 0 | 234 passed, 0 failed, 1 skipped | 17.856 s |
| Core test project separately | 0 | 131 passed, 0 failed, 0 skipped | 4.596 s |
| Desktop test project separately | 0 | 103 passed, 0 failed, 1 skipped | 8.369 s |
| Full suite — run 2 | 0 | 234 passed, 0 failed, 1 skipped | 5.618 s |
| Full suite — run 3 | 0 | 234 passed, 0 failed, 1 skipped | 6.811 s |

No flakiness occurred across the three full-suite runs. The one skip is `ScreenshotCaptureTests.CaptureShellScreens`; it is an intentional environment gate when `BEBOOSTED_SCREENSHOT_DIR` is absent. With that variable set to a disposable directory, the test passed in 6.653 seconds and created all 24 expected images.

Final acceptance gate after all package/runtime work:

- `dotnet format BeBoosted.slnx --verify-no-changes --no-restore`: exit 0 in 31.068 s.
- Exact required `dotnet build BeBoosted.slnx -warnaserror` (including its normal restore): exit 0 in 33.310 s, 0 warnings, 0 errors.
- Exact required `dotnet test BeBoosted.slnx`: exit 0 in 25.508 s; 234 passed, 0 failed, 1 skipped.

One build-cache caveat was reproduced and is recorded rather than hidden: immediately after the Release RID publishes, `dotnet build ... --no-restore` failed because the last Release restore had excluded the Debug-only `AvaloniaUI.DiagnosticsSupport` assets needed by `WithDeveloperTools()`. The required build with its normal restore regenerated Debug assets and passed. This is a configuration-sensitive no-restore workflow caveat, not a failure of the documented final command.

## Requirements traceability

| Approved requirement | Status | Evidence and qualification |
|---|---|---|
| Cross-platform .NET/Avalonia solution with clean dependency direction | Pass | Static project/reference audit and clean warning-as-error build. |
| Local-first SQLite persistence and migrations | Pass | Live clean profile; migrations 1–7 logged in order; restart persistence verified. |
| Today is first-run calendar default | Pass | Live empty-profile UI Automation. |
| Today/Week last view persists | Pass | Live Week selection, graceful close, restart, Week restored. |
| Monday-first seven-day Week view | Pass | Live and automated/headless inspection. |
| Calendar covers a user's full day through scrolling | **Fail** | Hard-coded 06:00–23:00 geometry; BB-QA-001. |
| Current-time, fixed, task, proposed, complete, and conflict states | Pass | Live fixed/overlap/proposal/approved states plus automated domain/UI coverage. |
| Fixed commitments remain protected | Pass | Automated planning/domain tests and live overlap behavior. |
| Drag/resize, snapping, cross-day manipulation, keyboard adjustment | Partial | Automated tests cover geometry/manipulation; not every pointer gesture was repeatably exercised in the native process. |
| Universal Inbox capture without forced categorization | Pass | Live title-only and Unicode capture; project/category not required. |
| Task edit, save, cancel, delete/dismiss | **Partial** | Save/delete exposed; no Cancel edit affordance; BB-QA-007. |
| User-editable task deadline, duration, project | Pass | Automated binding/service tests; native flyout inspected. |
| User-editable scheduling constraints and recurrence | **Fail** | Domain fields exist but no Inbox editor controls; BB-QA-009. |
| Batch selection in Inbox | **Fail** | No selection controls/model; BB-QA-008. |
| Completion and non-punitive rollover outcomes | Pass | Automated domain/view-model coverage; live outcome affordances exposed after elapsed task blocks. |
| Priority Sort pairwise workflow | Pass | Live two-task comparison/results/ranks; automated tie, undo, period, deterministic and keyboard coverage. |
| No fabricated decimal priority score | Pass | Live/results inspection and source audit; ordinal ranks only. |
| Reviewable draft planning before approved mutation | Pass | Live draft, approve, undo, discard; automated partial approval/restart coverage. |
| Independent AI task and calendar permissions | Pass | Live settings, restart persistence, auto-task capture, manual draft; automated auto-plan chat coverage. |
| AI task-context extraction | **Partial** | Core parsing works but context sentence becomes a second task; BB-QA-003. |
| Project-scoped Files, no nested Files/global document dashboard | Pass | Live Project/File navigation and static topology inspection. |
| Document, link, note, image resources | Pass | All four added live with generated resources. |
| Only allowed file types import | **Fail** | Typed `.bin` bypassed picker filter and indexed as PDF/document; BB-QA-005. |
| Imported bytes copied to controlled storage | Pass | Live PDF/image import; GUID-based copy verified; original PDF moved away and stored copy remained. |
| Resource indexing and state persistence | Pass | Live Indexed state, DB inspection, restart behavior through repository tests. |
| Document content retrieval with exact file/page/note/link citation | **Partial** | Note/link citations work; PDF bytes/pages are not parsed; BB-QA-010. |
| Project-scoped Q&A and cross-project isolation | Pass | Live note-supported answer/citation navigation; automated multi-project isolation. |
| Provenance and Needs-review invalidation | Pass | Live citation provenance; deleting cited note set provenance `needs_review=1`. |
| Chat is temporary overlay and does not permanently reflow calendar | Pass | Live and XAML inspection. |
| Shell remains fully usable at supported Windows conditions | **Fail** | High-DPI restored/default window hides bottom controls under taskbar; BB-QA-002. |
| Projects empty state is centered | **Fail** | User-reported and layout cause confirmed; BB-QA-014. |
| Keyboard shortcuts and platform keymap | Partial | Windows shortcuts/UIA semantics verified; macOS mapping code inspected only. |
| Overlay focus management and focus restoration | **Fail** | Inbox close/Escape leaves no app element focused; BB-QA-006. |
| Meaningful accessible names and non-color state | Pass with limitation | UIA names include block title/time/state; native screen-reader software was not used. |
| Two approved screenshot sizes and visual fidelity | Partial | 24/24 fresh renders exactly match phase seven; committed visual defects remain (BB-QA-012/013). |
| No runtime network/telemetry/calendar dependency | Pass | Zero TCP/UDP endpoints in live process; static code/package audit. |
| Safe path handling | Partial | Normal imports use generated GUID names, but storage resolution lacks containment validation; BB-QA-011. |
| Windows self-contained package | Pass | Fresh and existing packages launched and closed with disposable profiles. |
| macOS code readiness and distributable packaging | **Partial/Fail** | Cross-publish succeeds; distribution bundle/runtime checks absent; BB-QA-015. |

## Screen-by-screen results

| Screen | Result | Evidence |
|---|---|---|
| Calendar — Today | Partial | Default, navigation, timeline, fixed/conflict/proposed states pass. Same 06:00–23:00 limit as Week. |
| Calendar — Week | Partial | Seven-day Monday-first layout, navigation, recurrence, planning pass; range limit and 1280 title clipping fail. |
| Inbox drawer | Partial | Capture, Unicode, completion, edit opening, close/reopen, Plan and Priority Sort pass. Cancel, batch selection, and focus return fail. |
| Priority Sort comparison/results | Pass | Live left choice produced dense #1/#2 ranks; keyboard hints present. Remaining branches covered automatically. |
| Plan draft/approved/undo | Pass with visual issue | Live propose/approve/undo/discard and conflict states pass; 10-minute visual block issue remains. |
| Projects list | Partial | Empty/create/open/restart pass; empty-state centering fails. |
| Project detail | Pass | Empty tasks/blocks, File shelf, create/open and scoped Ask action exercised live. |
| Project File | Partial | Four resource kinds, copying, index state, selection, removal/provenance pass; invalid extensions and PDF text/page citations fail. |
| Chat | Partial | Review-first, auto-add, project answer/citation, close and provenance exercised. Parser creates a spurious context task. |
| Settings | Pass | Both independent permission pairs changed and persisted across restart; external calendar remains disabled/Coming later. |

## Workflow results

### Clean startup and shell

- **Live pass:** empty profile produced a visible responsive window, database, logs and resources directory.
- **Live pass:** migrations 1 through 7 applied once, in order, with no errors. Restart did not reapply them.
- **Live pass:** Today first-run default and Week restart persistence.
- **Live pass:** valid-profile restart preserved project, File/resources, AI settings and last calendar view.
- **Live pass:** malformed database displayed a clear `file is not a database` error window and closed cleanly.
- **Fail:** data-directory setup failure occurs before the migration error handler; unusable path exits without UI (BB-QA-004).

### Tasks and Inbox

- Live title-only and Unicode/punctuation capture succeeded. The field cleared after submission and task persisted.
- Live automatic capture added `Submit scholarship application tomorrow`, retained `AI added`, and did not change the calendar permission.
- Live open count/ranks updated after planning, undo, and Priority Sort.
- Automated tests cover deadlines, durations, project assignment, duplicates, completion, remaining duration, rollover outcomes and repository persistence.
- Native Save field-edit automation could not be treated as conclusive because UI Automation value injection does not always commit Avalonia two-way bindings. Static inspection conclusively found no Cancel command binding.
- Large Inbox test: 5,000 tasks plus 2,000 blocks left 3,000 unscheduled tasks; the packaged app reached a responsive window in 2.484 s and showed the 3,000 badge.

### Calendar and planning

- Live recurring weekly fixed commitment at 09:00–10:00 created successfully.
- Live second overlapping commitment marked both blocks as conflicts.
- Live Plan created two proposed blocks without mutating approved state; Approve converted them to approved blocks; Undo restored the draft; Discard removed it.
- Automated tests cover proposal drag/resize/remove, partial approval, conflict checks, unresolved drafts, persistence and fixed-event protection.
- Direct pointer drag/resize and modifier snapping were evaluated primarily through deterministic geometry/view-model tests because native synthetic pointer automation was not sufficiently reliable to call equivalent to a user drag.

### Priority Sort

- Live: two candidates, comparison prompt, left selection, #1/#2 results and Inbox rank badges.
- Automated: right, tie, adaptive comparisons, undo/back/Escape, early plan, period-specific ranks, dense/shared ranks, deadline separation, deterministic decisions, and keyboard commands.

### Projects, Files and AI Q&A

- Live created `QA Project café`, `QA Evidence`, a Unicode multiline note, link, generated PDF and generated PNG.
- PDF/image bytes were stored as GUID-named files under the disposable profile.
- Moving the original PDF away left the stored 1,705-byte copy intact.
- A note-supported question returned the correct snippet and an `Open source Unicode note ✓` citation; invoking it navigated to the exact selected resource.
- Removing that note set the related provenance record to Needs review.
- Unsupported `.bin` import was incorrectly accepted as a Document.
- PDF retrieval indexes only title/original filename, so the generated PDF fact and page cannot be answered/cited.

## Persistence and migration results

- New database size after ordinary workflows: 163,840 bytes.
- Synthetic performance database after 5,000 tasks, 2,000 blocks and 1,500 resources: 2,732,032 bytes after checkpoint/close.
- Migrations observed in order: `0001_initial` through `0007_ai`.
- SQLite WAL/SHM files appeared only while the database was open and checkpointed normally on shutdown.
- Project, File/resource metadata, AI settings, Week selection, priority ranks, calendar commitments and tasks persisted through the tested restart paths.
- Migration corruption is surfaced safely. Directory-creation failure is not (BB-QA-004).
- Schema review found limited foreign-key coverage in some comparison/rank/proposal tables. No live corruption resulted, but stronger relational constraints would improve resilience.

## Accessibility and keyboard results

**Automated/UIA inspection, not an actual screen-reader test.** No NVDA, JAWS, Narrator session, switch control, or macOS VoiceOver test was performed.

Passes:

- Primary rail controls expose Calendar, Inbox, Projects and Settings names and appropriate radio/toggle semantics.
- Calendar blocks expose title, date, time and state (fixed, proposed, conflict).
- Composer, Chat input, Send/Close, Inbox capture, edit/complete controls, File actions and citation chips have meaningful names.
- Priority cards are buttons and show keyboard hints for arrows, tie, Backspace and Escape.
- Today/Week and AI permissions expose radio semantics.
- Focus-visible styles are defined for primary control types; completion/conflict/proposal state includes text/icon/border cues rather than color alone.

Failures/limitations:

- Closing Inbox with its Close control or Escape produced zero focused elements within the app rather than returning focus to Inbox (BB-QA-006).
- The high-DPI window issue places Settings partly and the composer substantially under the taskbar, making keyboard/mouse discovery harder (BB-QA-002).
- Full keyboard-only pointer-equivalent calendar manipulation and real screen-reader speech were not independently validated.
- No explicit reduced-motion setting was found; current UI motion is limited and no problematic animation was observed.

## Visual comparison results

- Fresh headless screenshot run produced all **24/24** expected PNGs: 12 screens at 1440×960 and 1280×800.
- SHA-256 comparison: **24/24 fresh images exactly matched** the committed `docs/implementation/screenshots/phase7` files.
- All fresh images were visually inspected. They are headless renders, not packaged-app screenshots.
- The native high-DPI cutoff does not appear in those headless images because they have no Windows taskbar/window-restoration context.

Observed visual defects:

- Native high-DPI bottom cutoff: [evidence image](evidence/BB-QA-002-high-dpi-bottom-cutoff.png).
- At 1280×800, narrow Week columns clip/truncate task content (BB-QA-013).
- A 10-minute plan block is forced to a minimum visual height and collides/clips against adjacent content (BB-QA-012).
- The empty Projects message is not centered in the remaining content surface (BB-QA-014).
- Settings' lower About section is below the 1280 fold but remains reachable by its ScrollViewer; this is not recorded as a defect.

## Local-first, privacy, and security results

| Check | Result |
|---|---|
| Runtime network endpoints | Pass: 0 TCP, 0 UDP for the live process. |
| Network/telemetry clients in production | Pass: none found. Test-only telemetry dependencies are not in the app. |
| External AI/calendar call | Pass: deterministic local provider only; no vendor/calendar SDK. |
| Fonts/assets local | Pass: bundled Avalonia assets/fonts; no CDN URL. |
| Logs contain task/resource contents | Pass: no generated content terms found. |
| Logs contain secrets | Pass: no secrets found. |
| SQL user values parameterized | Pass: repository command inspection. |
| Resource containment in configured profile | Pass in normal workflows: controlled resource directory and GUID filenames. |
| Path traversal defense | Partial: `ResolvePath` combines an unchecked stored path; BB-QA-011. |
| Link execution safety | Pass: only `http(s)` or `https://`-prefixed values are sent to shell open. |
| Automatic permission escalation | Pass: task and calendar settings persisted independently. |
| Corrupted database recovery | Pass: clear startup error. |
| Unavailable data-root recovery | Fail: silent exit; BB-QA-004. |

Five test log files totaled 3,164 bytes. Excluding the intentionally corrupted-database profile, no error/fatal/exception lines were found. No tested note/task content appeared in logs.

## Performance observations

Measurements are approximate wall-clock observations on this machine, not a formal benchmark.

| Scenario | Observation |
|---|---|
| Debug clean-profile first visible window | ~2.15 s |
| Existing packaged executable, clean profile | 2.002 s |
| Fresh self-contained publish, first clean-profile run | 8.601 s (first-use/migration cold path) |
| Large profile: 5,000 tasks + 2,000 blocks | 2.484 s to responsive visible window; 267.7 MB working set; 191.3 MB private |
| Large project: 1,500 resources | Project list 234 ms; detail 204 ms; File 227 ms; responsive |
| Large project working set | 356.7 MB working set; 273.9 MB private |
| Ordinary startup working set | Observed approximately 140–292 MB; grew to ~418 MB after extensive screen/workflow exercise |
| Graceful shutdown | 223–341 ms in measured development runs; packaged runs exited within the 5 s bound |
| Log growth | 3,164 bytes across five tested profiles; no unexpected production errors |

Repeated navigation, chat/Inbox open/close, Project/File transitions, planning and restart remained responsive. No process crash or unhandled exception occurred during normal workflows. Memory reclamation over a long multi-hour session was not measured.

## Windows package results

Fresh command:

```powershell
dotnet publish src\BeBoosted.Desktop\BeBoosted.Desktop.csproj `
  -c Release -r win-x64 --self-contained true -o <temporary-output>
```

- Exit 0 in 19.090 s including RID restore.
- 245 files, 218,245,431 bytes total.
- `BeBoosted.exe` exists.
- No `.db`, `.log`, profile, secret or user resource was packaged.
- Clean-profile package launch created the expected database/log/resource directories, exposed Calendar/composer through UIA and closed with exit 0.
- Existing `publish/win-x64/BeBoosted.exe` also launched in 2.002 s, exposed Calendar/Projects/Settings and exited cleanly.
- An initial `--no-restore` RID publish failed with expected NETSDK1047 because the assets file had no `win-x64` target; rerunning the independent publish with the required RID restore succeeded. This is a command-precondition observation, not an application defect.

**Windows package verdict: technically functional, but distribution blocked by BB-QA-001 and BB-QA-002.**

## macOS readiness results

No macOS runtime claim is made.

| Item | Classification | Evidence |
|---|---|---|
| `osx-arm64` self-contained publish | Verified from Windows | Exit 0 in 7.931 s; 242 files; 120,212,635 bytes. |
| Platform-neutral compilation | Verified from Windows | Shared projects compile for target. |
| Accidental Win32 dependency in shared code | Verified from Windows | No Win32 P/Invoke in production shared/application layers. |
| App-data location | Code-ready, requires macOS | `~/Library/Application Support/BeBoosted` branch inspected. |
| Cmd keyboard mapping | Code-ready, requires macOS | `Meta`/`⌘J` implementation inspected. |
| `open` / `open -R` | Code-ready, requires macOS | Platform service inspected; quoting should be validated on real filenames on Mac. |
| Window restoration | Code-ready, requires macOS | Avalonia abstraction used; multi-monitor/Retina behavior untested. |
| Bundled fonts | Verified from Windows output | Font assets packaged locally. |
| `.app` bundle and `Info.plist` | **Not implemented** | Publish output is a flat directory; no `.app` or plist. |
| Signing/hardened runtime/notarization | **Not implemented / blocked** | Checklist is documented but no artifact or Mac execution evidence. |
| Gatekeeper launch, Retina, VoiceOver, Finder behavior | Blocked | Requires macOS hardware/runner. |

## Defect register

### BB-QA-001 — Timeline excludes midnight–06:00 and 23:00–midnight

- **Severity:** High
- **Subsystem:** Calendar Today and Week
- **Requirement violated:** Full-day scrolling/scheduling and boundary handling.
- **Reproduction:** Open Today or Week; scroll to the top and bottom. Attempt to view/schedule before 06:00 or after 23:00.
- **Expected:** User can reach and schedule the full day, including early and late commitments.
- **Actual:** Timeline begins at 06:00 and ends at 23:00; scroll and drop targets clamp to those bounds.
- **Evidence:** Live user reproduction; `src/BeBoosted.Desktop/Views/TimelineSurfaceView.axaml.cs` constants `VisibleStartHour = 6`, `VisibleEndHour = 23`; `TimelineGeometryTests` explicitly assert clamping to 06:00 and 23:00.
- **Frequency:** 100%, both views.
- **Likely cause:** A fixed display window was implemented as a hard data/manipulation boundary.
- **Recommended remediation:** Render/scroll 00:00–24:00, retain a sensible initial scroll position, and use the full range for hit testing, drag and resize. Add early/late UI and geometry tests.
- **Blocker:** Blocks normal use for early/late users and blocks Windows release.

### BB-QA-002 — Bottom composer and controls fall under Windows taskbar at high DPI

- **Severity:** High
- **Subsystem:** Main window/shell/chat
- **Requirement violated:** Supported Windows layout, safe window restoration, uncropped controls.
- **Reproduction:** On 2880×1800 at 200%, launch/restore the default 1440×960-DIP window. Inspect bottom composer and Settings rail control.
- **Expected:** Client bottom remains inside the monitor work area.
- **Actual:** Client bottom was physical Y=1754 while work-area bottom was Y=1704: 50 px hidden. Settings extended to Y=1726 and the composer was covered by the taskbar.
- **Evidence:** [Native screenshot](evidence/BB-QA-002-high-dpi-bottom-cutoff.png); Win32 measurement; `MainWindow.axaml` default/min sizes; `WindowStateService.Restore` only tests `screen.Bounds.Intersects(probe)` and does not clamp to `WorkingArea`.
- **Frequency:** 100% on tested high-DPI setup and persisted restart.
- **Likely cause:** DPI-independent width/height combined with pixel position and intersection-only restore validation; oversized normal window is restored partly outside the work area.
- **Recommended remediation:** Normalize placement units, clamp normal bounds to the selected screen working area, validate all edges, and test 125/150/175/200% scaling with taskbars on every edge.
- **Blocker:** Primary chat and navigation controls can be inaccessible; Windows release blocker.

### BB-QA-015 — macOS output is not an installable/distributable application

- **Severity:** High (macOS target)
- **Subsystem:** Packaging/release
- **Requirement violated:** macOS distribution readiness.
- **Reproduction:** Publish `-r osx-arm64`; inspect output.
- **Expected:** `.app/Contents/{MacOS,Resources}`, plist, icons, signing/notarization-ready artifact.
- **Actual:** Flat 242-file output, no `.app`, `Info.plist`, signature or notarization.
- **Evidence:** Fresh disposable publish; implementation report also documents this gap.
- **Frequency:** 100%.
- **Likely cause:** Only raw .NET publish was implemented.
- **Recommended remediation:** Add deterministic bundle assembly, plist/icon/entitlements, macOS CI launch tests, signing, notarization and Gatekeeper verification.
- **Blocker:** Blocks all macOS distribution; does not block Windows-only dogfooding.

### BB-QA-003 — Context sentence is extracted as a second task

- **Severity:** Medium
- **Subsystem:** Local AI task capture
- **Requirement violated:** Context must not become a standalone task.
- **Reproduction:** Submit `Finish my DECA presentation before Friday. It probably needs two focused sessions.` under review-first permission.
- **Expected:** One task, with deadline Friday and duration/session context.
- **Actual:** Two drafts: `Finish my DECA presentation` and `It probably needs two focused sessions`.
- **Evidence:** Live Chat review UI and matching phase-seven screenshot; `LocalHeuristicAiProvider.SplitSentences` evaluates each sentence independently.
- **Frequency:** 100% for exact sentence.
- **Likely cause:** `CleanTitle` strips `it probably needs` only when trailing in the same sentence, not when the pronoun-led sentence is split first.
- **Recommended remediation:** Add discourse/context classification before task creation and regression tests for pronoun/context sentences. In auto mode, reject low-actionability fragments.
- **Blocker:** Not a hard blocker in review mode; pollutes Inbox in auto-add mode.

### BB-QA-004 — Unavailable data-directory path exits without recovery UI

- **Severity:** Medium
- **Subsystem:** Startup/storage
- **Requirement violated:** Storage failures must surface safely.
- **Reproduction:** Set `BEBOOSTED_DATA_DIR` to an existing disposable file and launch.
- **Expected:** Startup error window explaining the data path cannot be created/opened.
- **Actual:** Process exited before showing a window.
- **Evidence:** PID 31276 exited with no main window; `paths.EnsureDirectoriesExist()` occurs outside `App.OnFrameworkInitializationCompleted`'s migration `try` block.
- **Frequency:** 100% for tested unavailable path.
- **Likely cause:** Only migration/database setup is protected by error UI.
- **Recommended remediation:** Wrap path, logging, DI and migration initialization in one recoverable startup boundary; fall back to a logger-independent error window.
- **Blocker:** Blocks affected users but not normal writable-profile launch.

### BB-QA-005 — Document picker accepts unsupported file types

- **Severity:** Medium
- **Subsystem:** Project Files/import
- **Requirement violated:** Invalid file type handling and safe resource classification.
- **Reproduction:** Choose Add document, type the full path to an existing `.bin` file, click Open.
- **Expected:** Rejection with a clear validation message.
- **Actual:** Imported, copied and Indexed as a Document; UI showed `BIN`/Uploaded.
- **Evidence:** Live native picker; `ProjectsView` has only a picker pattern filter; `FileDetailViewModel.Import` and `ProjectService.ImportFile` do not validate extension/MIME/content.
- **Frequency:** 100% for tested `.bin` path.
- **Likely cause:** Picker filter treated as enforcement.
- **Recommended remediation:** Validate extension and preferably signature/MIME in Application/Infrastructure, return per-file errors, and test picker-bypass paths.
- **Blocker:** Does not block basic use; should be fixed before public release.

### BB-QA-006 — Focus is not restored after Inbox closes

- **Severity:** Medium
- **Subsystem:** Accessibility/shell overlays
- **Requirement violated:** Focus returns to invoking control after temporary surfaces.
- **Reproduction:** Focus Inbox, open drawer, then activate Close Inbox or press Escape.
- **Expected:** Focus returns to Inbox (or the previously focused calendar control).
- **Actual:** UI Automation found zero focused elements inside the app.
- **Evidence:** Controlled foreground UIA test; `MainWindow.axaml.cs` explicitly focuses capture on open but has no corresponding close restoration.
- **Frequency:** 100% in tested close paths.
- **Likely cause:** One-way open-focus handler only.
- **Recommended remediation:** Store the invoking element/focus scope, restore on close, and add overlay focus lifecycle tests.
- **Blocker:** Accessibility release issue; mouse users can continue.

### BB-QA-007 — Task editor has no Cancel action

- **Severity:** Medium
- **Subsystem:** Inbox task edit
- **Requirement violated:** Edit cancellation.
- **Reproduction:** Open Edit for a task and inspect actions/dismiss behavior.
- **Expected:** Explicit Cancel that discards pending changes and returns focus.
- **Actual:** Only Save and Delete are bound. Generated `ResetEditCommand` is not wired to the view.
- **Evidence:** `InboxDrawerView.axaml`, `InboxDrawerView.axaml.cs`, `TaskRowViewModel` static inspection.
- **Frequency:** 100%.
- **Likely cause:** Reset behavior implemented in ViewModel but omitted in XAML/flyout lifecycle.
- **Recommended remediation:** Add Cancel binding, reset on flyout dismissal, and tests for no persisted mutation/focus return.
- **Blocker:** Does not block capture; incomplete required workflow.

### BB-QA-008 — Inbox batch selection is absent

- **Severity:** Medium
- **Subsystem:** Inbox/planning
- **Requirement violated:** Batch selection and related planning action.
- **Reproduction:** Open an Inbox with multiple tasks and inspect task rows/actions.
- **Expected:** Select multiple tasks for batch Plan/Priority Sort scope.
- **Actual:** No selection checkbox, selection model or batch count; Plan/Sort operate on all eligible tasks.
- **Evidence:** Live Inbox and XAML/ViewModel inspection.
- **Frequency:** 100%.
- **Likely cause:** Batch-selection design was not implemented.
- **Recommended remediation:** Add accessible multi-select state, select-all/clear, scoped commands and persistence decision.
- **Blocker:** Workflow gap, not a basic-use blocker.

### BB-QA-009 — Task recurrence and scheduling constraints are not editable in UI

- **Severity:** Medium
- **Subsystem:** Tasks/Inbox
- **Requirement violated:** Optional user-editable recurrence, not-before, earliest/latest availability.
- **Reproduction:** Open task editor.
- **Expected:** Controls for supported task recurrence and constraints.
- **Actual:** UI exposes title/deadline/duration/project only, though persistence/domain fields exist.
- **Evidence:** `InboxDrawerView.axaml`, `TaskRowViewModel`, task migration/domain inspection.
- **Frequency:** 100%.
- **Likely cause:** Domain capability not carried through to Desktop.
- **Recommended remediation:** Add compact optional constraint editor with validation and recurrence preview; persist/reload tests.
- **Blocker:** Blocks advanced scheduling scenarios; not title-only capture.

### BB-QA-010 — Imported document content and exact page citations are not indexed

- **Severity:** Medium
- **Subsystem:** Files/search/AI Q&A
- **Requirement violated:** Source-grounded answers with exact file/page/note/link evidence.
- **Reproduction:** Import generated PDF containing a unique fact, ask about that fact from its Project.
- **Expected:** Retrieve document text and cite file/page.
- **Actual:** Index contains only resource title and original filename; byte text/page is unavailable. Notes and links do cite correctly.
- **Evidence:** Live PDF import plus `SimpleLocalIndexer` comment/implementation (`no byte-level text extraction in v1`).
- **Frequency:** 100% for PDF content.
- **Likely cause:** Placeholder metadata-only indexer for binary resources.
- **Recommended remediation:** Add local PDF/text extraction with page/chunk locators, index versioning, failure states and citation chips containing exact location.
- **Blocker:** Blocks the approved document-grounded Q&A depth; does not block note/link Q&A.

### BB-QA-011 — Stored resource path resolution lacks containment validation

- **Severity:** Low
- **Subsystem:** Resource storage/security hardening
- **Requirement violated:** Path traversal prevention.
- **Reproduction:** Static inspection or tamper a disposable DB `stored_path` to contain traversal components, then resolve/open/delete it.
- **Expected:** Canonical path must remain under `ResourcesDirectory`.
- **Actual:** `Path.Combine(resourcesDirectory, storedPath)` is used without full-path containment checking.
- **Evidence:** `LocalResourceStorage.ResolvePath`.
- **Frequency:** Normal UI writes safe GUID names; exploitable only through corrupted/tampered persistence or future caller misuse.
- **Likely cause:** Trust in internal generated paths.
- **Recommended remediation:** Canonicalize, reject rooted/traversal paths and assert the result starts with the canonical resource root plus separator.
- **Blocker:** Defense-in-depth; not normal-use blocker.

### BB-QA-012 — Very short calendar blocks clip/collide visually

- **Severity:** Low
- **Subsystem:** Calendar visual layout
- **Requirement violated:** Readable very-short blocks without overlap.
- **Reproduction:** Render plan containing a 10-minute task adjacent to another block at 1280×800 or 1440×960.
- **Expected:** Compact readable representation or non-overlapping abbreviated label.
- **Actual:** Minimum visual height causes text clipping and apparent collision with the adjacent block.
- **Evidence:** Fresh images exactly matching `plan-draft-*` and `plan-approved-undo-toast-*`; implementation report acknowledged the minimum-height tradeoff.
- **Frequency:** Consistent for very short blocks.
- **Likely cause:** Minimum card height exceeds time-proportional geometry.
- **Recommended remediation:** Use short-block label lane/tooltip/popover and collision-aware layout.
- **Blocker:** Visual quality issue only.

### BB-QA-013 — Week task text clips in narrow 1280 columns

- **Severity:** Low
- **Subsystem:** Week visual layout
- **Requirement violated:** Readability at supported 1280×800 size.
- **Reproduction:** Open/render a busy Week at 1280×800.
- **Expected:** Predictable truncation/wrapping with details available.
- **Actual:** Titles/metadata are visibly clipped in narrow day columns.
- **Evidence:** Fresh `shell-calendar-week-1280x800.png`, exact match to phase-seven baseline.
- **Frequency:** Busy Week/narrow window.
- **Likely cause:** Fixed metadata density exceeds column width.
- **Recommended remediation:** Responsive compact card variant and accessible tooltip/details flyout.
- **Blocker:** Visual issue, not functional blocker.

### BB-QA-014 — Empty Projects message is not centered in usable content area

- **Severity:** Low
- **Subsystem:** Projects empty state
- **Requirement violated:** Approved empty-state alignment/fidelity.
- **Reproduction:** Open Projects on a profile with no projects.
- **Expected:** `No projects yet` group centered horizontally and vertically below the header.
- **Actual:** Empty-state group is not reliably centered in the remaining surface.
- **Evidence:** User report and live empty state; in `ProjectsView.axaml` the empty StackPanel is a non-final, undocked child of a `DockPanel`, followed by the projects ScrollViewer.
- **Frequency:** 100% in empty state on reported/tested layout.
- **Likely cause:** `DockPanel` child ordering/LastChildFill semantics conflict with center alignment.
- **Recommended remediation:** Use a Grid for mutually exclusive empty/list content or make one fill container and center inside it; add both-size empty screenshots.
- **Blocker:** Visual polish only.

## Existing known issues confirmed

- Metadata-only PDF/image indexing and lack of page citations (BB-QA-010) was documented as a v1 limitation.
- Short-block minimum-height visual clipping (BB-QA-012) was acknowledged in the implementation report.
- macOS raw-publish-only status and required packaging/signing work (BB-QA-015) were documented accurately.

## New issues discovered

- Full timeline unavailable outside 06:00–23:00 (user-reported, independently traced).
- High-DPI window/composer/taskbar cutoff (user-reported, independently measured).
- Projects empty-state centering (user-reported, layout cause identified).
- AI context sentence extracted as its own task.
- Silent exit for unavailable data-root path.
- Unsupported extension bypasses document picker filter.
- Inbox focus restoration failure.
- Task Cancel, batch selection, recurrence/constraints UI gaps.
- Resource-path containment hardening gap.
- 1280 Week text clipping.

## Release blockers

Windows alpha/public:

1. BB-QA-002 — bottom-edge controls can be inaccessible on a common high-DPI setup.
2. BB-QA-001 — users cannot view or schedule seven hours of the day.

Public-release quality gates after the High fixes:

3. BB-QA-003, 005, 006, 007 and 010.
4. Add native regression coverage for window work areas/DPI, full-day timeline and empty Projects layout.

macOS:

5. BB-QA-015 plus execution on real macOS, signing/notarization, Gatekeeper, Retina, keyboard, Finder and VoiceOver validation.

## Recommended fix order

1. Clamp/recover window geometry to the Windows work area and add DPI/taskbar matrix tests.
2. Separate timeline initial scroll position from a full 00:00–24:00 render/manipulation range.
3. Correct AI context extraction before auto-add can create noise.
4. Move all startup initialization into one safe error boundary.
5. Enforce import type/content rules below the picker and harden stored-path containment.
6. Complete task Cancel, constraints/recurrence and batch selection.
7. Restore focus for every overlay/flyout/dialog close path.
8. Implement local document text/page indexing and exact citations.
9. Fix responsive short/Week cards and center the Projects empty state.
10. Build and validate the macOS app-bundle/signing/notarization pipeline.

## Final acceptance checklist

- [x] Authoritative sources read completely.
- [x] Initial Git state recorded and user changes preserved.
- [x] Restore, format, warning-as-error build and tests executed fresh.
- [x] Three full test-suite runs completed without flakiness.
- [x] Skipped screenshot test explained and executed with its environment gate enabled.
- [x] Development app exercised with disposable profiles only.
- [x] Empty, valid restart, unavailable path and corrupted database paths evaluated.
- [x] Major shell, Inbox, calendar, Priority Sort, plan, Project/File, chat and Settings flows exercised live or explicitly labeled automated.
- [x] All 24 fresh screenshots produced, inspected and hash-compared at both sizes.
- [x] Local-first/network/log/package inspections completed.
- [x] Fresh and existing Windows packages smoke-tested.
- [x] macOS claims limited to Windows-verifiable compile/code evidence.
- [x] Large Inbox, busy calendar data and 1,500-resource File observed.
- [x] All BeBoosted processes started by the audit stopped before final verification.
- [ ] Windows alpha accepted — blocked by BB-QA-001/002.
- [ ] Windows public release accepted — blocked by High and selected Medium defects.
- [ ] macOS distribution accepted — blocked by BB-QA-015 and lack of Mac execution.

## Reproduction commands

Use only a new disposable data root for every runtime session:

```powershell
$qaData = Join-Path $env:TEMP ("BeBoosted-QA-" + [guid]::NewGuid())
$env:BEBOOSTED_DATA_DIR = $qaData
dotnet run --project src\BeBoosted.Desktop\BeBoosted.Desktop.csproj
```

Build/test baseline:

```powershell
dotnet --info
dotnet restore BeBoosted.slnx
dotnet format BeBoosted.slnx --verify-no-changes
dotnet build BeBoosted.slnx -warnaserror --no-restore
dotnet test BeBoosted.slnx --no-restore
```

Screenshot capture without touching committed baselines:

```powershell
$env:BEBOOSTED_DATA_DIR = Join-Path $env:TEMP ("BeBoosted-Shots-Data-" + [guid]::NewGuid())
$env:BEBOOSTED_SCREENSHOT_DIR = Join-Path $env:TEMP ("BeBoosted-Shots-" + [guid]::NewGuid())
dotnet test tests\BeBoosted.Desktop.Tests\BeBoosted.Desktop.Tests.csproj `
  --filter "FullyQualifiedName~ScreenshotCaptureTests.CaptureShellScreens"
```

Windows package:

```powershell
$publishOut = Join-Path $env:TEMP ("BeBoosted-Publish-" + [guid]::NewGuid())
dotnet publish src\BeBoosted.Desktop\BeBoosted.Desktop.csproj `
  -c Release -r win-x64 --self-contained true -o $publishOut
$env:BEBOOSTED_DATA_DIR = Join-Path $env:TEMP ("BeBoosted-Package-Data-" + [guid]::NewGuid())
& (Join-Path $publishOut "BeBoosted.exe")
```

Targeted user-reported cases:

1. Week/Today range: scroll to both extremes and try to place blocks at 05:30 and 23:30.
2. Bottom cutoff: use 200% scaling, launch default 1440×960-DIP window, and compare its client bottom with the monitor work-area bottom.
3. Projects centering: launch with a new empty profile, navigate to Projects, and inspect the empty state at 1440×960 and 1280×800.
4. AI context extraction: paste the exact DECA/two-sessions sentence under review-first mode.
5. Import validation: type a full `.bin` path into the Add document picker.

---

This report records diagnosis only. No application defect was fixed during the audit.

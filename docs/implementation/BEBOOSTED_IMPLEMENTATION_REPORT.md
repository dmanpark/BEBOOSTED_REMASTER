# BeBoosted Implementation Report

Maintained incrementally: each phase's record is appended immediately after its
verification gate passes. Sections marked *(in progress)* are completed at final handoff.

## Executive summary *(in progress — updated per phase)*

- **Implemented so far:** Phase 1 foundation — solution architecture, design tokens,
  bundled fonts, application shell with rail navigation, Inbox drawer shell, collapsed
  composer, SQLite persistence with migrations, settings + window/view restoration,
  structured logging, and a 48-test suite including headless UI tests.
- **Current usability:** the application launches on Windows, restores its window and
  last-used calendar view, and navigates between Calendar/Projects/Settings. Planning
  features arrive in Phases 2–7.
- **Windows status:** builds, tests, launches verified on Windows 11 (.NET 10.0.110 SDK).
- **macOS status:** code kept cross-platform behind interfaces; no macOS execution yet.
- **Important limitations:** calendar timeline, tasks, Priority Sort, plans, projects,
  and AI are not yet implemented (scheduled phases).

## Source authority

- **Claude Design project:** `fd7f9935-dc5e-4a27-9ca9-72361a41bccf` — file
  `BeBoosted Remaster.dc.html` (frames 01–10: Today, Week+Inbox, Priority Sort, Plan
  draft, Project, Project File, AI task review, AI permissions, component sheet,
  flow/design notes). `support.js` confirmed as design-doc runtime only; nothing from it
  is used in production.
- **Local specifications:** `docs/superpowers/specs/2026-08-11-beboosted-remaster-design.md`
  (approved spec), `docs/design/reference-designs.md`, `docs/design/claude-ui-mockup-prompt.md`.
- **Source priority:** implementation prompt > approved spec > Claude Design mockups >
  reference designs. No material contradictions found; ambiguities were resolved by the
  approved Phase 0 defaults (fixed local commitments in v1, Inbox as drawer, estimated
  Priority Sort progress, 15/5-minute snapping, 10s undo, user-resolved conflicts,
  custom ranges reuse the week engine).
- **Documented mockup deviations:** 11px minimum type size (mockups use 8–10px);
  Ctrl-based shortcuts on Windows (mockups show ⌘); bundled fonts instead of Google
  Fonts; no hard-coded mockup coordinates — layout is computed.

## Architecture

### Solution tree

```
BeBoosted.slnx
├─ src/BeBoosted.Domain            pure domain (no dependencies)
├─ src/BeBoosted.Application       use cases + ports (IAppDataPaths, ISettingsStore, IClock, …)
├─ src/BeBoosted.Infrastructure    SQLite (connection factory, migration runner, embedded
│                                  migrations, settings store), DefaultAppDataPaths, SystemClock
├─ src/BeBoosted.Desktop           Avalonia 12 UI: views, ViewModels, styles/tokens, platform
│                                  services (keymap, window state), composition root
├─ tests/BeBoosted.Tests           domain/application/infrastructure tests (file-based SQLite)
└─ tests/BeBoosted.Desktop.Tests   ViewModel tests + Avalonia headless UI/screenshot tests
```

### Dependency graph

`Desktop → Application → Domain`; `Infrastructure → Application → Domain`;
Desktop references Infrastructure **only** in the composition root (`App.axaml.cs`).
Domain depends on nothing.

### Important interfaces

| Interface | Layer | Purpose |
|---|---|---|
| `IAppDataPaths` | Application | Platform data/logs/resources locations; `BEBOOSTED_DATA_DIR` override for clean-profile testing |
| `ISettingsStore` | Application | Synchronous local key/value settings (SQLite-backed) |
| `IClock` | Application | Deterministic time source for tests |
| `IKeymapService` | Desktop.Platform | Ctrl vs ⌘ gestures and display strings |

### Persistence architecture

Single SQLite file `beboosted.db` (WAL, foreign keys on) under the app-data root.
Forward-only SQL migrations embedded as `Persistence/Migrations/NNNN_name.sql`, applied
in version order by `MigrationRunner`, each in its own transaction, recorded in
`schema_migrations`. Migration failure at startup shows a dedicated error window instead
of crashing.

### Calendar-layout and AI architecture

Arrive in Phases 3 and 7 respectively; sections will be completed then.

## Design system implementation

- **Colors:** six spec tokens plus formalized extended tokens (fixed-event cream gray
  `#ECE9DC`, folio underlay `#F6F3E4`, ink-hover `#5A6B1E`, project accents umber/slate,
  graphite alpha ramp 8–55%, lime halo) in `Styles/Tokens.axaml`.
- **Typography:** Instrument Sans (UI), IBM Plex Mono (times/metadata), Newsreader 14pt
  optical (File titles) — bundled as static TTFs with their OFL 1.1 licenses in
  `Assets/Fonts/**`. A headless test asserts every design weight (400/500/600/700)
  resolves to a real glyph typeface, guarding the GDI per-weight family-name pitfall.
- **Type ramp:** 11px floor for metadata (mockups used 8–10px), body 13, headings 15/22,
  prompt 27.
- **Geometry/spacing:** 4px grid tokens, radii 4–10 (no pills), rules at graphite alpha
  levels, shadows reserved for drawers/popovers/Files.
- **Focus:** graphite border + lime halo on `:focus-visible` in all custom templates.
- **Accessibility deviations from mockups:** larger minimum text; automation names on
  icon-only controls; real RadioButton semantics for rail and segmented switch.

---

## Phase 1 — Foundation

### Intended scope
Solution structure, build/test infrastructure, design tokens and bundled fonts,
application shell with navigation, settings persistence, local database and migrations.

### Work completed
- Solution (`BeBoosted.slnx`), six projects, `Directory.Build.props`
  (net10.0, nullable, warnings-as-errors, compiled bindings by default),
  `Directory.Packages.props` (central pinning), `global.json` (SDK 10.0.100 latestFeature),
  `.editorconfig`, `.gitignore`.
- Design tokens, typography, icon geometry, and control styles (custom templates for
  Button, rail toggles, segmented switch) — `src/BeBoosted.Desktop/Styles/*.axaml`.
- Bundled fonts: Instrument Sans (Regular/Medium/SemiBold/Bold, upstream repo statics),
  IBM Plex Mono (Regular/Medium/SemiBold, google/fonts statics), Newsreader 14pt
  (Regular/Medium/SemiBold, Google Fonts static instances) + OFL.txt per family.
- Application shell: 52px icon rail (brandmark, Calendar/Inbox/Projects, Settings at
  bottom), per-section top bars, date navigation with Monday-start week ranges,
  Today/Week segmented switch, collapsed composer visual, floating Inbox drawer shell
  with empty state, Escape-to-close.
- Persistence: `SqliteConnectionFactory` (WAL/FK), `MigrationRunner` +
  `EmbeddedMigrations`, migration `0001_initial` (settings table, STRICT),
  `SqliteSettingsStore`, `AppSettings` facade, last-used-view persistence,
  `WindowStateService` (size/position/maximized with off-screen guard).
- Composition: Serilog (rolling file in app-data logs + debug sink), M.E.DI container,
  startup migration with error window fallback, clean shutdown flushing.

### Architecture and behavior decisions
- `ISettingsStore` is synchronous by design (local SQLite; needed before the UI loop).
- The Inbox rail control is a ToggleButton (drawer), not a RadioButton section.
- The composer is an inert visual until Phase 7 (no shortcut chip shown yet, so no
  keyboard affordance is advertised that does nothing).
- `BeBoosted.Domain` is intentionally empty in Phase 1; first domain types land with
  Phase 2 to avoid speculative modeling.
- Avalonia 12 note: DevTools moved to `AvaloniaUI.DiagnosticsSupport` (Debug-only).

### Tests added (48 total; 21 + 27)
Migration order/idempotence/duplicate detection/rollback-on-failure, embedded migration
loading, settings round-trip incl. file reopen, WindowPlacement parsing, AppSettings
defaults/corrupt values, ShellViewModel navigation + drawer, CalendarViewModel
persistence/navigation/week-range/headers (en-US pinned), font weight resolution
(10 cases), shell smoke (rail/composer/section swap/drawer), screenshot capture
(skipped unless `BEBOOSTED_SCREENSHOT_DIR` is set).

### Verification
```
dotnet format BeBoosted.slnx --verify-no-changes   # clean
dotnet build BeBoosted.slnx -warnaserror           # 0 warnings, 0 errors
dotnet test BeBoosted.slnx                         # 21 passed; 27 passed (1 skipped-by-design)
# real launch, clean profile:
BEBOOSTED_DATA_DIR=<temp> BeBoosted.exe            # alive after 6s; db + WAL + logs created;
                                                   # "Applied migration 1 initial" logged
```

### Screenshots
`docs/implementation/screenshots/phase1/` — shell (Today, Week, Inbox drawer, Settings)
at 1440×960 and 1280×800. Compared against Frame 01/02 chrome: rail, top bar, segmented
switch, composer, and drawer match the design language; calendar canvas is an explicit
empty state until Phase 3.

### Problems discovered during self-review
- `Application` namespace collision with `Avalonia.Application` → qualified base class.
- `Bitmap.Save(string)` obsolete in Avalonia 12 → `PngBitmapEncoderOptions` overload.
- Static font instances carry GDI per-weight family names (id1); Skia resolves via
  typographic family (id16) — verified by dedicated tests rather than assumed.

### Known remaining limitations
- Calendar/Projects surfaces are explicit empty states pending Phases 3/6.
- A runtime settings-write failure (e.g. disk full) would surface as an unhandled
  exception; acceptable for now, revisit with a global exception handler (tracked).
- Window position restore uses logical size + pixel position; per-monitor DPI mixes are
  approximated (screen-intersection guard prevents off-screen windows).

### Local commit
`a7af431` — phase 1: establish Avalonia foundation

---

## Phase 2 — Tasks and Inbox

### Intended scope
Task domain behavior, repository, universal Inbox, capture and editing, completion outcomes.

### Work completed
- **Domain** (`BeBoosted.Domain`): strongly-typed IDs for every core entity (`TaskId`,
  `ProjectId`, `CalendarBlockId`, `PlanningProposalId`, `ComparisonId`, `AiProvenanceId`, …),
  `DomainException`, `TaskItem` entity (validated title, positive duration, deadline,
  project link, constraints, recurrence, origin, provenance slot, completion state,
  created/modified stamps; `Complete`/`Reopen` idempotent; `RecordNeedsMoreTime` replaces
  the estimate and keeps the task open), `SchedulingConstraints` value object,
  `RecurrenceRule` (daily/weekly with interval + weekday set, `OccursOn` expansion —
  deliberately *not* a habit system).
- **Application**: `ITaskRepository` port (synchronous local persistence by design),
  `TaskService` use cases (capture, update details, complete/reopen, needs-more-time,
  delete) with clock injection.
- **Infrastructure**: migration `0002_tasks` (STRICT table + open-tasks index),
  `SqliteTaskRepository` (full field mapping, invariant-culture ISO storage),
  `RecurrenceSerializer` ("D:n" / "W:n:MO,FR").
- **Desktop**: Inbox drawer becomes real — fast capture (plus Enter), task rows with
  completion circle, title, mono metadata ("Fri · 1 h 30 min", relative deadline names),
  edit flyout (title/deadline/duration/delete), empty state, open-count badge on the rail
  icon and drawer header, capture box auto-focus on open. Fluent accent recolored to
  graphite; text selection lime.

### Architecture and behavior decisions
- Task scheduling state is derived from calendar blocks (Phase 3), never stored on the task.
- Completed tasks leave the Inbox (it is a queue of unscheduled work, not an archive).
- Batch selection + "Plan…" footer intentionally deferred to Phase 4 (Priority Sort) so no
  dead controls ship; mock frame 02's selected-row styling will be reused then.
- Deadlines are date-level (`DateOnly`) per the design frames; durations stored as minutes.

### Tests added (31 new; totals 43 + 36)
TaskItem (validation, idempotent completion, needs-more-time boundaries), RecurrenceRule
(daily/weekly/biweekly occurrence math, invalid rules), SchedulingConstraints,
RecurrenceSerializer round-trips, SqliteTaskRepository (full-field round-trip, update,
missing-row update failure, inbox ordering/exclusion, delete), InboxViewModel (load,
capture/clear, blank input, complete/delete persistence, edit commit incl. blank-title
revert, relative metadata), headless drawer rows + live capture.

### Verification
```
dotnet format --verify-no-changes   # clean
dotnet build -warnaserror           # 0 warnings
dotnet test                         # 43 + 35 passed (1 screenshot skip by design)
# launch smoke on clean profile     # alive, db migrated to version 2
```

### Screenshots
`docs/implementation/screenshots/phase2/` — Inbox drawer populated with the frame-02 task
set at 1440×960 and 1280×800; capture field focus is graphite (accent override verified).

### Problems discovered during self-review
- Avalonia 12 renamed `Watermark` → `PlaceholderText` (build error surfaced it).
- `AvaloniaPropertyChangedEventArgs.GetNewValue<T>` no longer generic — used `NewValue is true`.
- Fluent's blue accent leaked into TextBox focus — repainted `SystemAccentColor*` to
  graphite tones and set lime selection brushes.

### Known remaining limitations
- Edit affordance (pencil) is always visible rather than hover-revealed; acceptable, may
  tighten with the calendar-phase polish pass.
- Capture accepts a bare title only; structured quick-parse belongs to the AI phase.

### Local commit
`9c37281` — phase 2: implement task inbox

---

## Phase 3 — Calendar

### Intended scope
Timeline layout engine, Today and Week views, local calendar blocks (including fixed and
recurring commitments), drag, resize, conflicts, and keyboard movement.

### Work completed
- **Domain:** `CalendarBlock` (fixed commitments with own titles + optional weekly/daily
  recurrence; task blocks; wall-clock date+times that never cross midnight; reserved
  provider/external-id/sync-state fields; outcome recording restricted to task blocks;
  external events refuse edits), `BlockOccurrence`, `ConflictDetector` (same-date overlap
  scan; touching blocks don't conflict).
- **Application:** `ICalendarBlockRepository`, `CalendarService` (fixed commitments,
  `ScheduleTask` with estimate-or-30-min default, move/resize with midnight clamping,
  `RecordOutcome` — Done completes the task, Needs-more-time re-estimates and returns it
  to the Inbox, Didn't-happen leaves it open; recurrence expansion in `GetOccurrences`;
  elapsed-without-outcome query), `InboxQueryService` (Inbox = open tasks with no pending
  block — elapsed-unresolved blocks route through the review notice, never auto-complete).
- **Infrastructure:** migration `0003_calendar_blocks` (STRICT, task FK with cascade,
  date/task indexes), `SqliteCalendarBlockRepository` with candidate-range, elapsed, and
  pending-task queries.
- **Layout engine (pure, no Avalonia):** `TimelineGeometry` (time↔Y, snap+clamp,
  hour marks) and `OverlapLayout` (cluster detection, greedy column reuse).
- **Desktop:** `TimelinePanel` (custom panel arranging blocks by attached time props with
  live re-arrange during drags), `TimelineDecorations` (hour rules, current-time
  indicator with lime dot, dashed lime drop-preview slot with mono time label),
  `TimeGutter`, `HatchOverlay` (conflict hatching), `TimelineSurfaceView` (shared
  Today/Week surface: initial scroll, drop-from-Inbox with snapped preview, focus
  restoration), `CalendarBlockView` (state-classed visuals: cream/locked fixed, paper
  task block with accent edge, done strikethrough, conflict hatch + emphasis; pointer
  drag with 15-min snap / Alt = 5-min; bottom-grip resize; keyboard: ↑/↓ move,
  Shift+↑/↓ resize, ←/→ change day, Enter/Space outcome menu, Delete unschedule),
  outcome flyout (Done / Needs more time with remaining minutes / Didn't happen /
  Remove), review-notice bar with per-block resolution, "New commitment" editor
  (date, start/end, weekly weekday toggles, validation errors), capacity summary in the
  top bar, non-destructive 60-second current-time refresh, Inbox rows as drag sources.

### Architecture and behavior decisions
- Proposals (Phase 5) will render from PlanningProposal state, not calendar_blocks —
  the calendar store holds only approved/fixed items, matching the spec.
- Recurring fixed commitments edit as a series and are locked on the surface; task
  blocks never recur (recurring *tasks* spawn instances later, not habit UI).
- Visible range 6:00–23:00 at a constant 56px hour height with scrolling at both target
  resolutions (the mockup's 48px compact rows were unnecessary once scrolling exists).
- Time metadata renders "1 h 30 min" instead of the mockup's "90 min" for consistency
  with Inbox durations.
- Avalonia 12 changes handled: sealed `Panel.Render` (decorations split into a sibling
  control), `DataObject`→`DataTransfer` drag API, `DoDragDropAsync` requiring the press
  args, class-handler replacement for `AffectsParentArrange`.

### Tests added (53 new; totals 67 + 65)
CalendarBlock rules (validation, outcomes, external lock, recurrence expansion, rename),
ConflictDetector (overlap/touch/cross-day/chains), CalendarService (schedule defaults,
move/resize persistence, all three outcomes incl. Inbox round-trip, weekly recurrence
expansion, elapsed-needing-outcome boundary), SqliteCalendarBlockRepository (full-field
round-trip, outcome update, candidate ranges, elapsed boundary at end==now, pending task
ids, task-delete cascade), TimelineGeometry (linear mapping, snap/clamp, marks),
OverlapLayout (disjoint/touching/pair/chain-reuse/triple/cluster-independence),
CalendarViewModel (day counts, block placement incl. recurrence, conflicts, DataChanged,
review notice lifecycle, capacity summary, commitment editor validation+creation,
keyboard move/resize/day ops, non-destructive RefreshNow), headless seeded-calendar
render (5 Today blocks, 12 Week occurrences, fixed-block lock).

### Verification
```
dotnet format --verify-no-changes   # clean (after one auto-format pass)
dotnet build -warnaserror           # 0 warnings
dotnet test                         # 67 + 64 passed (1 screenshot skip by design)
# clean-profile launch              # alive; migrations 1–3 applied
```

### Screenshots
`docs/implementation/screenshots/phase3/` — Today and Week with the frame-01/02 content
at 1440×960 and 1280×800. Matches the design's calendar state language: cream locked
commitments with lock glyphs, paper task blocks with slate accent edge and completion
circles, lime current-time dot, lime TUE 11 chip, mono gutter.

### Problems discovered during self-review
- `Panel.Render` is sealed in Avalonia 12 → introduced `TimelineDecorations`.
- Avalonia 12's new DataTransfer drag API (typed `DataFormat<string>`, `DoDragDropAsync`).
- Timer-driven reload would have closed open flyouts — `RefreshNow` now mutates
  `NowMinutes` in place.

### Known remaining limitations
- Pointer drags commit on release with live snapping; cross-day drag shows a snapped
  column ghost rather than full free-floating preview.
- Recurring commitments have no per-occurrence exceptions (series-level edits only).
- The mockup's wide right margin on the Today lane is not reproduced; blocks span the
  lane (consistent with Week).

### Local commit
`a20e7ed` — phase 3: implement calendar engine

---

## Phase 4 — Priority Sort

### Intended scope
Adaptive pairwise comparisons, real ties, Today/Week-scoped ordinal ranks, planning
tiers, and the complete accessible comparison UI.

### Work completed
- **Domain:** `PlanningPeriod` (Today anchored to a date / Week anchored to Monday, with
  stable persistence keys), `ComparisonDecision` + `ComparisonResult` (Tie is a first-class
  answer), `PriorityRank` + `PlanningTier`, and `ComparisonSession` — the adaptive
  algorithm. Tasks are placed one at a time by binary insertion among already-ordered
  tie-groups, so each question is maximally informative (~n·log n total). The session is a
  pure function of (candidates, answer log): undo pops the log and replays, which makes
  undo trivially correct and the whole flow deterministic. `BuildRanking()` works at any
  time (Build my plan now): dense ordinal ranks (1, 2, 2, 3), unplaced tasks share the
  trailing ordinal, tiers split into thirds without ever splitting a tied group. Progress
  is an explicit estimate of remaining informative questions — no fabricated scores.
- **Application:** `IPrioritizationRepository`, `PrioritySortService` (session start,
  completion persisting decisions + replacing the period's ranks, rank lookups).
- **Infrastructure:** migration `0004_prioritization` (comparisons history + per-period
  ranks), `SqlitePrioritizationRepository` with transactional rank replacement.
- **Desktop:** full-screen `PrioritySortView` overlay (no rail — the mockup's focused
  frame): centered period prompt, two large card buttons with lime-wash Due chips and
  mono metadata, "Too tough to decide [T]", "Build my plan now", Back (undo), status
  label ("Priority Sort · This week · Comparison 3"), 3px lime estimated-completeness
  strip, keyboard hints, and full keyboard support (←/→/T/Backspace/Esc). Results stage
  shows Protect now / Advance next / Can wait groups with lime ordinal chips and a
  "period only" note. Shell integration: drawer footer "Priority Sort" (enabled at ≥2
  tasks), period follows the visible calendar view, Escape closes sort before drawer,
  rank chips appear on Inbox rows and refresh with the view.

### Architecture and behavior decisions
- Adaptive selection = binary insertion between uncertain neighbors; this satisfies the
  informativeness requirement with a deterministic, fully testable core (no randomness).
- Decisions and ranks persist when a session finishes (including early exit); an
  abandoned session (Esc) writes nothing.
- The progress strip is labeled an estimate; the only numeric claim shown is the
  comparison count.
- Fluent state styles leak into replaced templates through the `PART_ContentPresenter`
  name — presenter renamed to `PART_ButtonContent` (fixed a gray disabled artifact).

### Tests added (25 new; totals 82 + 75)
ComparisonSession (single question for two tasks, tie sharing + dense ranks, immediate
tie continuation, undo restoring the exact question + pure-replay equivalence, undo at
zero, best-effort early ranking, tier thirds incl. all-tied, progress bounds + completion,
n·log n comparison budget, deterministic question sequences, single/empty candidate
edges, period key scoping), SqlitePrioritizationRepository (per-period decisions,
atomic rank replacement, period independence), PrioritySortViewModel (cards with
relative deadlines, tie advance, completion persistence + result groups, early exit,
undo, close-without-save), Shell integration (two-task gate, period from view, rank
chips after sort, Escape layering).

### Verification
```
dotnet format --verify-no-changes   # clean
dotnet build -warnaserror           # 0 warnings
dotnet test                         # 82 + 74 passed (1 screenshot skip by design)
# clean-profile launch              # migration 4 applied
```

### Screenshots
`docs/implementation/screenshots/phase4/` — comparison stage and results stage at both
resolutions, plus refreshed shell screens. Matches frame 03's composition: focused cream
surface, two paper cards, lime Due chips, tie button with T chip, progress strip.

### Problems discovered during self-review
- Two hand-derived test scripts didn't match the algorithm's actual pivot sequence —
  corrected the scripts (the algorithm was right).
- Disabled Back button rendered with Fluent's gray fill (template name leak) — fixed.

### Known remaining limitations
- Inbox batch *selection* for sorting a subset arrives with Phase 5's Plan flow; sorting
  currently ranks the whole Inbox queue.
- Tier chips on the results screen share one lime-wash treatment (component sheet shows
  three intensities) — polish candidate.

### Local commit
`79d2487` — phase 4: implement Priority Sort

---

## Phase 5 — Plan drafts

### Intended scope
Scheduling service, proposal blocks, Why evidence, individual and batch approval,
undo, and replanning.

### Work completed
- **Domain:** `PlanningProposal` + `ProposedBlock` (pending/approved/removed statuses;
  a proposed block's id becomes the calendar block's id on approval, making approval
  traceable and undoable; drafts never mutate the approved calendar), `WhyEvidence`
  (deadline/duration/priority/availability/source — user-relevant facts, never
  chain-of-thought), and `DeterministicScheduler`: rank-then-deadline-then-capture
  ordering, first-fit into the 8:00–21:00 planning window, 15-minute snapping, never
  before "now" on today, task time-of-day/not-before constraints, sessions capped at
  90 minutes with tiny remainders folded in, partial-session rollback when a task can't
  fully fit, per-task unplaced reasons, and Why evidence generation (including
  "Rank #1 · above …" from Priority Sort results).
- **Application:** `IPlanningProposalRepository`, `PlanningService` (single active draft;
  creating a new draft discards the old; move/resize/remove pending blocks; per-block and
  batch approval materializing calendar blocks; `UndoApproval` deleting the created
  blocks and reverting the draft; discard).
- **Infrastructure:** migration `0005_planning` (proposals + blocks with evidence
  columns, cascade delete), `SqlitePlanningProposalRepository` (transactional full-state
  save, active-draft query).
- **Desktop:** proposals render as lime-wash blocks with dashed graphite outlines and a
  lime spark chip; drag/resize/keyboard movement reuse the existing interactions but
  route through the planning service; the spark chip opens a flyout with **Approve this
  block**, **Remove**, and the Why evidence trail (DEADLINE / DURATION / PRIORITY /
  OPEN TIME / SOURCE); floating **Plan draft** summary panel (counts, leftover note,
  Approve plan, Discard draft); 10-second graphite undo toast with a lime Undo button,
  plus session-level Ctrl+Z/⌘Z bound at the window; Inbox drawer footer gains the
  primary **Plan…** action; conflicts include proposals (hatched on both sides) and
  fixed events are never moved by planning.

### Architecture and behavior decisions
- One active draft at a time, matching "plan Today / plan this week" as a single flow.
- Conflict detection was generalized (`TimedItem`) so proposals participate without
  faking domain calendar blocks.
- The unplaced-reason note is transient (recreated with each draft); persisted state
  keeps only the draft itself.
- CalendarBlockViewModel now wraps either an approved block or a proposal behind one
  interface — the timeline surface and keyboard handling did not change.

### Tests added (31 new; totals 104 + 83)
DeterministicScheduler (session splitting incl. remainder folding, never-before-now with
snap, busy-slot first fit, rank ordering + evidence text, session labels without overlap,
deadline-driven unplaced reasons, time-of-day constraints, day overflow, determinism,
no proposal overlap), PlanningService on real SQLite (draft persistence with evidence
round-trip, draft replacement, move/resize/remove persistence, approval materialization
with shared ids and state transitions, batch approval emptying the Inbox queue, undo
restoring blocks/draft/Inbox, discard, unplaced reporting), plan-draft ViewModel flows
(draft creation on the calendar, batch approval with undo toast, Ctrl+Z restore,
individual approve/remove, keyboard nudge of proposals, proposal-over-fixed conflict
with the fixed event untouched, discard, rank-influenced ordering).

### Verification
```
dotnet format --verify-no-changes   # clean
dotnet build -warnaserror           # 0 warnings
dotnet test                         # 104 + 82 passed (1 screenshot skip by design)
# clean-profile launch              # migration 5 applied
# BeBoosted.Tests re-run 5× — stable after the TempDatabase fix below
```

### Screenshots
`docs/implementation/screenshots/phase5/` — plan draft over Today (lime dashed proposals
+ summary panel), approved plan with the undo toast, at 1440×960 and 1280×800.

### Problems discovered during self-review
- A parallel-test flake: `TempDatabase.Dispose` used the global
  `SqliteConnection.ClearAllPools()` (interfering across concurrently-running classes)
  and let `File.Delete` throw on a lagging WAL handle. Cleanup is now pool-scoped and
  best-effort; verified stable across five consecutive full runs.
- A test initially referenced a fixed "Lunch" block its own setup never seeded.

### Known remaining limitations
- Blocks shorter than ~20 minutes render at an 18px minimum height and can visually
  brush the next block without a real time overlap (cosmetic).
- The mockup's "Review on calendar" summary button is intentionally omitted — the
  calendar is already the review surface.

### Local commit
`bf2c919` — phase 5: implement plan drafts

---

## Phase 6 — Projects and Files

### Intended scope
Minimal Project view, File objects, resources, preview and provenance, local indexing
interfaces.

### Work completed
- **Domain:** `Project` (validated name, restrained accent from `ProjectPalette` —
  six muted colors assigned round-robin, always subordinate to lime), `ProjectFile`
  (title/optional description, flat by construction — no nesting exists in the model),
  `Resource` (document/link/note/image; stable ids; notes carry content, links a URL,
  documents/images an original file name + id-based stored path; `Pending/Indexed/Failed`
  index states; editing a note resets it to Pending for re-indexing).
- **Application:** `IProjectRepository` / `IProjectFileRepository` /
  `IResourceRepository` (including `SetIndexText` and project-scoped `SearchInProject` —
  the retrieval surface Phase 7's AI will be confined to), `IResourceStorage`
  (app-controlled byte store keyed by resource id), `IResourceIndexer` port,
  `ProjectService` (projects/Files/resources CRUD with byte cleanup, cascade delete that
  unlinks tasks, open/recently-completed task queries, upcoming pending blocks per
  project). `TaskService.UpdateDetails` now assigns projects.
- **Infrastructure:** migration `0006_projects` (cascading FKs, index-text column),
  the three SQLite repositories, `LocalResourceStorage`
  (`resources/<id>.<ext>`), `SimpleLocalIndexer` (notes index content, links URL,
  documents/images title+filename with byte verification; missing bytes → Failed).
- **Desktop:** Projects index (accent-chip cards, New project flyout); frame-05 project
  view (accent bar + name, OPEN TASKS with completion circles and mono metadata,
  struck-through recently-completed rows, UPCOMING BLOCKS with accent ticks, the tactile
  folio shelf — layered underlay, labeled tab, Newsreader titles, resource counts —
  New File flyout, disabled "Ask BeBoosted about this project" until Phase 7); frame-06
  File view (folio tab header, serif title, kind-chip resource list with source/date/
  index-state metadata, lime-wash selection, preview pane: serif note reading surface,
  link open-in-browser, image preview with open/reveal, document card with open/reveal,
  provenance section with an honest empty state, per-resource delete, Document/Link/
  Note/Image add actions with platform file pickers); Inbox rows now show project names
  and the edit flyout assigns projects; `IFileRevealService` platform port
  (Explorer/Finder conventions).

### Architecture and behavior decisions
- Brushes are created lazily in ViewModels (composition resources are UI-thread-bound;
  eager creation in plain unit tests poisoned the shared headless session — see below).
- Detail/File surfaces render through `ContentControl` DataTemplates so compiled
  bindings scope correctly across the DataContext switch.
- Document indexing is honest about v1 scope: title + file-name text only (no PDF text
  extraction); recorded as a known limitation.

### Tests added (16 new; totals 112 + 88)
ProjectService on real SQLite (palette rotation, File/resource round-trips incl. index
state, byte import to stable paths + delete cleanup, project-scoped search isolation,
cascade delete unlinking tasks, open/recent task split, upcoming-block filtering,
missing-bytes → Failed re-index), Projects ViewModel flows (create-project navigation,
File creation + breadcrumbs, resource add/select/count/delete with kind chips, project
task completion moving rows, Inbox project names + assignment via the edit flyout).

### Verification
```
dotnet format --verify-no-changes   # clean
dotnet build -warnaserror           # 0 warnings
dotnet test                         # 112 + 87 passed (1 screenshot skip by design)
# Desktop tests re-run 3× — stable after the brush-thread fix
# clean-profile launch              # migrations 1–6 applied
```

### Screenshots
`docs/implementation/screenshots/phase6/` — projects list, frame-05 project view
(College Admissions with two folio Files), frame-06 open File (Metric Proof with note
reading pane, Indexed chip, provenance section) at 1440×960 and 1280×800, produced by
driving the real creation flows.

### Problems discovered during self-review
- Creating `SolidColorBrush` eagerly in ViewModels made plain unit tests construct
  composition resources off the Avalonia session thread, intermittently failing an
  unrelated headless test's cleanup. All accent brushes are now lazy properties.
- Freshly-captured Inbox rows missed the project-choice list (only `Reload` passed it).

### Known remaining limitations
- Document indexing covers title/filename only (no byte-level text extraction in v1).
- Provenance shows an empty state until Phase 7 populates citations.
- Project rename/delete have service + tests but no UI affordance yet (management UI is
  a polish candidate; creation, the primary flow, is complete).

### Local commit
`e7bc35d` — phase 6: implement projects and files

---

## Phase 7 — AI

### Intended scope
Deterministic mock provider, review list, project-scoped retrieval, provider interface
for later real AI connections.

### Work completed
- **Domain:** `AiProvenance` (operation kind, exact source-resource ids, Needs-review
  state) and `AiAnswer` (persisted project answers keyed to their provenance).
- **Application:** `IAiProvider` port with the four typed operations (extract tasks,
  suggest metadata, produce planning input — planning itself reuses the deterministic
  scheduler — and project-scoped Q&A returning exact citations); `IAiProvenanceRepository`;
  `AiPermissionSettings` (task capture and calendar planning as *separate* persisted
  permissions, both defaulting to review-first); `AiService` (review-first extraction,
  `AcceptDrafts` creating AI-origin tasks with shared provenance, auto-add honoring the
  permission while always labeling and keeping provenance, `AskProjectAsync` persisting
  answers + citations, `InvalidateForResource` implementing `IProvenanceInvalidator`,
  `TaskNeedsReview`, and `GetDerivations` for the File provenance panel).
  `ProjectService` now invalidates provenance on resource delete and note edits.
- **Infrastructure:** migration `0007_ai` (provenance + source junction + answers),
  `SqliteAiProvenanceRepository`, and `LocalHeuristicAiProvider` — the deterministic
  local provider: honorific-aware sentence splitting, duration parsing ("45 min",
  "2 hours", "two focused sessions"), relative-deadline parsing (today/tomorrow/weekday),
  keyword-verb duration heuristics, project-name matching, and keyword retrieval strictly
  over the active project's index with a template answer citing at most three resources.
- **Desktop:** the composer is now real — collapsed strip with the platform shortcut
  chip (Ctrl+J/⌘J focuses it), Enter submits, and the frame-07 expanded panel overlays
  the current surface: user bubbles, assistant messages, the reviewable task list
  (lime-wash rows, "AI suggested" chips, in-place title editing, per-row dismiss,
  Add all N / Dismiss all, the settings note), citation chips that navigate to the exact
  resource in its File, plan requests ("plan my week") that draft on the calendar —
  or apply automatically under that separate permission, still labeled and Ctrl+Z-undoable.
  "Ask BeBoosted about this project" is live and scopes the composer; frame-08 Settings
  ships real radio groups for both permissions plus the disabled External events card;
  Inbox rows show **AI added** and **Needs review** chips; the File provenance panel lists
  "Used by …" / "Cited in …" with Needs-review flags.

### Architecture and behavior decisions
- Retrieval cannot leave the active project by construction: the provider's only search
  surface is `IResourceRepository.SearchInProject`.
- Answers persist so citations survive restarts and invalidation is retroactive.
- The deterministic extractor is intentionally naive (it can propose a noise row like
  "It probably needs two focused sessions") — the review-first workflow exists exactly
  to dismiss such rows, and the provider interface lets a real model replace it without
  touching any workflow.

### Tests added (35 new; totals 131 + 104)
LocalHeuristicAiProvider (frame-07-style extraction with titles/deadlines/durations,
explicit duration/deadline parsing tables, project-name matching, project-scoped
citations with a decoy project, no-match fallback, determinism), AiService on SQLite
(review-first default, shared provenance on accept, auto-add labeling, persisted answers
with exact citations, note-edit and resource-delete invalidation flowing to Needs review,
task-level review flags), Chat ViewModel (plan/question intent tables, review flow
end-to-end incl. edit + dismiss, auto-add path, project Q&A with citation navigation
to the selected resource, scope placeholder, unscoped-question guidance, plan drafting
and auto-apply with undo, Escape layering), Settings permissions independence +
persistence round-trip.

### Verification
```
dotnet format --verify-no-changes   # clean
dotnet build -warnaserror           # 0 warnings
dotnet test                         # 131 + 103 passed (1 screenshot skip by design)
# clean-profile launch              # migrations 1–7 applied
```

### Screenshots
`docs/implementation/screenshots/phase7/` — frame-07 chat review over the calendar and
frame-08 AI permissions, plus refreshed shell/plan/sort/project screens, both resolutions.

### Problems discovered during self-review
- The sentence splitter broke on "Ms. Rivera" (honorific periods) — fixed with a
  lookbehind; caught by the frame-07 message test.

### Known remaining limitations
- The heuristic provider does not link extracted tasks to mentioned resources (task
  provenance from extraction carries no sources), so Needs-review on tasks arises via
  answer-style provenance or future providers; the invalidation plumbing is fully tested.
- Chat history is session-only (not persisted) — answers and provenance are persisted.

### Local commit
Recorded below after commit.

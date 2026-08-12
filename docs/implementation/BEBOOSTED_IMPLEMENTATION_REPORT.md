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
Recorded below after commit.

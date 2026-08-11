# BeBoosted Remaster Design Specification

**Date:** 2026-08-11

**Status:** Approved design direction

**Target:** Desktop-first, local-first application

## Product definition

BeBoosted is a calm, chatbot-assisted calendar planner that turns a universal inbox into a realistic daily or weekly schedule. The calendar is the product's primary working surface. AI helps capture, compare, prioritize, and schedule work, but begins in a review-first mode and always explains its decisions.

The intended experience is a planning desk, not a productivity dashboard.

## Primary outcomes

The remaster should make it easy to:

1. Capture unstructured tasks into one Inbox.
2. Ask the chatbot to plan Today, This week, or a custom period.
3. Resolve ambiguous priorities through adaptive, Beli-inspired task comparisons.
4. Review proposed calendar blocks directly on the calendar.
5. Approve, edit, complete, or replan work without punitive language.
6. Keep supporting documents, links, notes, and evidence in visually distinct Files inside broader Projects.
7. Trace AI claims and planning decisions back to their sources.

## Deliberate exclusions

The first remaster does not include:

- Habits, streaks, or habit dashboards
- Constellation or other goal-map visualizations
- A dashboard home screen
- Project progress percentages or project-health scores
- Social, cohort, or accountability features
- Dark mode
- Mobile-specific layouts
- Live external-calendar connections
- Required accounts or cloud synchronization
- Silent AI changes by default
- A global document library or nested knowledge-management system

Recurring work may exist as ordinary recurring tasks. It must not recreate a habit-tracking subsystem.

## Experience principles

### Calendar first

The calendar receives the majority of the viewport and remains usable without opening the Inbox, Projects, or chatbot.

### Progressive disclosure

Controls, metadata, AI reasoning, and resource details appear only when relevant. There is no permanent AI sidebar or collection of dashboard cards.

### Review before authority

AI-generated tasks and schedules begin as proposals. Users can later grant separate automatic permissions for task capture and calendar scheduling.

### Relative priority, objective constraints

User comparisons determine subjective importance. Deadlines, dependencies, durations, fixed commitments, and available time determine feasibility. Neither is allowed to impersonate the other.

### Calm accountability

The system reports what happened and offers a next action. It avoids guilt, streak loss, red-alert overload, and claims that elapsed time means work was completed.

### Provenance by default

AI-derived claims cite the exact project resource used. AI-generated tasks retain their origin. Schedule explanations distinguish user preference, calendar evidence, and model inference.

## Information architecture

There are exactly three primary destinations:

1. **Calendar** — Today and Week views; the first-time default is Today, then the application remembers the last view.
2. **Inbox** — one universal capture queue for unscheduled work.
3. **Projects** — deliberately simple contexts containing tasks, scheduled blocks, and Project Files.

The chatbot is a collapsed composer at the bottom of the application. It expands temporarily over the current surface.

## Domain model

### Task

Required:

- Title

Optional user-editable planning data:

- Estimated duration
- Deadline
- Project
- Scheduling constraints
- Recurrence

System-maintained data:

- Origin: user or AI
- Provenance links
- Completion state
- Scheduled block references
- Today rank or Week rank for the active planning result
- Planning tier: Protect now, Advance next, or Can wait

Urgency is derived for a selected planning period rather than stored as a permanent high/medium/low property.

### Project

A broad commitment such as DECA, College Admissions, or AP Economics. The first version contains only:

- Open and recently completed tasks
- Upcoming scheduled blocks
- Project Files
- An action to ask BeBoosted about the project

### Project File

The user-facing name is **File**. Internally, it is a curated reference collection rather than an operating-system folder. Examples include Event Prep and Metric Proof.

A File:

- Belongs to one Project
- Has a title, optional description, project accent, and resource count
- Contains a flat collection of resources
- Has no nested File hierarchy
- Can be linked from multiple tasks or scheduled blocks

### Resource

Supported resource types:

- Uploaded document
- Web link
- Short note
- Image or evidence item

Each resource records source metadata, added date, indexing state, and any derived task references.

### Calendar item

Every calendar item records an origin:

- BeBoosted-owned block
- Future external fixed commitment

The model reserves provider name, external identifier, synchronization state, edit capability, and conflict metadata even though external connections are deferred.

### Planning proposal

A proposal contains suggested calendar operations without mutating the approved calendar. Each operation retains its rationale, source constraints, and acceptance state.

### Comparison decision

A comparison records two tasks, planning period, selected winner, or a tie. **Too tough to decide** records a tie, makes neither task lose, and continues immediately.

## Core workflows

### 1. Capture

Users can type directly into the Inbox or speak naturally to the chatbot:

> Finish my DECA presentation before Friday. It probably needs two focused sessions.

BeBoosted may suggest title, deadline, duration, and project. By default, AI-generated tasks appear in a review list with Add all, edit, and dismiss actions. A setting can allow automatic addition, but every automatically created task receives an **AI added** label and retained provenance.

### 2. Request a plan

The user asks to plan Today, This week, or a custom period. BeBoosted evaluates:

- Candidate unscheduled tasks
- Approved calendar blocks
- Future fixed commitments
- Deadlines and dependencies
- Estimated durations
- User scheduling constraints
- Existing comparison history relevant to the period

### 3. Priority Sort

Priority Sort is a focused, full-screen comparison flow.

Daily prompt:

> If only one gets protected today, which should it be?

Weekly prompt:

> If only one moves forward this week, which should it be?

The user chooses the left task, right task, or **Too tough to decide**. The algorithm adaptively selects uncertain neighboring candidates and stops when it has adequate information. Users may exit early with **Build my plan now**.

The output uses period-specific ordinal ranks:

- Today rank: #1, #2, #3
- Week rank: #1, #2, #3
- Tied tasks share a rank

Ranks express ordering, not distance. The interface does not display decimal or 1–100 priority scores.

### 4. Review a schedule

AI proposals appear directly over the calendar as translucent lime blocks with dashed outlines. Users can:

- Drag or resize a proposal
- Remove it from the draft
- Approve one block
- Approve the entire visible plan
- Open **Why?** to inspect reasoning and sources

Approving the plan converts accepted proposals into solid BeBoosted calendar blocks. A short Undo window follows approval.

### 5. Complete or replan

Users mark tasks done directly on calendar blocks as they go. An elapsed block is never automatically completed.

Available outcomes:

- Done
- Needs more time
- Didn't happen

Needs more time may request the remaining duration and returns the task to the Inbox. Unresolved previous blocks create one quiet review notice during the next planning session. There is no mandatory Start My Day or End Day ritual.

### 6. Use Project Files

Opening a Project reveals upcoming tasks and a small collection of tactile File objects. Opening a File reveals its resources in a clean reading/list surface. A task or calendar block with related material exposes a small **Materials** link.

When a chatbot conversation or task is scoped to a Project, BeBoosted may retrieve from that Project's Files. It must cite the exact file, page, note, or link. It does not search unrelated Projects unless the user asks.

## Main screens

### Application shell

- Narrow icon rail: Calendar, Inbox, Projects
- Compact top bar: date navigation and Today/Week switch
- Large calendar canvas
- Collapsed chatbot composer along the bottom
- Temporary drawers and overlays instead of permanent secondary columns

### Today

- One continuous vertical timeline
- Current-time indicator
- Fixed commitments and BeBoosted blocks in one time model
- Optional, quiet capacity summary
- No morning/afternoon lanes or dashboard widgets

### Week

- Seven-column time grid
- Direct drag-and-drop scheduling from the Inbox drawer
- Fixed items, approved blocks, proposals, and conflicts have distinct states
- Last-used Today or Week view persists locally

### Inbox drawer

- Fast capture at the top
- Rows show task title and only useful metadata
- Primary action: Plan…
- Batch selection can begin Priority Sort or schedule selected work

### Priority Sort

- Two large comparison cards
- One clear prompt
- Left choice, right choice, Too tough to decide, and Build my plan now
- Minimal progress feedback; no streak, points, or leaderboard
- Keyboard support for left, right, tie, back, and exit

### Plan review

- Proposal summary in a small floating panel
- Calendar remains the review surface
- Approve plan is the primary action
- Why? reveals a short evidence trail, not chain-of-thought

### Project

- Project name and restrained accent
- Open/recent tasks
- Upcoming blocks
- File shelf
- Ask BeBoosted about this project
- No metrics dashboard

### Project File

- Tabbed folio header
- Flat resource list with type, source, date, and indexing state
- Preview/reading pane where appropriate
- Add document, link, note, or image
- Provenance links back to generated tasks and answers

### Settings

AI permissions are separated by action:

- Task capture: Review before adding / Add automatically and label AI added
- Calendar planning: Review every plan / Apply plans automatically
- External events: future read-only availability capability

Granting task-capture automation never grants calendar automation.

## Visual system

### Direction

**Academic Workbench** — a spacious, lightly tactile desktop planning environment inspired by annotated calendars, index tabs, and organized study material.

The calendar remains precise and digital. Project Files receive the only notable physical depth. This contrast is the visual signature.

### Palette

| Token | Value | Use |
|---|---:|---|
| Paper white | `#FCFCF8` | Calendar and reading surfaces |
| Workbench cream | `#F2EEDB` | Application background and secondary panels |
| Graphite | `#20231F` | Primary text and strong structure |
| Pencil gray | `#73776E` | Secondary text and metadata |
| Highlighter lime | `#C8F24A` | Selection, active controls, comparison feedback |
| Lime wash | `#EDF8C8` | AI proposals and quiet generated labels |

Lime is a highlighter beneath dark text, not a text color on white. Project accents should be muted and subordinate to lime.

### Typography

- Instrument Sans: navigation, task titles, chat, and controls
- IBM Plex Mono: times, durations, deadlines, ranks, and source metadata
- Newsreader: sparingly for Project File titles and reading contexts

### Geometry

- Fine graphite calendar rules
- Generous white space
- Modest radii; avoid pill-shaped containers as a default
- Flat calendar and list surfaces
- Subtle layered shadow and tab treatment only for Project Files

### Calendar states

- Future external commitment: cream-gray and visually locked
- Approved BeBoosted block: paper white with a project-colored edge
- AI proposal: lime wash with dashed graphite outline
- Conflict: graphite hatching or warning glyph rather than an alarming red panel

### Motion

Spend motion in one place: AI proposals appear like translucent highlighter strokes over available time. Approval settles them into solid calendar blocks; rejection softly erases them. Other motion should be functional and restrained. Respect reduced-motion preferences.

## Desktop interaction quality

- Design first for approximately 1440 × 960 and remain usable at 1280 × 800.
- All primary flows work with mouse and keyboard.
- Visible focus indicators use graphite plus lime without relying only on color.
- Hit targets remain comfortable even when the visual treatment is compact.
- Command/search access may be added, but must not replace visible navigation.
- Secondary information should use popovers, drawers, and peek panels that preserve calendar context.

## Local-first and integration-ready architecture

The first remaster requires no account and stores core state locally. Storage should be accessed through repository interfaces so a synchronized backend can replace or augment it later.

Calendar reads and writes should pass through a provider-neutral adapter. The initial provider is the local BeBoosted calendar. Future providers can expose separate capabilities such as read availability, create BeBoosted-owned events, update owned events, or detect conflicts.

Project-resource indexing must retain stable source identifiers and location metadata so citations survive reopening the application.

## Success criteria

The design succeeds when:

- A first-time user can capture a task and create a reviewed day plan without learning the full data model.
- The calendar remains visually dominant in Today and Week.
- The Inbox, chatbot, and Projects never permanently compress the calendar.
- A user can understand why any AI-proposed block exists.
- Too tough to decide preserves a tie and advances immediately.
- AI-generated tasks and blocks remain identifiable after approval.
- Project Files feel distinctive but clearly secondary to planning.
- Habits, Constellation, dashboards, and progress metrics do not reappear indirectly.
- Future calendar integration can be added without replacing the event model or UI state system.

## Design handoff

The mockup-generation prompt is in `docs/design/claude-ui-mockup-prompt.md`. The curated reference set is in `docs/design/reference-designs.md`.

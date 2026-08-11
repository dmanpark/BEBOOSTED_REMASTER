# Claude Design Prompt — BeBoosted Remaster

Copy everything below into Claude Design.

---

Act as a senior product designer designing a high-fidelity desktop application called **BeBoosted**. Create a coherent set of product mockups, not a marketing landing page.

## Product thesis

BeBoosted is a calm, chatbot-assisted calendar planner for ambitious students managing school, competitions, college applications, and personal commitments. Its job is to turn a messy universal inbox into a realistic daily or weekly schedule.

The product should feel like **a planning desk, not a productivity dashboard**.

The calendar is the primary interface. AI is a reviewable planning layer over the calendar, not a permanent chat destination and not an autonomous agent that silently changes the user's time.

## Platform and frame

- Desktop first; do not create mobile screens.
- Design primary frames at 1440 × 960.
- Ensure the system remains plausible at 1280 × 800.
- Light theme only.
- Treat this as a native-feeling desktop app, not a responsive website.

## Required information architecture

Use exactly three primary destinations:

1. Calendar
2. Inbox
3. Projects

Use a very narrow left icon rail. Do not use a wide permanent sidebar.

The top bar should contain compact date navigation and a Today/Week switch. The application remembers the previously used view; Today is the first-time default.

The chatbot begins as a collapsed composer spanning the bottom edge with placeholder copy such as **“Tell BeBoosted what you need…”**. It expands temporarily over the current screen. Do not give it a permanent right-hand column.

## Visual direction: Academic Workbench

Create a spacious, precise planning environment inspired by annotated calendars, index tabs, organized study material, and a well-kept academic workbench.

Do not make it nostalgic, scrapbook-like, excessively skeuomorphic, or beige-on-beige. The calendar should remain crisp and digital. Reserve tactile depth for Project Files.

### Color tokens

- Paper white `#FCFCF8` — calendar and reading surfaces
- Workbench cream `#F2EEDB` — app background and secondary panels
- Graphite `#20231F` — primary text and structure
- Pencil gray `#73776E` — secondary text and metadata
- Highlighter lime `#C8F24A` — selected states and decisive interaction feedback
- Lime wash `#EDF8C8` — AI proposals and generated labels

Use lime as a physical highlighter behind graphite text. Never use lime for small text on white. Allow muted per-project edge colors, but make them subordinate to the system lime.

### Typography

- Instrument Sans for navigation, controls, chat, and task titles
- IBM Plex Mono for times, dates, durations, ranks, deadlines, and source metadata
- Newsreader only for Project File titles and resource-reading moments

Typography should create hierarchy without oversized headings. Avoid generic SaaS hero typography.

### Shape and spacing

- Generous negative space
- Fine graphite calendar rules
- Modest corner radii
- Avoid pill-shaped containers unless the content is genuinely a compact status or filter
- Avoid a grid of dashboard cards
- Keep calendar and task-list surfaces mostly flat
- Give Project Files subtle layering, a labeled tab, and a restrained paper shadow

## Signature interaction

AI-proposed time blocks should resemble translucent lime highlighter strokes placed over available calendar time. They use lime wash, dark text, and a dashed graphite outline. Once approved, they settle into solid paper-white BeBoosted blocks with a thin project-colored edge.

This is the single expressive motion concept. Do not scatter decorative animation elsewhere.

## Create these high-fidelity screens

### Screen 1 — Today calendar

Show the default working surface:

- Narrow icon rail
- Compact top bar
- One continuous vertical timeline
- Current-time indicator
- Several fixed commitments and approved BeBoosted focus blocks
- Collapsed chatbot composer along the bottom
- No dashboard panels, habit metrics, progress rings, or motivational copy

Use realistic student content:

- AP Economics
- Lunch
- Practice DECA role-play
- Draft personal statement

Include an easy completion control directly on BeBoosted blocks.

### Screen 2 — Week calendar with Inbox drawer

Show a seven-column week grid with a temporary Inbox drawer floating over or sliding above part of the calendar without permanently changing the base layout.

Inbox rows should display only useful information:

- Task title
- Optional Project
- Deadline
- Estimated duration

Example tasks:

- Finish DECA presentation — DECA — Friday — 90 min
- Draft essay outline — College Admissions — Sunday — 60 min
- Review economics chapter — AP Economics — 45 min
- Email recommendation request — College Admissions — 10 min

Include a clear **Plan…** action and support dragging a task onto the week grid.

### Screen 3 — Priority Sort

Create a focused comparison screen with no surrounding calendar noise.

Prompt:

**“If only one moves forward this week, which should it be?”**

Show two large task cards:

- Finish DECA presentation — Friday — 90 min
- Draft essay outline — Sunday — 60 min

Actions:

- Select left task
- Select right task
- Too tough to decide
- Build my plan now
- Back

The interface should make clicking either task satisfying, then imply that another adaptive comparison will appear. **Too tough to decide** records a tie and continues; it is not an error or cancellation.

Show restrained progress such as “Comparison 3” without points, streaks, or gamification chrome.

### Screen 4 — Week plan draft and review

Show the week calendar with several translucent lime proposed blocks over open time.

Include a compact floating summary:

**Plan draft · This week**

4 blocks proposed · 3 tasks scheduled · 1 remains flexible

Actions:

- Review on calendar
- Approve plan

One selected proposal should expose a lightweight detail popover with:

- Drag/resize affordance
- Remove from draft
- Approve this block
- Why?

The Why panel should show a concise evidence trail:

- Deadline: Friday
- Duration: two 90-minute sessions
- Priority: ranked above economics review
- Availability: first uninterrupted open block
- Source: Event Guidelines.pdf, page 4

Do not reveal chain-of-thought. Show evidence and user-relevant rationale only.

### Screen 5 — Simple Project view

Show **College Admissions** as a deliberately sparse Project:

- Open and recently completed tasks
- Upcoming scheduled blocks
- A Files section
- Ask BeBoosted about this project

Do not include completion percentages, health scores, charts, milestones dashboards, or kanban boards.

Show two distinctive File objects:

- Metric Proof — 12 resources
- Essay Research — 7 resources

Files should look like restrained tabbed folios with a slight layered edge. This is the product's memorable visual motif, so refine it carefully without turning it into cartoon skeuomorphism.

### Screen 6 — Open Project File

Open the **Metric Proof** File inside College Admissions.

Show a flat resource collection containing examples such as:

- Transcript.pdf
- DECA State Finalist Certificate.pdf
- Volunteer Hours Verification
- SAT Score Report link
- Leadership metrics note

Support document, link, note, and image resource types. Show type, source, date added, indexing state, and a preview/reading pane where useful.

Include provenance relationships such as:

- Used by “Update activities list”
- Cited in a recent AI answer

Provide Add document, Add link, Add note, and Add image without adding nested folders.

### Screen 7 — AI task review list

Show the chatbot proposing several unscheduled tasks extracted from a conversation or Project File.

Each row can be edited or dismissed. Provide **Add all** as the primary action. Clearly mark the proposed origin without making the list look dangerous.

Include a subtle note that automatic addition can be enabled in Settings and that auto-added tasks retain an **AI added** label.

### Screen 8 — AI permissions settings

Show three clearly separated permission groups:

Task capture:

- Review before adding
- Add automatically and label AI added

Calendar planning:

- Review every plan
- Apply plans automatically

External events:

- Future read-only availability capability, visibly unavailable or “Coming later”

Make it obvious that granting automatic task capture does not grant automatic calendar changes.

## Calendar state language

Use these four visually distinct states:

- Fixed/future external commitment: cream-gray and visually locked
- Approved BeBoosted block: paper white with project-colored left edge
- AI proposal: lime wash with dashed graphite outline
- Conflict: graphite hatching or a warning glyph, not a bright red error card

## Ranking behavior

The product uses period-specific ordinal ranks instead of artificial decimal scores:

- Today rank #1, #2, #3
- Week rank #1, #2, #3
- Tied tasks share a number

Show result groups titled:

- Protect now
- Advance next
- Can wait

The ranks apply only to the current planning period and can change later.

## Completion and rollover

Users mark work Done, Needs more time, or Didn't happen. Do not automatically complete a task because its calendar block elapsed. When appropriate, show one quiet notice such as:

**“2 previous blocks need an outcome. Review”**

Do not use punitive overdue language, broken streaks, guilt, or celebratory confetti.

## Provenance rules

- Project-scoped AI may search only that Project's Files unless the user requests otherwise.
- AI answers cite the exact file, page, note, or link.
- AI-generated tasks and calendar blocks retain visible origin labels.
- Selecting a citation should plausibly open the source in context.
- If a source changes or disappears, the derived item can be marked Needs review.

## Desktop interaction requirements

- Provide visible keyboard focus states.
- Include plausible hover, selected, disabled, drag, conflict, and empty states.
- Keep hit targets comfortable.
- Drawers and popovers should preserve calendar context.
- A command/search interface may exist as a shortcut, but visible navigation must remain sufficient.
- Respect reduced motion.

## Hard exclusions

Do not add:

- Habits or streaks
- Constellation imagery
- Goal maps
- Dashboard home
- Productivity analytics
- Progress rings
- Social feeds
- Team collaboration
- Mobile screens
- Dark mode
- Wide permanent sidebars
- Permanent chatbot panel
- Nested folders
- Global resource library
- Excessive rounded cards
- Gradients, glassmorphism, neon glows, or generic purple AI styling
- Lime text on white
- Unrequested marketing copy

## Final deliverable

Produce:

1. The eight high-fidelity desktop frames listed above.
2. A small component sheet containing navigation, task rows, calendar blocks in all states, File objects, comparison cards, buttons, source citations, rank markers, and permission controls.
3. A concise interaction flow connecting Capture → Priority Sort → Plan draft → Approve → Complete/Replan.
4. Notes explaining spacing, typography, state transitions, keyboard behavior, and how the layout remains calm at 1280 × 800.

Use the supplied references as pattern inspiration only. Do not clone any one product. The final result must read unmistakably as BeBoosted's white/cream/lime Academic Workbench.

---

# BeBoosted — Mock First-Days Dogfooding Diary

**Date of run:** 2026-09-10
**Profile:** `%TEMP%\bb-mock-day1` (fresh, empty at start; one continuous profile across all three simulated days)
**Binary:** `src\BeBoosted.Desktop\bin\Release\net10.0\BeBoosted.exe` (Release, not rebuilt)
**Capture model under test:** Ollama, `qwen2.5:7b-instruct`, `http://localhost:11434` (confirmed running throughout unless noted)
**Persona:** high-school student — DECA, college applications, nonprofit volunteering

This is a running diary, written as the session happened rather than reconstructed afterward. The session itself was interrupted twice by pauses unrelated to BeBoosted (the machine sleeping; a later pause mid-way through the Ollama-outage test) — both are folded into the diary below as real observations (a relaunch-persistence check, and a change of method for the fallback test) rather than smoothed over.

---

## Day 1 — first run and switching the model on

### 1. Empty state — first impression

Screenshot: `day1-empty-state.png`

Launched on the blank profile. The Today view shows "Today's tasks — 0 of 0 complete", a SCHEDULED section ("Nothing scheduled for this day." + "+ Add task") and an UNSCHEDULED section (same pattern), plus the composer bar pinned at the bottom ("Tell BeBoosted what you need…", with a "Ctrl+J" hint).

**Honest first impression:** it's reasonably obvious what to do first — there are two explicit "+ Add task" affordances and an inviting composer placeholder. But nothing on this screen hints that the composer understands free-text, multi-task natural language, or that there's a whole AI-capture system with a model picker sitting in Settings. A first-time user would have to already know to type a paragraph into that box, or to go digging in Settings, to discover the app's headline feature. There's no onboarding tooltip, sample prompt, or "try typing a sentence like…" nudge anywhere on this screen.

(An unrelated Steam updater dialog happened to be on top of part of this screenshot — desktop environment noise, not a BeBoosted issue.)

### 2. Settings → Capture model

Screenshots: `day1-settings-capture-card-before.png`, `day1-settings-capture-card-scrolled.png`, `day1-settings-ollama-selected.png`

Settings is a single long scrolling page: AI permissions (Task capture: Review-before-adding vs Add-automatically; Calendar planning: Review-every-plan vs Apply-automatically) → External events (disabled, "Coming later") → **Capture model** → About. The Capture model card is the fourth section down and needed 8 "Page down" presses to bring fully into view from the top — a genuinely long scroll for what is the feature this whole exercise is about.

Selected the **Ollama capture** radio. Effects were immediate, no save/confirm step needed:
- Two new fields appeared inline: **ENDPOINT** = `http://localhost:11434`, **MODEL** = `qwen2.5:7b-instruct` — both pre-filled correctly to match the actually-running local Ollama instance.
- A one-line consent note appeared under the radio group: *"Your message goes to the model running on this computer. Nothing leaves it."* Clear, plain language, sits right where you're looking.
- The About card's **DATA LOCATION** field confirmed `...\Temp\bb-mock-day1` — a good sanity check that this session was never touching the owner's real profile.

**Verdict on this step:** the consent text and endpoint/model fields make sense and are easy to read. What's *not* clear is confirmation that the change "took" — there's no toast, no "saved" state, just the visual fact that a new field group appeared. For a permissions-relevant setting (data leaving vs. not leaving the machine) a slightly more assertive confirmation would be reassuring.

### 3–6. First capture

**Sent (verbatim):**
> Finish my DECA presentation before Friday. It probably needs two focused sessions. Also I still owe Ms. Rivera the rec request email.

**Mechanical note:** the composer has no visible Send button at all (confirmed by enumerating every Button/Edit in the window) — submission is Enter-key only. Enter had to be delivered as a Win32 message posted directly to BeBoosted's own window handle (obtained from its own PID), never a coordinate click or global key injection, so it could never land on another window.

**Timing:** measured cleanly (re-sent once after an internal tooling check to get a clean stopwatch) — **15.17 seconds** from send to the response/drafts appearing.

**What came back:** the chat literally said **"Parsed locally — Ollama couldn't be reached."** and fell back to the built-in rule-based parser. It returned **3 drafts**:

| Title | Duration | Deadline | Project |
|---|---|---|---|
| Finish my DECA presentation | 1 h 30 min | Fri | — |
| It probably needs two focused sessions | 3 h | — | — |
| Ms. Rivera the rec request email | 10 min | — | — |

**BB-QA-003 verdict: 3 drafts, not 2.** The elaborating sentence ("It probably needs two focused sessions") was **not** folded into the DECA task — it became its own standalone fragment task, exactly the known built-in-parser bug the scenario describes. Ollama never got to run: the model backend never responded inside the 15-second window, so the model-based 2-draft behavior was never exercised on this capture.

**Is "couldn't be reached" accurate?** No. I independently timed a raw `POST /api/generate` call to the same Ollama instance with a comparably-sized prompt: it took **13.69 seconds** to answer — meaning Ollama *was* reachable and *did* respond, just slowly, close enough to the 15 s cutoff that it lost the race. The in-app message conflates "connection refused" with "timed out"; they are different failure modes and the copy should say so (see Friction log).

Accepted all 3 via "Add all 3." All three landed in the Inbox and Today's Unscheduled list labeled "AI added." Screenshot: `day1-inbox.png`.

---

## Day 2 — projects and planning

### 7. Project + File

Created project **"College Apps"** (Projects → "Create a new project" → Edit "Project name" → Create). First attempt at the dialog failed because the name field didn't render fast enough for a ~500 ms wait — needed to poll up to ~1 s. Minor friction: the "New project" flyout felt a beat slower to appear than everything else in the app.

Created a File inside it, **"Common App Checklist"**. Naming friction: the fields are labelled **"File title"** and **"File description"**, not "File name" — an easy first guess to get wrong if you're not reading carefully.

**Bug found:** on the freshly-created File's detail page, the left pane correctly says "0 resources / Nothing collected yet," but the **right-hand panel still renders a full resource-detail template** — "Open in browser," "Open," "Reveal in folder," "Stored safely in BeBoosted's local library," a PROVENANCE section, "Rename," "Remove from File" — all with blank/empty values, for a resource that doesn't exist and isn't selected. It looks like a leftover empty-state of the resource viewer that should be hidden entirely. Screenshot: `day2-file-created.png`.

### 8. Second capture

**Sent (verbatim):**
> Need to draft my Common App personal statement this week, maybe 3 hours total, and get the counselor recommendation form submitted by the 20th.

**Timing: 15.29 seconds.** Same fallback message: "Parsed locally — Ollama couldn't be reached."

**What came back:** **1 draft**, not two:

| Title | Duration | Deadline | Project |
|---|---|---|---|
| Need to draft my Common App personal statement this week, maybe 3 hours total, and get the counselor recommendation form submitted by the 20th | 3 h | — (none captured) | — (none captured, despite "College Apps" existing) |

This is the opposite failure from capture 1: instead of over-fragmenting, the built-in parser lumped **two distinct action items** (draft the personal statement; submit the counselor rec form) into a single task whose title is the entire run-on sentence. It picked up "maybe 3 hours total" as the duration but **completely dropped "by the 20th" as a deadline** — the accepted task has no due date at all. It also made no connection to the "College Apps" project that already existed. Screenshot: `day2-capture2-drafts.png`.

**Anomaly while accepting this draft:** I clicked "Add task" to accept it, then closed the chat panel. The personal-statement task never subsequently appeared anywhere (not Scheduled, Unscheduled, or Completed) — it seems to have been lost. Two plausible causes, and I can't fully distinguish them from the UI alone: (a) the label "Add task" is reused verbatim by several unrelated buttons elsewhere in the layout (row-level "Add scheduled/unscheduled task" buttons carry the same text), so an automated click risked hitting the wrong one; or (b) the chat panel is explicitly labeled **"temporary — collapses on close"**, and closing it while a draft is still pending silently discards that draft with no confirmation. Either way, (b) is a real, user-facing risk worth flagging on its own: if that label means what it says, a user who reviews a draft and then closes the panel before explicitly accepting or dismissing it loses the suggestion with no warning.

### 9. Scheduling and completing

- Scheduled **"Finish my DECA presentation"** via its row "Schedule" button. The flyout defaults **DATE to today** and **START to 1:30 PM / 90 min** — it does not default to the task's own Friday due date, which is a small mismatch a distracted user could miss (scheduling a work session for the right day requires noticing the date field, not just accepting defaults).
- Completed **"Ms. Rivera the rec request email"** via its "Mark … done" button; it moved into a collapsed "Completed" section with a strikethrough.
- Screenshot showing all three states at once (scheduled/unscheduled/completed): `day2-today-mixed-states.png`.

### 10. Priority Sort and "Plan my day"

**Priority Sort** (from the Inbox drawer): a clean pairwise-comparison screen — "If only one gets protected today, which should it be?" with two cards, keyboard hints (← left · → right · T tie · Backspace back · Esc exit). With 2 unresolved items it took exactly one comparison, then showed a clear result: "Your priorities are set" — **Protect now: #1 Finish my DECA presentation**, **Advance next: #2 It probably needs two focused sessions**, with a "Done" button. This is genuinely good, understandable UX. Screenshots: `day2-priority-sort.png`, `day2-priority-sort-after-choice.png`.

**Asked the chat to plan the day** ("Plan my day"): this came back **near-instantly** — well under a second, nothing like the 15-second capture path. Calendar planning is evidently a separate, non-LLM (or at least non-Ollama-gated) code path from task capture, and it shows: no timeout risk here at all. Result: *"I've drafted a plan on your calendar (2 blocks proposed · 1 task scheduled). Review the lime blocks and approve what fits."* It intelligently split the 3-hour "two focused sessions" task into **two separate 1.5-hour blocks** ("Session 1 of 2" at 3:00 PM, "Session 2 of 2" at 4:30 PM) — correctly honoring the "two sessions" phrasing from the original capture text. Approved via "Approve plan," which gave a clear "Plan approved · 2 blocks" confirmation plus an "Undo approval" escape hatch. Screenshots: `day2-plan-my-day.png`, `day2-after-plan-today.png`, `day2-plan-approved.png`.

Minor quirk noticed here: the "Today's tasks — X of Y complete" header counts each scheduled **block** (both plan sessions count separately) rather than each distinct task, so a single 2-session task inflates the denominator (it read "1 of 4" for what were really 3 distinct task entities).

---

## Session interruption and relaunch (folds in Day 3's persistence check early)

The app process was closed externally between Day 2 and Day 3 for an unrelated reason. Per instructions, this was treated as a genuine observation rather than quietly worked around: relaunched the **same** binary against the **same** profile (`BEBOOSTED_DATA_DIR = %TEMP%\bb-mock-day1`), targeting the new PID only.

**Cold start on a populated profile:** the window took **10.47 seconds** to appear at all, with "Today's tasks" rendering real data at **10.91 seconds**. This is a populated profile (a handful of tasks, one project, one File) — noticeably slower than the practically-instant empty-profile launch on Day 1. That gap is worth the owner's attention on its own, independent of anything AI-related.

**Persistence: everything survived correctly.**
- **Today/Calendar:** all 3 real tasks intact — "Finish my DECA presentation" (P1, scheduled 1:30 PM, AI added), "It probably needs two focused sessions" as its two approved plan blocks (P2, 4:30 PM and 3:00 PM, AI added), and "Ms. Rivera the rec request email" in Completed (1). Screenshot: `day3-relaunch-today.png`.
- **Projects:** "College Apps" survived with its File count correct ("0 open tasks · 1 File"). Screenshot: `day3-relaunch-projects.png`.
- **Settings:** the Ollama capture radio was still selected, with the endpoint (`http://localhost:11434`) and model (`qwen2.5:7b-instruct`) fields still correctly populated — the model choice is a durable setting, not something that resets per session. Screenshot: `day3-relaunch-settings.png`.
- As expected, the still-pending, never-explicitly-accepted "advisor conversation" draft from immediately before the interruption did **not** survive the restart — consistent with the chat panel's own "temporary — collapses on close" label. A full app restart is an even more final version of "close," so its disappearance here isn't a new finding on its own, just a confirmation that the label means what it says at the process level.

**Methodology note, not a BeBoosted finding:** the very first screenshot attempt after this relaunch captured an unrelated window instead of BeBoosted — a different application had focus on this desktop and my screen-capture (which grabs whatever is topmost in a screen region, not "whatever process I mean to capture") picked it up. That image was deleted immediately without being examined further or used, a stricter foreground-confirmation check was added before any further screenshot, and the shots above were re-taken and verified to actually show BeBoosted before being kept. Noted here only so the screenshot index above can be trusted; nothing about this reflects on the BeBoosted app itself.

---

## Day 3 — routine use and the failure path (in progress)

### 11. No-task capture

**Sent (verbatim):**
> Had a really good conversation with my advisor about which schools to focus on.

**Timing: 15.31 seconds.** Same fallback: "Parsed locally — Ollama couldn't be reached."

**What came back:** **1 draft** — titled literally the entire input sentence ("Had a really good conversation with my advisor about which schools to f[ocus on]…", truncated in the UI), with a fabricated **30-minute** duration, tagged "from your message."

**Is this sensible? No.** There is no task in that sentence — it's a reflective, past-tense journal note, not an action item. The built-in fallback parser has no concept of "no actionable task here"; it manufactures a task draft out of any input it's given, regardless of content. Given that Ollama has not once returned inside the 15-second window in this session, **every single capture so far — including this one with zero actionable content — has been auto-converted into a proposed task by the fallback.** Screenshot: `day3-no-task-capture.png`.

### 12. A messier multi-topic capture (my own invention)

**Sent (verbatim):**
> The nonprofit needs volunteer hours logged by Sunday, I keep meaning to reorganize my desk before finals season, and I should pick up poster board from the store for the DECA display this weekend.

This sentence deliberately mixes three things: a concrete deadline (log volunteer hours by Sunday), a vague, undated intention (reorganize my desk "sometime"), and a concrete errand (buy poster board, no hard date, "this weekend").

**Timing: 15.26 seconds.** Fifth capture in a row to land inside 0.15 s of the 15-second wall and fall back to "Parsed locally — Ollama couldn't be reached."

**What came back:** **1 draft only** —

| Title | Duration | Deadline | Project |
|---|---|---|---|
| The nonprofit needs volunteer hours logged | 30 min | Sun | — |

This is a **third distinct failure mode**, different from both Day 1/2 captures: the parser didn't over-fragment (capture 1) or merge-and-drop-the-deadline (capture 2) — it simply **stopped after the first clause and silently discarded the other two action items entirely.** The desk reorganization and the poster-board errand never appeared anywhere, not even as a bad or wrongly-worded draft — they just vanished with no indication anything was dropped. Screenshot: `day3-messy-capture.png`.

Across three different capture texts now, the built-in fallback parser has shown three different ways of getting multi-clause input wrong: over-split, under-split-and-drop-the-date, and truncate-and-silently-lose-items. There is no single consistent bug here so much as a parser that simply isn't equipped for compound sentences at all, in any direction.

### 13. Exercising the fallback on purpose

The scenario calls for physically stopping Ollama to force the fallback path. I started to do exactly that (`Stop-Process` on `ollama.exe`), but the local install runs a supervisor process ("ollama app") that silently respawns the server — so a plain process-kill doesn't produce a sustained outage, and mid-test the session was interrupted while Ollama was down. It was restored by the time I resumed. **To avoid ever depending on timing around stopping the owner's actual local tool again, I switched methods**: instead of touching the Ollama process, I went into Settings and changed the **Ollama endpoint** field from `http://localhost:11434` to `http://localhost:11439` — a port nothing listens on. This exercises the identical "can't reach the model" code path, is something a real user could trigger by fat-fingering the port, and can never leave the owner's own Ollama installation in a bad state no matter how the session goes.

**With the dead endpoint set, sent (verbatim):**
> Testing the fallback with a dead Ollama endpoint: email the volunteer coordinator this week.

**Timing: 4.43 seconds.** Notably faster than every prior capture. The chat showed the same **"Parsed locally — Ollama couldn't be reached."** notice, and it **did still return a usable draft**: "…email the volunteer coordinator" — 10 min, no title truncation issue this time. Screenshot: `day3-dead-endpoint-fallback.png`.

This is a genuinely useful contrast: a *true* connection failure (nothing listening) resolves in ~4.4 s, while a *slow-but-reachable* model consistently burns the full ~15 s before giving up. Both produce the exact same on-screen sentence, "couldn't be reached" — which is accurate for the first case and misleading for the second (see Friction log). The fallback mechanism itself, though, worked correctly and safely in both cases: no crash, no dead end, always a usable draft.

**Restored the endpoint** to `http://localhost:11434` in Settings (confirmed via the field's own value read back). Screenshot: `day3-endpoint-restored.png`. **Sent one more capture (verbatim):**
> Confirming capture works again after restoring the real Ollama endpoint: text the nonprofit coordinator about Saturday's shift.

**Timing: 15.28 seconds — fell back again.** This is the sixth consecutive real-endpoint capture attempt to land inside a third of a second of the 15-second wall (15.17, 15.29, 15.31, 15.26, then this 15.28). The capture *pipeline* recovered correctly and immediately once the correct endpoint was back (proof: the fast 4.43 s failure disappeared and the familiar ~15 s pattern returned) — but the *model itself* never once produced an in-time answer anywhere in this entire session. Screenshot: `day3-capture-after-restore.png`.

---

## Latency reality

| # | Capture | Result | Elapsed |
|---|---|---|---|
| 1 | DECA / Rivera (Day 1) | fallback, 3 drafts | 15.17 s |
| 2 | Personal statement / rec form (Day 2) | fallback, 1 draft | 15.29 s |
| — | "Plan my day" (Day 2, not a capture) | drafted plan | < 1 s |
| 3 | No-task / advisor chat (Day 3) | fallback, 1 bogus draft | 15.31 s |
| 4 | Messy multi-topic capture (Day 3) | fallback, 1 draft (2 items dropped) | 15.26 s |
| 5 | Dead-endpoint test (Day 3, port 11439) | fallback, 1 draft | **4.43 s** |
| 6 | Confirm-restored capture (Day 3, real endpoint) | fallback, 1 draft | 15.28 s |

**Six real-endpoint capture attempts. Six fallbacks. Five of them landed within 0.14 seconds of each other, right at the 15-second wall** (15.17, 15.29, 15.31, 15.26, 15.28 s). Not once did an actual Ollama-parsed response arrive in time. A direct, out-of-band timing test against the same Ollama endpoint (`qwen2.5:7b-instruct`, a comparably-sized prompt) took **13.69 seconds** for a real answer — this is not a broken connection, it's a **model that is only barely, unreliably faster than the app's own cutoff**, so it loses the race almost every time. The one genuinely fast failure (4.43 s) only happened when the endpoint pointed at nothing at all — proving the fallback mechanism itself is not what's slow; the 15-second wait is the app faithfully waiting out its own timeout on a model that was never going to make it.

**Bluntly: on this hardware, turning on Ollama capture does not turn on Ollama capture.** It turns on a consistent 15-second delay before the built-in parser runs anyway. Every capture in this entire three-day session — six for six — was in practice handled by the same built-in rule-based parser a user gets with the feature turned off, just 15 seconds slower and with a misleading "couldn't be reached" message on top. If this machine is at all representative of what the owner or an early user would run this on, the headline feature of this release is not usable as shipped — that's a product-level problem, not a fluke of one bad run.

---

## Friction log

Ordered roughly by how much it would cost a real user, worst first. Each entry says what I expected, what actually happened, and what it cost.

1. **Ollama capture is a 15-second tax that (on this hardware) always loses.** Expected: turning on the "better" capture model to get smarter parsing. Actual: every single capture — six for six — silently waited the full 15-second timeout and then ran the exact same built-in parser it would have run anyway, just much slower. Cost: the feature is strictly worse than leaving it off, on this machine, and nothing in the UI would tell a user that ahead of time.

2. **The fallback message actively misreports what happened.** Expected: "couldn't be reached" to mean a connection problem (Ollama not running, wrong port). Actual: in five of six cases the true cause was a timeout — Ollama was running and did eventually answer (confirmed independently: 13.69 s for a real response) — but the app shows the identical sentence it shows for a truly dead endpoint (confirmed side-by-side: a dead port fails in 4.4 s with the same wording as a live-but-slow model failing at 15 s). Cost: a user troubleshooting "why doesn't this work" will check whether Ollama is running, find that it is, and be stuck with no correct next step — the app is telling them the wrong problem.

3. **A reviewed-but-not-yet-accepted AI draft can vanish with no warning.** Expected: a draft I looked at and clicked "Add task" on would either be added or clearly still be sitting there. Actual: after clicking Add and then closing the chat panel, the task was nowhere — not Scheduled, not Unscheduled, not Completed. The panel does say "temporary — collapses on close" in small print, but nothing stops you from closing it mid-review, and nothing confirms whether your last action landed before you did. Cost: a genuine risk of silently losing a task a user believed they'd just added, with no error and no undo.

4. **The built-in fallback parser has no concept of "there's no task here."** Expected: a purely reflective, past-tense sentence ("Had a really good conversation with my advisor…") to produce no draft, or at least a low-confidence one. Actual: it manufactured a confident-looking task card with a fabricated 30-minute duration. Cost: given the fallback fires on effectively every capture on this hardware (see above), a user will accumulate junk tasks from ordinary conversational venting unless they catch and dismiss every single one.

5. **The same parser is inconsistent in three different ways across three sentences**, none of them right: it over-split one two-clause message into 3 drafts (turning an elaborating aside into its own fake task); it under-split a different two-clause message into 1 overloaded draft that silently dropped an explicit "by the 20th" deadline; and it truncated a three-item message down to 1 draft, silently discarding two whole action items with no indication anything was dropped. Cost: there's no way to predict, and no way to tell after the fact, whether a given capture actually got everything you said.

6. **No onboarding hint that the composer understands natural language at all**, or that Settings has a model picker. Expected some nudge, sample prompt, or tooltip the first time the empty Today screen renders. Actual: nothing — the composer's only guidance is its placeholder text and the "Ctrl+J" shortcut hint. Cost: a first-time user has to already know to type a paragraph into that box to discover the app's headline feature at all.

7. **The Capture model setting — the entire subject of this feature — is buried 4 sections and 8 "Page down" presses into a single long Settings scroll.** Cost: real friction finding the one control this whole exercise is about, on every visit to Settings.

8. **No visible Send button on the composer at all** (confirmed by enumerating every control in the window — there simply isn't one). Submission is Enter-only, and the sole visible hint ("Ctrl+J") tells you how to *jump to* the composer, not how to *submit from* it. Cost: genuinely undiscoverable without trial and error or being told.

9. **No confirmation that a settings change "took."** Expected some acknowledgment after switching to Ollama capture (a toast, a "saved," anything). Actual: the only signal is that new fields silently appeared. Cost: minor on its own, but this is a data-handling-relevant setting (whether messages leave the machine), and a slightly more assertive confirmation would be reassuring exactly where it matters most.

10. **No project auto-association**, even when a capture is unambiguously about an existing project. A message clearly about "College Apps" work was never linked to the "College Apps" project that already existed. Cost: manual re-filing work the "AI capture" framing implies shouldn't be necessary.

11. **The File detail page renders a fully-populated but completely empty resource-detail panel** — "Open in browser," "Open," "Reveal in folder," a Provenance section, "Rename," "Remove from File" — for a brand-new File with zero resources and nothing selected. Looks like a leftover empty-state that should be hidden. Screenshot: `day2-file-created.png`. Cost: cosmetic, but confusing on first look — it reads as if something is already there.

12. **Field-name mismatches from what you'd guess**: the File dialog's fields are "File title"/"File description," not "File name" — an easy first guess to get wrong.

13. **The Schedule flyout defaults to today's date**, not a task's own due date, even when that task has an explicit different deadline (DECA's flyout defaulted to Thursday for a task due Friday). Cost: low but real — a distracted user could schedule a work session for the wrong day by just accepting the defaults.

14. **Project/File creation dialogs sometimes needed noticeably longer than ~500 ms to render their input fields** on first open in a session — small, but a real, perceptible lag the first time each dialog type appears.

15. **The "X of Y complete" counter counts scheduled calendar blocks, not distinct tasks** — a single 3-hour task split into two calendar sessions by "Plan my day" inflated the total from 3 real tasks to 4, reading "1 of 4 complete" for what was genuinely 1-of-3 work done.

## What worked well

- **Priority Sort** is genuinely well designed: simple pairwise comparisons, clear keyboard hints (← / → / T / Backspace / Esc), an honest "ordinal ranks for this period only" caveat, and a legible, confidence-inspiring summary screen at the end ("Protect now" / "Advance next").
- **"Plan my day" is fast and smart.** Unlike task capture, it never touched the 15-second wall — it responded in well under a second every time — and it made a genuinely good call splitting a "two focused sessions" task into two separate 1.5-hour calendar blocks rather than one lump, correctly picking up on the phrasing of the original message.
- **Plan approval/undo is reassuring**: a clear "Plan approved · 2 blocks" confirmation plus a one-click "Undo approval" escape hatch.
- **The fallback mechanism itself never failed to deliver a usable draft**, across six different real-endpoint attempts plus the deliberate dead-endpoint test — whatever else is wrong with the timing or the parsing, the app never left the user with nothing, a crash, or a dead end. That reliability is worth crediting on its own.
- **The Ollama Settings card pre-fills correct, working defaults** — the endpoint and model shown actually match the real local install — and states the privacy consequence of the choice in one clear, plain sentence.
- **Data location is surfaced in Settings → About**, which made it trivial to independently verify, throughout this entire session, that nothing ever touched the owner's real profile.
- **Data persistence across a full app restart was flawless**: every accepted task, its schedule/priority/completion state, the created project, and the created File all survived a cold relaunch exactly as left, and the durable Ollama setting was still selected afterward too.
- **Accepted AI-suggested tasks are consistently and visibly labeled "AI added"** everywhere they appear (Today list, Inbox drawer, Scheduled/Completed sections) — good, honest provenance.

## Screenshot index

| File | Shows |
|---|---|
| `day1-empty-state.png` | Blank Today view, first launch |
| `day1-settings-capture-card-before.png` | Top of Settings (AI permissions), before scrolling to Capture model |
| `day1-settings-capture-card-scrolled.png` | Capture model card fully visible, with Ollama fields and consent text |
| `day1-settings-ollama-selected.png` | Ollama radio selected (pre-scroll state) |
| `day1-capture1-drafts.png` | First capture's fallback message + 3 drafts (DECA/sessions/Rivera) |
| `day1-drafts-accepted-chat.png` | Chat view right after accepting all 3 Day-1 drafts |
| `day1-inbox.png` | Inbox drawer after accepting all 3 Day-1 drafts |
| `day2-projects-empty.png` | Empty Projects list |
| `day2-project-create-dialog.png` | "New project" dialog, empty |
| `day2-project-create-dialog-filled.png` | "New project" dialog with "College Apps" typed in |
| `day2-project-created.png` | College Apps project detail page |
| `day2-file-create-dialog.png` | "New File" dialog filled in |
| `day2-file-created.png` | Common App Checklist File page — shows the empty ghost resource-panel bug |
| `day2-capture2-drafts.png` | Second capture's fallback + single overloaded draft |
| `day2-schedule-flyout.png` | Schedule flyout for the DECA task, defaults visible |
| `day2-today-mixed-states.png` | Today list with one scheduled, one unscheduled, one completed task |
| `day2-priority-sort.png` | Priority Sort pairwise comparison screen |
| `day2-priority-sort-after-choice.png` | Priority Sort result screen ("Your priorities are set") |
| `day2-plan-my-day.png` | Chat response after "Plan my day" |
| `day2-after-plan-today.png` | Today list showing the proposed plan draft (2 sessions) awaiting approval |
| `day2-plan-approved.png` | Plan-approved confirmation with Undo option |
| `day3-no-task-capture.png` | The "advisor conversation" sentence turned into a bogus 30-min task draft |
| `day3-messy-capture.png` | Messy 3-topic capture — only 1 of 3 items survived as a draft |
| `day3-dead-endpoint-set.png` | Settings showing the endpoint deliberately pointed at a dead port |
| `day3-dead-endpoint-fallback.png` | Fast (4.43 s) fallback + usable draft with the dead endpoint |
| `day3-endpoint-restored.png` | Settings showing the real endpoint restored |
| `day3-capture-after-restore.png` | Confirmation capture after restore — back to the ~15 s pattern |
| `day3-relaunch-today.png` | Today list after a full app restart — all data intact |
| `day3-relaunch-projects.png` | Projects list after restart — College Apps intact |
| `day3-relaunch-settings.png` | Settings after restart — Ollama capture still selected |

## What I could not do, and why

- **Never observed a genuine Ollama-parsed (non-fallback) draft, at all, in this entire session.** Six for six real-endpoint capture attempts timed out before the model answered. The scenario's expected "2 drafts, DECA folded together" model behavior (BB-QA-003's model-based comparison case) was never actually exercisable on this hardware — everything reported here about task quality is necessarily about the **built-in fallback parser**, because that is the only parser that ever actually ran.
- **Could not sustain an Ollama-process-level outage** for the fallback test as originally instructed, because the local install runs a supervisor ("ollama app") that automatically respawns the server — a plain `Stop-Process` doesn't produce a lasting outage. Substituted a dead-endpoint-in-Settings test instead (see step 13), which exercises the same code path without needing to touch the owner's actual Ollama install, and is arguably a more realistic user-triggerable scenario (a mistyped port) besides.
- **Did not attempt to distinguish precisely** why the Day 2 "personal statement" draft never persisted after being accepted (ambiguous same-labeled button vs. the chat panel discarding a pending draft on close) — both are plausible from the UI alone and I did not want to speculate past what I actually observed.
- One screenshot capture technique hazard was found and fixed mid-session (pixel-based screenshots can grab whatever window is topmost, not necessarily BeBoosted) — see the relaunch section above. All screenshots in the index above were verified to actually show BeBoosted before being kept; none of the discarded/incorrect captures were retained or examined further.

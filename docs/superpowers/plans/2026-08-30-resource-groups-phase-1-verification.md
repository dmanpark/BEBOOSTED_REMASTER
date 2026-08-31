# Resource Groups Phase 1 — Verification Record

Branch `feature/resource-groups-phase-1`. Base `9626197` (PR #1 merge). This record covers Task 8,
the integration gate. It states what was verified, how, and what was **not** verified.

Nothing has been pushed, no PR opened, nothing merged.

## Test results

Both configurations were restored, built and tested in a **matched** configuration. A Release restore
followed by a Debug `--no-restore` is not a valid sequence here: the desktop diagnostics package has
configuration-dependent assets, and the Release restore below did in fact restore a project the Debug
restore considered up to date.

| Run | `BeBoosted.Tests` | `BeBoosted.Desktop.Tests` | Build |
| --- | --- | --- | --- |
| Debug — restore, build, test | **533 passed, 0 skipped** | **543 passed, 3 skipped** | 0 warnings, 0 errors |
| Release — restore, build, test | **533 passed, 0 skipped** | **543 passed, 3 skipped** | 0 warnings, 0 errors |

The 3 desktop skips are pre-existing screenshot-capture tests, skipped when `BEBOOSTED_SCREENSHOT_DIR`
is unset. They predate this branch. No new skip was introduced. `git diff --check` is clean and the
working tree is clean.

Baseline at the branch point was 410 core / 497 desktop. The plan's stated 519/539 figures were
superseded during Task 8; the numbers above are the actual measured results, not inherited.

## Gate 1 — production dependency injection, migration, startup, and the full lifecycle

`tests/BeBoosted.Desktop.Tests/Composition/ResourceGroupsCompositionTests.cs` resolves the **real** DI
graph with `ValidateOnBuild` and `ValidateScopes`, applies the real embedded migrations, asserts the
startup pass did not defer the reconcile, then drives create project → create File → create group →
**import loose** → move into the group → delete group, asserting the stored bytes are gone at the end.
It also asserts the imported resource arrives **loose**, which is the only production-graph check of
phase 1's "no group-targeted import" rule.

It passed first time, so it was mutation-tested. Removing the group repository registration reddens it
inside container validation; mis-scoping that registration reddens it differently.

One blind spot was found and closed: removing the provenance invalidator originally left the test
green, because the service takes it as an optional parameter defaulting to null, so the container
honoured the default and silently injected nothing. The test now asserts, through the production graph,
that deleting a group flags a derived item for review — so that registration's removal reddens it.

## Gate 2 — see the table above

## Gate 3 — the rendered group header, and keyboard paths headless tests cannot reach

Verified against the **running app** on real Windows rendering, driven through UI Automation:

- Group headers render on the app's own paper rather than a stock Fluent grey card, with the app's
  chevron and focus treatment. The header repaint is also pinned by two automated tests that read the
  named template parts' painted colours across rest, hover and focus.
- **Focus after a move is held**, on the moved row. Before the Task 7 fix the focus manager reported
  nothing focused after a move. Confirmed live on the real backend, which is where it matters, since
  headless popups are window overlays rather than real popup roots.
- Move into a group, and the resulting focus, were exercised live. Move between and out of groups were
  exercised in the automated suite at both view-model and rendered-control level; live, the out-of-group
  path was exercised via Ungroup rather than the Move-to flyout.

## Gate 4 — the reading-pane title's accessible name

**Claim, stated precisely:** an automation-peer assertion was performed through a real UI Automation
client against the running application. The element with automation id `SelectedResourceTitle` reports
the name `Vocab list` — the actual resource title — and **no** element in the tree carries the literal
name `Selected resource`.

**Not performed:** no screen reader was run. This is not a screen-reader test, and no claim is made
about how any specific assistive technology announces the pane.

## Gate 5 — disposable-data verification

All destructive checks ran against a throwaway profile. `DefaultAppDataPaths.CreateDefault()` already
honours the `BEBOOSTED_DATA_DIR` environment variable, so an **already-supported override** was used
and no profile feature was added to this branch.

**The real library was never opened.** Confirmed afterwards: it still holds 9 documents and 5 projects,
its schema stops at migration **12**, it has no `resource_groups` table, and its database file's last
write predates this work.

Verified live in the disposable profile:

| Check | Result |
| --- | --- |
| Migration 13 applies to a clean profile | Applied once, at first launch, no warnings |
| A File with no groups | No group chrome, no loose header — invisible until used |
| Empty group | Renders with its header and a zero count |
| Group creation | Creates its directory immediately; segment persisted on the row |
| Two same-titled groups | Distinct persisted segments and distinct directories |
| Loose-section header | Absent when nothing is loose, present when something is |
| Move-to flyout | Lists both same-titled groups, omits the container already holding the resource |
| Move through the flyout | Landed in the correct group, verified by group id in the database |
| Group rename | New segment reserved and directory created; the sibling kept its own segment |
| Confirmed deletion | Prompt names the group and count; Keep left it intact; Confirm removed it |
| Ungroup | Resource preserved; with no groups left the File renders flat again |
| Restart persistence | Both groups survive, **including the empty one**, with membership and order |
| Second settled reconcile | **Zero moves** — no move was logged on any restart |

**Weaker than it looks, stated honestly:** the live zero-moves and restart checks were performed with a
**link**, which has no stored bytes. Byte movement on a settled reconcile is covered by the automated
suite against real SQLite and real disk, not by this live check.

## Gate 6 — whole-range review

An independent review of the full range examined transaction boundaries, post-commit isolation,
repository mappings, startup guards, adoption ownership, and selection/refresh behaviour. Nine seams
were verified as holding. One **Important** was found — see below.

Its scope audit confirmed all phase-1 non-goals are absent: no drag-and-drop handlers, no reorder
controls, no group parameter on resource-creation APIs, no empty-folder pruning, and no unrelated
reading-pane changes.

## The blocker found and fixed during this gate

**A File or Project rename could permanently strand a group's bytes.** The group work taught the
*adoption* probe about group directory claims; the **placement** path never learned about them. After a
rename the destination directories do not exist yet, so a loose extensionless document could be placed
*at a group's destination, as a file*. The grouped member then failed to create its directory, was
skipped, and stayed skipped on every later reconcile — a permanent split, silently, with recovery
requiring a manual group rename. Swapping two timestamps made it converge, so it was ordering-dependent
and nothing caught it. No test in the range renamed a File or Project while a group existed.

Fixed by moving the claim **onto the group row rather than the disk**: the two placement entry points
take a required claim set, placement skips a claimed name before consulting the disk, and the
already-placed check no longer blesses a file sitting on a claimed name — which is what lets an
already-stranded state heal rather than only be prevented. Because the claim is a row fact, it also
protects an **empty** group, which no ordering-based fix could.

Covered by regressions for both renames against both orderings, the empty-group case, same-titled
groups keeping distinct segments and exact contents, recovery from an already-stranded state, restart
with fresh repositories then zero moves, and failure isolation. Independently re-reviewed: prevention
established, recovery convergent (hand-traced, no oscillation), failure isolation correct with the
blast radius stopping at the affected File.

## Accessibility defect found live and fixed

The group header's expand/collapse toggle — the element Tab actually lands on — exposed the accessible
name `Avalonia.Controls.Grid`. The name set on the Expander reached the *Group* peer, not the toggle.
Found by a UI Automation sweep of the running app; invisible to any test that reads XAML.

Fixed so the header announces its title and item count. Verified live after rebuild: the two headers
report `Group Unit 5, 1 item` and `Group Unit 6 empty, 0 items`, tracking a move and a rename, with the
inner Rename, Ungroup and Delete buttons keeping their own names. Pinned by tests that consult the
toggle's automation **peer**, not the attached property.

## Not verified — manual verification requested

**Importing a document through the running app.** The file picker's controls expose no invokable
patterns to UI Automation — they resolve as panes with no patterns — so the dialog can only be driven
by synthesized input, which was ruled out as unsafe. The picker opens and cancels correctly; only
choosing a file could not be driven.

Consequently these were **not** verified live, though each is covered by the automated suite against
real SQLite and real disk:

- byte movement on a parent File or Project rename,
- an extensionless filename colliding with a group's folder segment,
- confirmed group deletion removing stored document bytes.

**Please verify these three by hand** in a disposable profile: set `BEBOOSTED_DATA_DIR` to a throwaway
directory, import two documents, name one to collide with a group's folder segment, rename the parent
File, and confirm a group deletion.

Also not verified live, carried from Task 7's reviews:

- real-popup focus scope for the Move-to flyout — whether Tab cycles inside the popup or escapes to
  the window, and where focus returns on close,
- scroll-offset behaviour on a File large enough to actually scroll,
- wheel passthrough from an inner list to the outer scroll container.

## Incident during live verification

While driving the GUI by absolute screen coordinates, a browser window took the foreground between two
steps and a click plus typed text landed in that browser's bookmark dialog.

**No persisted bookmark was found by the checks performed** — a read of each browser profile's
bookmarks file and their modification times. That is **not** proof that no browser state changed. No
further inspection of the browser was performed.

Coordinate-driven automation was then abandoned. A foreground assertion followed by synthesized input
still leaves a check-to-input race, so the remaining live checks were driven through UI Automation,
which resolves a **named element inside the application's own tree** and acts on it through its
patterns — no cursor, no z-order, no foreground dependency, and no possibility of input reaching
another application. Where that was not possible, the check was left unverified above rather than
approximated.

## Deferred, recorded, not fixed

- **Resource rows announce a view-model type name** to assistive technology. Verified **pre-existing**:
  the row template on `main` carried no accessible name either, so that peer has always fallen back to
  the type name. Out of scope for this branch.
- **Renaming or deleting a group leaves its now-empty directory behind.** Plan-sanctioned; folder
  pruning belongs to a dedicated storage-cleanup pull request covering Project, File and group folders
  under one policy.
- **Renaming a group whose claimed path is occupied by a file** still throws from the reservation's
  owned-segment branch. Reachable but narrow, nothing partially persists, and it already degrades to a
  notice rather than a crash. Fixing it would change that method's contract for five other callers.
- **Five refresh callers remain unguarded**, so a transient read failure behind a committed add,
  import, delete or rename can still escape as an unhandled exception. Pre-existing shape; this branch
  added one more read to it.
- **The row's select command** still writes the unguarded selection setter. Unbound today, guaranteed
  only by grep.
- **The core test project's temporary databases leak.** SQLite pools are keyed on the whole connection
  string, so clearing one keyed on the path alone misses the pool the app actually fills.
  Pre-existing, affects the whole core suite, unrelated to this feature.
- **Two pre-existing documentation-reference warnings** in the storage tests, invisible because no
  project generates a documentation file.

## Status

Phase 1 is implemented, reviewed and verified to the extent recorded above. Three live checks are
explicitly unverified and await manual confirmation. No push, no pull request, no merge.

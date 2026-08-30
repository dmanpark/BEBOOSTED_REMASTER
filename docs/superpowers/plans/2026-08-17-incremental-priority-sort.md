# Incremental Priority Sort Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a repeat Priority Sort ask only about newly captured tasks, and let any ranked task be re-placed on its own through a short comparison run.

**Architecture:** `ComparisonSession` already performs Beli-style binary insertion. Give it an explicit seed ordering next to its insert list and all three flows (first sort, re-sort, re-rank) become the same object with different arguments. Two service factory methods build the seed from the saved ranking; the shell picks which to call.

**Tech Stack:** .NET 10, C#, Avalonia 12, CommunityToolkit.Mvvm, xUnit, Avalonia.Headless, SQLite via `SqlitePrioritizationRepository`.

Spec: `docs/superpowers/specs/2026-08-17-incremental-priority-sort-design.md`

## Global Constraints

- No change to the comparison mechanic, to tiers, to dense ranking, or to the persisted rank format. No migration.
- Ranks stay period-scoped (`PlanningPeriod.Key`). A new day still gets a full first sort.
- No tier prefilter before comparisons.
- `BuildRanking()` is not modified. An abandoned re-rank saves nothing.
- Strict TDD: every production edit is preceded by a test watched failing for the right reason.
- Do not touch `docs/qa/` or screenshot baselines. The three screenshot-capture tests stay skipped.
- `dotnet format BeBoosted.slnx --verify-no-changes --no-restore` stays clean; build runs `-warnaserror`.
- Do not stage, commit, or push unless the user asks.

---

### Task 1: Seeded `ComparisonSession`

**Files:**
- Modify: `src/BeBoosted.Domain/Prioritization/ComparisonSession.cs:14-34` (fields, constructor), `:147-159` (`Replay`)
- Test: `tests/BeBoosted.Tests/Domain/ComparisonSessionTests.cs`

**Interfaces:**
- Produces: `ComparisonSession(PlanningPeriod, IEnumerable<IReadOnlyList<TaskId>> seedOrder, IEnumerable<TaskId> candidates)`. The existing two-argument constructor stays and delegates with an empty seed.

- [x] **Step 1: Write the failing tests**

Append to `tests/BeBoosted.Tests/Domain/ComparisonSessionTests.cs` before the closing brace:

```csharp
    // ---- Seeded sessions: insert into an existing order ----

    private static IReadOnlyList<IReadOnlyList<TaskId>> Seed(params TaskId[] ids)
        => [.. ids.Select(id => (IReadOnlyList<TaskId>)[id])];

    [Fact]
    public void SeededSession_WithNothingToInsert_AsksNothing_AndRanksTheSeed()
    {
        var ids = Ids(3);
        var session = new ComparisonSession(Period, Seed(ids[0], ids[1], ids[2]), []);

        Assert.True(session.IsComplete);
        var byTask = session.BuildRanking().ToDictionary(r => r.TaskId, r => r.Rank);
        Assert.Equal(1, byTask[ids[0]]);
        Assert.Equal(2, byTask[ids[1]]);
        Assert.Equal(3, byTask[ids[2]]);
    }

    [Fact]
    public void SeededSession_InsertingOneTask_PreservesTheExistingOrder()
    {
        var ids = Ids(4); // A, B, C already ranked in that order; D is new
        var session = new ComparisonSession(Period, Seed(ids[0], ids[1], ids[2]), [ids[3]]);

        // Bisect: D vs B (mid of [0,3)). D loses, so it sits below B.
        Assert.Equal((ids[3], ids[1]), session.CurrentComparison);
        session.Record(ComparisonResult.RightWins);
        while (!session.IsComplete)
        {
            session.Record(ComparisonResult.RightWins); // D keeps losing → lands last
        }

        var byTask = session.BuildRanking().ToDictionary(r => r.TaskId, r => r.Rank);
        Assert.Equal(1, byTask[ids[0]]);
        Assert.Equal(2, byTask[ids[1]]);
        Assert.Equal(3, byTask[ids[2]]);
        Assert.Equal(4, byTask[ids[3]]);
    }

    [Fact]
    public void SeededSession_KeepsTiedGroupsSharingAnOrdinal()
    {
        var ids = Ids(3);
        IReadOnlyList<IReadOnlyList<TaskId>> seed = [[ids[0], ids[1]]]; // A and B tied at #1
        var session = new ComparisonSession(Period, seed, [ids[2]]);

        session.Record(ComparisonResult.RightWins); // C loses to the tied group → below it

        var byTask = session.BuildRanking().ToDictionary(r => r.TaskId, r => r.Rank);
        Assert.Equal(1, byTask[ids[0]]);
        Assert.Equal(1, byTask[ids[1]]);
        Assert.Equal(2, byTask[ids[2]]);
    }

    [Fact]
    public void SeededSession_IgnoresACandidateAlreadyInTheSeed()
    {
        var ids = Ids(2);
        var session = new ComparisonSession(Period, Seed(ids[0], ids[1]), [ids[0]]);

        Assert.True(session.IsComplete); // the seed already places it — nothing to ask
        Assert.Equal(2, session.BuildRanking().Count);
    }

    [Fact]
    public void SeededSession_RejectsOnlyWhenSeedAndCandidatesAreBothEmpty()
    {
        Assert.Throws<DomainException>(() => new ComparisonSession(Period, [], []));

        var ids = Ids(1);
        var seeded = new ComparisonSession(Period, Seed(ids[0]), []); // legal
        Assert.True(seeded.IsComplete);
    }

    [Fact]
    public void SeededSession_SupportsUndo()
    {
        var ids = Ids(4);
        var session = new ComparisonSession(Period, Seed(ids[0], ids[1], ids[2]), [ids[3]]);
        var first = session.CurrentComparison;

        session.Record(ComparisonResult.LeftWins);
        Assert.True(session.Undo());

        Assert.Equal(first, session.CurrentComparison);
        Assert.Equal(0, session.AnsweredCount);
    }
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ComparisonSessionTests"
```
Expected: compile errors — no three-argument `ComparisonSession` constructor exists.

- [x] **Step 3: Implement**

In `src/BeBoosted.Domain/Prioritization/ComparisonSession.cs`, add a `_seed` field beside `_candidates`:

```csharp
    private readonly List<TaskId> _candidates;
    private readonly List<List<TaskId>> _seed;
```

Replace the constructor with these two:

```csharp
    public ComparisonSession(PlanningPeriod period, IEnumerable<TaskId> candidates)
        : this(period, [], candidates)
    {
    }

    /// <summary>
    /// Starts from an existing ordering (tied tasks grouped together) and places only
    /// <paramref name="candidates"/> into it. An empty seed is the from-scratch sort.
    /// Candidates already present in the seed are ignored — the seed places them.
    /// </summary>
    public ComparisonSession(
        PlanningPeriod period,
        IEnumerable<IReadOnlyList<TaskId>> seedOrder,
        IEnumerable<TaskId> candidates)
    {
        Period = period;
        _seed = seedOrder.Select(group => group.ToList()).Where(group => group.Count > 0).ToList();
        var seeded = _seed.SelectMany(group => group).ToHashSet();
        _candidates = candidates.Distinct().Where(id => !seeded.Contains(id)).ToList();
        if (_seed.Count == 0 && _candidates.Count == 0)
        {
            throw new DomainException("Priority Sort needs at least one task.");
        }

        Replay();
    }
```

Replace the first three lines of `Replay`:

```csharp
    private void Replay()
    {
        if (_seed.Count > 0)
        {
            _groups = _seed.Select(group => group.ToList()).ToList();
            _nextCandidateIndex = 0;
        }
        else
        {
            _groups = [[_candidates[0]]];
            _nextCandidateIndex = 1;
        }

        _inserting = null;
```

The rest of `Replay` and every other member is unchanged.

- [x] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ComparisonSessionTests"
```
Expected: all PASS, including the twelve pre-existing tests.

---

### Task 2: Service factories for incremental and re-rank sessions

**Files:**
- Modify: `src/BeBoosted.Application/Prioritization/PrioritySortService.cs`
- Create: `tests/BeBoosted.Tests/Prioritization/PrioritySortServiceTests.cs`

**Interfaces:**
- Consumes: the three-argument `ComparisonSession` constructor from Task 1.
- Produces: `PrioritySortService.StartIncrementalSession(PlanningPeriod, IReadOnlyList<TaskId>) -> ComparisonSession` and `PrioritySortService.StartRerankSession(PlanningPeriod, TaskId, IReadOnlyList<TaskId>) -> ComparisonSession`. `StartSession` is unchanged.

- [x] **Step 1: Write the failing tests**

Create `tests/BeBoosted.Tests/Prioritization/PrioritySortServiceTests.cs`:

```csharp
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Prioritization;
using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Prioritization;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Prioritization;

/// <summary>
/// A repeat sort asks only about unranked work, and a re-rank re-places one task,
/// both by seeding the session from the saved ordering. Verified against real
/// SQLite and re-read through fresh repositories.
/// </summary>
public sealed class PrioritySortServiceTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private static readonly PlanningPeriod Period = PlanningPeriod.ForToday(new DateOnly(2026, 8, 11));

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqlitePrioritizationRepository _ranks;

    public PrioritySortServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _ranks = new SqlitePrioritizationRepository(_database.Factory);
    }

    private PrioritySortService CreateService() => new(_ranks, _clock);

    /// <summary>Answers every question the session asks, always choosing the right card.</summary>
    private static void AnswerAll(ComparisonSession session, ComparisonResult result)
    {
        while (!session.IsComplete)
        {
            session.Record(result);
        }
    }

    /// <summary>Ranks re-read through a fresh repository — the state after a restart.</summary>
    private Dictionary<TaskId, int> RestartedRanks()
        => new SqlitePrioritizationRepository(_database.Factory)
            .GetRanks(Period.Key)
            .ToDictionary(r => r.TaskId, r => r.Rank);

    [Fact]
    public void StartIncrementalSession_WithNoSavedRanks_BehavesLikeAFullSort()
    {
        var ids = new[] { TaskId.New(), TaskId.New(), TaskId.New() };

        var session = CreateService().StartIncrementalSession(Period, ids);

        Assert.False(session.IsComplete); // every task still needs placing
        AnswerAll(session, ComparisonResult.RightWins);
        CreateService().Complete(session);
        Assert.Equal(3, RestartedRanks().Count);
    }

    [Fact]
    public void StartIncrementalSession_AsksOnlyAboutUnrankedTasks()
    {
        var ranked = new[] { TaskId.New(), TaskId.New(), TaskId.New() };
        _ranks.ReplaceRanks(Period.Key, [
            new PriorityRank(ranked[0], 1, PlanningTier.ProtectNow),
            new PriorityRank(ranked[1], 2, PlanningTier.AdvanceNext),
            new PriorityRank(ranked[2], 3, PlanningTier.CanWait),
        ]);
        var captured = TaskId.New();

        var session = CreateService().StartIncrementalSession(Period, [.. ranked, captured]);

        // Every question must involve the new task — the old three are never re-compared.
        var asked = 0;
        while (session.CurrentComparison is { } comparison)
        {
            Assert.Equal(captured, comparison.Left);
            asked++;
            session.Record(ComparisonResult.RightWins); // the new task keeps losing
        }

        Assert.True(asked <= 2); // log2(3 groups), not a full re-sort
        CreateService().Complete(session);
        var after = RestartedRanks();
        Assert.Equal(1, after[ranked[0]]);
        Assert.Equal(2, after[ranked[1]]);
        Assert.Equal(3, after[ranked[2]]);
        Assert.Equal(4, after[captured]);
    }

    [Fact]
    public void StartIncrementalSession_KeepsRanksOfTasksItDoesNotAskAbout()
    {
        var scheduled = TaskId.New();
        var inbox = TaskId.New();
        _ranks.ReplaceRanks(Period.Key, [
            new PriorityRank(scheduled, 1, PlanningTier.ProtectNow),
            new PriorityRank(inbox, 2, PlanningTier.AdvanceNext),
        ]);
        var captured = TaskId.New();

        var session = CreateService().StartIncrementalSession(Period, [scheduled, inbox, captured]);
        AnswerAll(session, ComparisonResult.RightWins);
        CreateService().Complete(session);

        // The already-scheduled task keeps its rank instead of being wiped.
        var after = RestartedRanks();
        Assert.Equal(1, after[scheduled]);
        Assert.Equal(2, after[inbox]);
    }

    [Fact]
    public void StartIncrementalSession_DropsRanksOfTasksThatAreNoLongerLive()
    {
        var alive = TaskId.New();
        var gone = TaskId.New();
        _ranks.ReplaceRanks(Period.Key, [
            new PriorityRank(gone, 1, PlanningTier.ProtectNow),
            new PriorityRank(alive, 2, PlanningTier.AdvanceNext),
        ]);
        var captured = TaskId.New();

        // `gone` was completed or deleted, so it is not in the live set.
        var session = CreateService().StartIncrementalSession(Period, [alive, captured]);
        AnswerAll(session, ComparisonResult.RightWins);
        CreateService().Complete(session);

        var after = RestartedRanks();
        Assert.DoesNotContain(gone, after.Keys);
        Assert.Equal(1, after[alive]);
    }

    [Fact]
    public void StartRerankSession_MovesOneTaskUp_AndLeavesTheRestInOrder()
    {
        var ids = new[] { TaskId.New(), TaskId.New(), TaskId.New() };
        _ranks.ReplaceRanks(Period.Key, [
            new PriorityRank(ids[0], 1, PlanningTier.ProtectNow),
            new PriorityRank(ids[1], 2, PlanningTier.AdvanceNext),
            new PriorityRank(ids[2], 3, PlanningTier.CanWait),
        ]);

        // Re-rank the last task and let it win everything → it becomes #1.
        var session = CreateService().StartRerankSession(Period, ids[2], ids);
        AnswerAll(session, ComparisonResult.LeftWins);
        CreateService().Complete(session);

        var after = RestartedRanks();
        Assert.Equal(1, after[ids[2]]);
        Assert.Equal(2, after[ids[0]]);
        Assert.Equal(3, after[ids[1]]);
    }

    [Fact]
    public void StartRerankSession_MovesOneTaskDown()
    {
        var ids = new[] { TaskId.New(), TaskId.New(), TaskId.New() };
        _ranks.ReplaceRanks(Period.Key, [
            new PriorityRank(ids[0], 1, PlanningTier.ProtectNow),
            new PriorityRank(ids[1], 2, PlanningTier.AdvanceNext),
            new PriorityRank(ids[2], 3, PlanningTier.CanWait),
        ]);

        var session = CreateService().StartRerankSession(Period, ids[0], ids);
        AnswerAll(session, ComparisonResult.RightWins); // the target keeps losing → last
        CreateService().Complete(session);

        var after = RestartedRanks();
        Assert.Equal(1, after[ids[1]]);
        Assert.Equal(2, after[ids[2]]);
        Assert.Equal(3, after[ids[0]]);
    }

    [Fact]
    public void StartRerankSession_NeverComparesTheTargetWithItself()
    {
        var ids = new[] { TaskId.New(), TaskId.New(), TaskId.New() };
        _ranks.ReplaceRanks(Period.Key, [
            new PriorityRank(ids[0], 1, PlanningTier.ProtectNow),
            new PriorityRank(ids[1], 2, PlanningTier.AdvanceNext),
            new PriorityRank(ids[2], 3, PlanningTier.CanWait),
        ]);

        var session = CreateService().StartRerankSession(Period, ids[1], ids);

        while (session.CurrentComparison is { } comparison)
        {
            Assert.Equal(ids[1], comparison.Left);
            Assert.NotEqual(ids[1], comparison.Right);
            session.Record(ComparisonResult.LeftWins);
        }
    }

    public void Dispose() => _database.Dispose();
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~PrioritySortServiceTests"
```
Expected: compile errors — `StartIncrementalSession` and `StartRerankSession` do not exist.

- [x] **Step 3: Implement**

In `src/BeBoosted.Application/Prioritization/PrioritySortService.cs`, after `StartSession`:

```csharp
    /// <summary>
    /// A sort that asks only about work with no rank yet: the saved ordering seeds the
    /// session, so answers place the new tasks among the existing ones. With no saved
    /// ranks this is exactly a full first sort.
    /// </summary>
    public ComparisonSession StartIncrementalSession(
        PlanningPeriod period, IReadOnlyList<TaskId> liveTasks)
    {
        var ranked = repository.GetRanks(period.Key).Select(r => r.TaskId).ToHashSet();
        return new ComparisonSession(
            period,
            SeedFor(period, liveTasks, exclude: null),
            liveTasks.Where(id => !ranked.Contains(id)));
    }

    /// <summary>Re-places one already-ranked task among the others.</summary>
    public ComparisonSession StartRerankSession(
        PlanningPeriod period, TaskId target, IReadOnlyList<TaskId> liveTasks)
        => new(period, SeedFor(period, liveTasks, exclude: target), [target]);

    /// <summary>
    /// The saved ranking as tied groups in rank order, pruned to tasks that are still
    /// live. Deleted and completed work never anchors a comparison.
    /// </summary>
    private List<IReadOnlyList<TaskId>> SeedFor(
        PlanningPeriod period, IReadOnlyList<TaskId> liveTasks, TaskId? exclude)
    {
        var live = liveTasks.ToHashSet();
        return repository.GetRanks(period.Key)
            .Where(r => live.Contains(r.TaskId) && r.TaskId != exclude)
            .GroupBy(r => r.Rank)
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<TaskId>)g.Select(r => r.TaskId).ToList())
            .ToList();
    }
```

- [x] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~PrioritySortServiceTests"
```
Expected: PASS.

---

### Task 3: The shell starts an incremental sort

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` (add `OpenTasks`)
- Modify: `src/BeBoosted.Desktop/ViewModels/PrioritySortViewModel.cs:44-67` (accept a prepared session)
- Modify: `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs:156-169` (`CanStartPrioritySort`, `StartPrioritySort`)
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/PrioritySortViewModelTests.cs`

**Interfaces:**
- Consumes: `StartIncrementalSession` from Task 2.
- Produces: `CalendarViewModel.OpenTasks -> IReadOnlyList<TaskItem>` (internal); a `PrioritySortViewModel` constructor taking a prepared `ComparisonSession`, used again by Task 4.

- [x] **Step 1: Write the failing tests**

Append to `tests/BeBoosted.Desktop.Tests/ViewModels/PrioritySortViewModelTests.cs` before the closing brace:

```csharp
    // ---- A repeat sort only asks about new work ----

    [Fact]
    public void SecondSort_OnlyAsksAboutNewlyCapturedTasks()
    {
        var shell = TestShell.Create();
        Capture(shell, "Essay outline");
        Capture(shell, "Vocab review");
        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseLeftCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);

        Capture(shell, "Lab report"); // the only unranked task
        shell.StartPrioritySortCommand.Execute(null);

        // Every question involves the new task; the settled pair is never re-asked.
        var sort = shell.ActiveSort!;
        var asked = 0;
        while (!sort.IsFinished)
        {
            Assert.Equal("Lab report", sort.LeftCard!.Title);
            asked++;
            sort.ChooseRightCommand.Execute(null);
        }

        Assert.True(asked is >= 1 and <= 2);
    }

    [Fact]
    public void StartPrioritySort_IsDisabled_WhenNothingIsNew()
    {
        var shell = TestShell.Create();
        Capture(shell, "Essay outline");
        Capture(shell, "Vocab review");
        Assert.True(shell.StartPrioritySortCommand.CanExecute(null));

        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseLeftCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);

        Assert.False(shell.StartPrioritySortCommand.CanExecute(null));
    }

    private static void Capture(ShellViewModel shell, string title)
    {
        shell.Inbox.CaptureText = title;
        shell.Inbox.CaptureCommand.Execute(null);
    }
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~PrioritySortViewModelTests"
```
Expected: `SecondSort_OnlyAsksAboutNewlyCapturedTasks` fails because the second sort re-asks about "Essay outline"/"Vocab review" (assertion on `LeftCard.Title`), and `StartPrioritySort_IsDisabled_WhenNothingIsNew` fails with Expected False / Actual True.

- [x] **Step 3: Implement**

In `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs`, beside the other internal accessors:

```csharp
    /// <summary>Every open task — the set eligible to hold a rank this period.</summary>
    internal IReadOnlyList<TaskItem> OpenTasks => _tasks.GetOpen();
```

In `src/BeBoosted.Desktop/ViewModels/PrioritySortViewModel.cs`, replace the constructor with a pair — the existing signature delegates, so present callers and tests keep working:

```csharp
    public PrioritySortViewModel(
        PlanningPeriod period,
        IReadOnlyList<TaskItem> candidates,
        PrioritySortService service,
        IClock clock,
        Action onClosed,
        Action<IReadOnlyList<PriorityRank>> onSaved)
        : this(
            period, candidates, service.StartSession(period, candidates.Select(t => t.Id)),
            service, clock, onClosed, onSaved)
    {
    }

    /// <summary>
    /// Runs a prepared session. <paramref name="knownTasks"/> must cover every task the
    /// session can name, seed anchors included, because they appear as comparison cards.
    /// </summary>
    public PrioritySortViewModel(
        PlanningPeriod period,
        IReadOnlyList<TaskItem> knownTasks,
        ComparisonSession session,
        PrioritySortService service,
        IClock clock,
        Action onClosed,
        Action<IReadOnlyList<PriorityRank>> onSaved)
    {
        _service = service;
        _clock = clock;
        _onClosed = onClosed;
        _onSaved = onSaved;
        _tasks = knownTasks.ToDictionary(t => t.Id);
        Period = period;
        _session = session;
        if (_session.IsComplete)
        {
            Finish();
        }
        else
        {
            RefreshQuestion();
        }
    }
```

In `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs`, replace `CanStartPrioritySort` and `StartPrioritySort`:

```csharp
    /// <summary>
    /// A sort is worth opening only when it would ask something: some live task has no
    /// rank yet, or the period has no ranking at all and there are at least two tasks.
    /// </summary>
    public bool CanStartPrioritySort
    {
        get
        {
            var live = Calendar.OpenTasks;
            var ranked = _prioritySort.GetRankLookup(CurrentPlanningPeriod);
            return ranked.Count == 0
                ? live.Count >= 2
                : live.Any(task => !ranked.ContainsKey(task.Id));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartPrioritySort))]
    private void StartPrioritySort()
    {
        var period = CurrentPlanningPeriod;
        var live = Calendar.OpenTasks;
        ActiveSort = new PrioritySortViewModel(
            period,
            live,
            _prioritySort.StartIncrementalSession(period, [.. live.Select(t => t.Id)]),
            _prioritySort,
            _clock,
            onClosed: () => ActiveSort = null,
            onSaved: _ =>
            {
                RefreshInboxRanks();
                StartPrioritySortCommand.NotifyCanExecuteChanged();
            });
    }
```

Add `using BeBoosted.Domain.Tasks;` to `CalendarViewModel.cs` if `TaskItem` is not already in scope (it is used elsewhere in the file, so it should be).

- [x] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~PrioritySortViewModelTests"
```
Expected: PASS. If `RanksInfluenceDraftOrdering` in `PlanDraftViewModelTests` fails, it is because that test finishes a sort and expects ranks — re-run the full desktop suite in Task 5's verification and fix any fallout there.

---

### Task 4: Re-rank mode in the sort surface, and the shell entry point

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/PrioritySortViewModel.cs` (re-rank mode)
- Modify: `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs` (`StartRerank`)
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/PrioritySortViewModelTests.cs`

**Interfaces:**
- Consumes: `StartRerankSession` from Task 2; the prepared-session constructor from Task 3.
- Produces: `PrioritySortViewModel.IsRerank -> bool`, `ShowBuildPlanNow -> bool`; `ShellViewModel.StartRerank(TaskId)` (internal), called by Task 5's row commands.

- [x] **Step 1: Write the failing tests**

Append to `tests/BeBoosted.Desktop.Tests/ViewModels/PrioritySortViewModelTests.cs`:

```csharp
    // ---- Re-ranking one task ----

    private static ShellViewModel SortedShell(params string[] titles)
    {
        var shell = TestShell.Create();
        foreach (var title in titles)
        {
            Capture(shell, title);
        }

        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseLeftCommand.Execute(null); // keeps capture order
        }

        shell.ActiveSort.CloseCommand.Execute(null);
        return shell;
    }

    private static string?[] RankedTitles(ShellViewModel shell)
        => [.. shell.Inbox.Tasks.OrderBy(r => r.RankText).Select(r => r.Title)];

    [Fact]
    public void Rerank_AsksOnlyAboutTheTargetTask_AndHidesTheEarlyExit()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var gamma = shell.Inbox.Tasks.Single(r => r.Title == "Gamma").Task.Id;

        shell.StartRerank(gamma);

        var sort = shell.ActiveSort!;
        Assert.True(sort.IsRerank);
        Assert.False(sort.ShowBuildPlanNow);
        Assert.Contains("Re-rank", sort.StatusLabel, StringComparison.Ordinal);
        Assert.Equal("Where does this belong now?", sort.Prompt);
        while (!sort.IsFinished)
        {
            Assert.Equal("Gamma", sort.LeftCard!.Title);
            Assert.NotEqual("Gamma", sort.RightCard!.Title);
            sort.ChooseLeftCommand.Execute(null); // Gamma wins everything → #1
        }

        Assert.Equal("Gamma", shell.Inbox.Tasks.Single(r => r.RankText == "#1").Title);
    }

    [Fact]
    public void AbandoningARerank_LeavesTheSavedRankingUntouched()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var before = RankedTitles(shell);
        var gamma = shell.Inbox.Tasks.Single(r => r.Title == "Gamma").Task.Id;

        shell.StartRerank(gamma);
        shell.ActiveSort!.ChooseLeftCommand.Execute(null); // answer one, then walk away
        shell.ActiveSort.CloseCommand.Execute(null);

        Assert.Null(shell.ActiveSort);
        Assert.Equal(before, RankedTitles(shell));
    }

    [Fact]
    public void Rerank_MovesATaskDown_LeavingTheOthersInOrder()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var alpha = shell.Inbox.Tasks.Single(r => r.Title == "Alpha").Task.Id;

        shell.StartRerank(alpha);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseRightCommand.Execute(null); // Alpha loses everything
        }

        Assert.Equal(["Beta", "Gamma", "Alpha"], RankedTitles(shell));
    }
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~PrioritySortViewModelTests"
```
Expected: compile errors — `ShellViewModel.StartRerank`, `PrioritySortViewModel.IsRerank`, and `ShowBuildPlanNow` do not exist.

- [x] **Step 3: Implement**

In `src/BeBoosted.Desktop/ViewModels/PrioritySortViewModel.cs`, add a final optional parameter to the prepared-session constructor and store it:

```csharp
        Action<IReadOnlyList<PriorityRank>> onSaved,
        bool isRerank = false)
```

and inside the body, before the `IsComplete` check:

```csharp
        IsRerank = isRerank;
```

Add the two members next to `Prompt`:

```csharp
    /// <summary>Re-placing one already-ranked task rather than sorting the queue.</summary>
    public bool IsRerank { get; private init; }

    /// <summary>
    /// A re-rank has no partial-save exit: an unfinished run must leave the saved
    /// ranking exactly as it was, and closing already saves nothing.
    /// </summary>
    public bool ShowBuildPlanNow => !IsRerank;
```

Replace `Prompt`:

```csharp
    public string Prompt => IsRerank
        ? "Where does this belong now?"
        : Period.Kind == PlanningPeriodKind.Today
            ? "If only one gets protected today,\nwhich should it be?"
            : "If only one moves forward this week,\nwhich should it be?";
```

In `RefreshQuestion`, replace the `StatusLabel` assignment:

```csharp
        StatusLabel = IsRerank
            ? $"Re-rank · {PeriodLabel} · Comparison {_session.ComparisonNumber}"
            : $"Priority Sort · {PeriodLabel} · Comparison {_session.ComparisonNumber}";
```

and in `Finish`:

```csharp
        StatusLabel = IsRerank
            ? $"Re-rank · {PeriodLabel} · {_session.AnsweredCount} comparisons"
            : $"Priority Sort · {PeriodLabel} · {_session.AnsweredCount} comparisons";
```

In `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs`, after `StartPrioritySort`:

```csharp
    /// <summary>
    /// Re-places one already-ranked task through the same comparison surface, seeded
    /// with the saved order minus that task. Abandoning it saves nothing.
    /// </summary>
    internal void StartRerank(TaskId taskId)
    {
        var period = CurrentPlanningPeriod;
        var live = Calendar.OpenTasks;
        if (!live.Any(task => task.Id == taskId))
        {
            return;
        }

        ActiveSort = new PrioritySortViewModel(
            period,
            live,
            _prioritySort.StartRerankSession(period, taskId, [.. live.Select(t => t.Id)]),
            _prioritySort,
            _clock,
            onClosed: () => ActiveSort = null,
            onSaved: _ =>
            {
                RefreshInboxRanks();
                StartPrioritySortCommand.NotifyCanExecuteChanged();
            },
            isRerank: true);
    }
```

Add `using BeBoosted.Domain;` to `ShellViewModel.cs` if `TaskId` is not already in scope.

- [x] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~PrioritySortViewModelTests"
```
Expected: PASS.

---

### Task 5: Rank chips become the re-rank entry point

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/TaskRowViewModel.cs` (constructor parameter, `RerankCommand`)
- Modify: `src/BeBoosted.Desktop/ViewModels/InboxViewModel.cs` (`RerankRequested`, `CreateRow`)
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs` (`CanRerank`, `RerankCommand`)
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs` (`Rerank` forwarder)
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` (`RerankRequested` event)
- Modify: `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs:64` (event wiring)
- Modify: `src/BeBoosted.Desktop/Views/InboxDrawerView.axaml:73-78`, `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml:254-259`
- Test: `tests/BeBoosted.Desktop.Tests/Ui/DailyListUiTests.cs`, `tests/BeBoosted.Desktop.Tests/ViewModels/PrioritySortViewModelTests.cs`

**Interfaces:**
- Consumes: `ShellViewModel.StartRerank` from Task 4.
- Produces: nothing consumed by later tasks.

- [x] **Step 1: Write the failing tests**

Append to `tests/BeBoosted.Desktop.Tests/ViewModels/PrioritySortViewModelTests.cs`:

```csharp
    [Fact]
    public void TheInboxRankChip_OpensARerank()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var row = shell.Inbox.Tasks.Single(r => r.Title == "Gamma");
        Assert.True(row.HasRank);

        row.RerankCommand.Execute(null);

        Assert.NotNull(shell.ActiveSort);
        Assert.True(shell.ActiveSort.IsRerank);
        Assert.Equal("Gamma", shell.ActiveSort.LeftCard!.Title);
    }

    [Fact]
    public void TheDailyPriorityMarker_OpensARerank()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        shell.Calendar.Reload();
        var row = shell.Calendar.Daily.UnscheduledRows.Single(r => r.Title == "Gamma");
        Assert.True(row.CanRerank);

        row.RerankCommand.Execute(null);

        Assert.NotNull(shell.ActiveSort);
        Assert.True(shell.ActiveSort.IsRerank);
        Assert.Equal("Gamma", shell.ActiveSort.LeftCard!.Title);
    }

    [Fact]
    public void AnUnrankedRow_OffersNoRerank()
    {
        var shell = SortedShell("Alpha", "Beta");
        Capture(shell, "Delta"); // captured after the sort — no rank yet
        shell.Calendar.Reload();

        var delta = shell.Calendar.Daily.UnscheduledRows.Single(r => r.Title == "Delta");
        Assert.False(delta.CanRerank);
        Assert.False(shell.Inbox.Tasks.Single(r => r.Title == "Delta").HasRank);
    }
```

Append to `tests/BeBoosted.Desktop.Tests/Ui/DailyListUiTests.cs`:

```csharp
    [AvaloniaFact]
    public void DailyRow_PriorityMarker_IsAButtonWhenRanked()
    {
        var (window, shell, tasks, _, clock) = CreateShellWindow();
        var task = TaskItem.Create("Ranked work", clock.Now, estimatedDuration: TimeSpan.FromMinutes(30));
        tasks.Add(task);
        var second = TaskItem.Create("Other work", clock.Now, estimatedDuration: TimeSpan.FromMinutes(30));
        tasks.Add(second);
        shell.Inbox.Reload();
        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseLeftCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var marker = window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("priorityMarker")
                && b.IsEffectivelyVisible
                && b.IsEnabled);
        Assert.NotNull(marker);
    }
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj \
  --filter "FullyQualifiedName~PrioritySortViewModelTests|FullyQualifiedName~DailyListUiTests"
```
Expected: compile errors — `TaskRowViewModel.RerankCommand`, `DailyRowViewModel.CanRerank`, and `DailyRowViewModel.RerankCommand` do not exist.

- [x] **Step 3: Implement the view models**

In `src/BeBoosted.Desktop/ViewModels/TaskRowViewModel.cs`, add a final primary-constructor parameter:

```csharp
    Action<TaskRowViewModel>? onEditRequested = null,
    Action<TaskRowViewModel>? onRerankRequested = null) : ViewModelBase
```

and a command beside `Edit`:

```csharp
    /// <summary>The rank chip: re-place this task among the others.</summary>
    [RelayCommand]
    private void Rerank() => onRerankRequested?.Invoke(this);
```

In `src/BeBoosted.Desktop/ViewModels/InboxViewModel.cs`, beside `EditRequested`:

```csharp
    public event Action<Domain.TaskId>? RerankRequested;
```

and in `CreateRow`, extend the construction:

```csharp
            onEditRequested: row => EditRequested?.Invoke(row.Task.Id),
            onRerankRequested: row => RerankRequested?.Invoke(row.Task.Id))
```

In `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs`, beside `CanReopen`:

```csharp
    /// <summary>Only a row that already holds a rank can be re-placed among the others.</summary>
    public bool CanRerank => Rank is not null && TaskId is not null;
```

and beside the other commands:

```csharp
    [RelayCommand]
    private void Rerank() => _owner.Rerank(this);
```

In `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs`, beside `EditRow`:

```csharp
    internal void Rerank(DailyRowViewModel row)
    {
        if (row.CanRerank && row.TaskId is { } taskId)
        {
            _owner.RequestRerank(taskId);
        }
    }
```

In `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs`, beside the `DataChanged` event:

```csharp
    /// <summary>Raised when a ranked row asks to be re-placed; the shell owns the surface.</summary>
    public event Action<TaskId>? RerankRequested;

    internal void RequestRerank(TaskId taskId) => RerankRequested?.Invoke(taskId);
```

In `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs`, beside the existing `Inbox.EditRequested` wiring at line 64:

```csharp
        Inbox.RerankRequested += StartRerank;
        Calendar.RerankRequested += StartRerank;
```

- [x] **Step 4: Implement the views**

In `src/BeBoosted.Desktop/Views/InboxDrawerView.axaml`, replace the rank `Border` with a button carrying the same chip look:

```xml
                      <Button Classes="rankChip" IsVisible="{Binding HasRank}"
                              Command="{Binding RerankCommand}"
                              AutomationProperties.Name="{Binding Title, StringFormat='Re-rank {0}'}"
                              ToolTip.Tip="Rank for the current planning period — click to re-rank">
                        <TextBlock Classes="mono" Text="{Binding RankText}"
                                   FontSize="{StaticResource TextSizeMeta}" FontWeight="SemiBold" />
                      </Button>
```

and add the style to that file's `<UserControl.Styles>` block:

```xml
    <Style Selector="Button.rankChip">
      <Setter Property="Background" Value="{DynamicResource BrushHighlighterLime}" />
      <Setter Property="BorderThickness" Value="0" />
      <Setter Property="CornerRadius" Value="0" />
      <Setter Property="Padding" Value="5,0" />
      <Setter Property="VerticalAlignment" Value="Center" />
    </Style>
    <Style Selector="Button.rankChip:pointerover">
      <Setter Property="BorderThickness" Value="1" />
      <Setter Property="BorderBrush" Value="{DynamicResource BrushGraphite}" />
    </Style>
```

In `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml`, replace the priority-marker `TextBlock` with:

```xml
          <!-- Priority marker — click to re-rank when the row carries a rank -->
          <Button Grid.Column="3" Classes="priorityMarker" VerticalAlignment="Center"
                  IsEnabled="{Binding CanRerank}"
                  Command="{Binding RerankCommand}"
                  AutomationProperties.Name="{Binding Title, StringFormat='Re-rank {0}'}"
                  ToolTip.Tip="{Binding PriorityAccessibleText}">
            <TextBlock Classes="mono" Text="{Binding PriorityText}"
                       FontSize="{StaticResource TextSizeMeta}" FontWeight="SemiBold"
                       Foreground="{StaticResource BrushGraphite}" />
          </Button>
```

and add to that file's `<UserControl.Styles>`:

```xml
    <Style Selector="Button.priorityMarker">
      <Setter Property="Background" Value="Transparent" />
      <Setter Property="BorderThickness" Value="0" />
      <Setter Property="Padding" Value="2,0" />
      <Setter Property="MinWidth" Value="0" />
    </Style>
    <Style Selector="Button.priorityMarker:pointerover">
      <Setter Property="Background" Value="{DynamicResource BrushRuleMedium}" />
    </Style>
    <Style Selector="Button.priorityMarker:disabled">
      <Setter Property="Opacity" Value="1" />
    </Style>
```

- [x] **Step 5: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj \
  --filter "FullyQualifiedName~PrioritySortViewModelTests|FullyQualifiedName~DailyListUiTests"
```
Expected: PASS.

- [x] **Step 6: Full verification**

Run, in order:
```bash
dotnet format BeBoosted.slnx --verify-no-changes --no-restore
dotnet build BeBoosted.slnx --no-restore -warnaserror
dotnet test BeBoosted.slnx --no-restore --no-build
git diff --check
```
Expected: format clean, 0 warnings / 0 errors, all tests pass (3 screenshot tests still skipped), `git diff --check` exit 0.

Any pre-existing test that finished a sort and then started another (for example `PlanDraftViewModelTests.RanksInfluenceDraftOrdering`) may now find the command disabled because nothing is unranked. That is the intended new behavior — adjust such a test to capture a new task first, or to assert the disabled state, rather than weakening the production rule.

---

## Notes for the implementer

- `ComparisonSession.Undo` is replay-from-answers, so it works against a seed with no extra code. Do not add seed-specific undo handling.
- `PrioritySortService.Complete` is unchanged. A seeded session's `BuildRanking()` already spans seed plus inserted tasks, so `ReplaceRanks` stays correct.
- `Close` never calls `Complete`, so abandoning a re-rank already saves nothing. The only change needed for that guarantee is hiding "Build my plan now".
- `DailyRowViewModel.Rank` is null for obligations by construction, so `CanRerank` needs no extra kind check beyond the null test.
- Re-ranking the only ranked task yields an empty seed and one candidate, which completes instantly at rank 1. No special case.
